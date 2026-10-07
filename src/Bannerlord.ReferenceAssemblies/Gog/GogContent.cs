using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Bannerlord.ReferenceAssemblies;

/// <summary>GOG does not have a file of a build, or does not serve it any more.</summary>
internal sealed class GogContentMissingException(string message) : Exception(message);

/// <summary>
/// Downloads single files of a GOG build, which needs an account that owns the product. Manifests are
/// public, but their content chunks are served only through a signed link GOG hands to an owner. Signs
/// in as GOG Galaxy does, from a refresh token; the client id and secret are Galaxy's own, the same in
/// every copy of it.
/// </summary>
internal sealed class GogContent(HttpClient http, string refreshToken)
{
    private const string ClientId = "46899977096215655";
    private const string ClientSecret = "9d85c43b1482497dbbce61f6e4aa173a433796eeae2ca8c5f6129f2dc4de46d9";

    private string? _accessToken;
    private DateTimeOffset _accessTokenExpires;

    /// <summary>
    /// Whether the refresh token signs in and the account owns the product, so its files can be had. Returns
    /// null when it can, or why not.
    /// </summary>
    public async Task<string?> CheckAccessAsync(ulong productId, CancellationToken ct)
    {
        try
        {
            await GetAccessTokenAsync(ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return "GOG does not accept the refresh token";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
        {
            return $"GOG could not be asked whether the refresh token is good ({ex.Message})";
        }

        try
        {
            using var games = await GetJsonAsync("https://embed.gog.com/user/data/games", auth: true, ct);
            return games.RootElement.GetProperty("owned").EnumerateArray().Any(x => x.GetUInt64() == productId)
                ? null
                : $"the GOG account does not own product {productId}";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
        {
            return $"GOG could not be asked what the account owns ({ex.Message})";
        }
    }

    /// <summary>
    /// Downloads the files of the product's own depots in the build whose paths match the filters into
    /// <paramref name="directory"/>, and returns their paths. DLC depots are left out: they need a link of
    /// their own, and the version files are all in the game's.
    /// </summary>
    public async Task<List<string>> DownloadAsync(ulong productId, string metaId, IReadOnlyList<Regex> files, string directory, CancellationToken ct)
    {
        using var meta = await GetJsonAsync(GogClient.MetaUrl(metaId), auth: false, ct);
        var product = productId.ToString(CultureInfo.InvariantCulture);
        var depots = meta.RootElement.GetProperty("depots").EnumerateArray()
            .Where(x => x.GetProperty("productId").GetString() == product)
            .Select(x => x.GetProperty("manifest").GetString()!)
            .ToList();

        var links = await GetSecureLinksAsync(productId, ct);
        var written = new List<string>();
        foreach (var manifest in depots)
        {
            using var depot = await GetJsonAsync(GogClient.MetaUrl(manifest), auth: false, ct);
            var root = depot.RootElement.GetProperty("depot");
            byte[]? smallFiles = null;
            foreach (var item in root.GetProperty("items").EnumerateArray())
            {
                if (item.GetProperty("type").GetString() != "DepotFile")
                    continue;
                var path = item.GetProperty("path").GetString()!.Replace('\\', '/');
                if (!files.Any(x => x.IsMatch(path)))
                    continue;

                byte[] data;
                if (item.TryGetProperty("sfcRef", out var sfcRef))
                {
                    // Small files can be packed together into one container the depot lists apart.
                    smallFiles ??= await DownloadChunksAsync(links, root.GetProperty("smallFilesContainer").GetProperty("chunks"), ct);
                    data = smallFiles.AsSpan(sfcRef.GetProperty("offset").GetInt32(), sfcRef.GetProperty("size").GetInt32()).ToArray();
                }
                else
                {
                    data = await DownloadChunksAsync(links, item.GetProperty("chunks"), ct);
                }

                var target = Path.Combine(directory, path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, data, ct);
                written.Add(path);
            }
        }
        return written;
    }

    private async Task<byte[]> DownloadChunksAsync(List<(string Format, Dictionary<string, string> Parameters)> links, JsonElement chunks, CancellationToken ct)
    {
        using var file = new MemoryStream();
        foreach (var chunk in chunks.EnumerateArray())
        {
            var compressedMd5 = chunk.GetProperty("compressedMd5").GetString()!;
            var md5 = chunk.GetProperty("md5").GetString()!;
            var data = await DownloadChunkAsync(links, compressedMd5, ct);
            if (!Convert.ToHexStringLower(MD5.HashData(data)).Equals(md5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Chunk {compressedMd5} does not match its checksum.");
            file.Write(data);
        }
        return file.ToArray();
    }

    /// <summary>Tries each CDN GOG offers in turn. A chunk none of them has is missing, not a failed download.</summary>
    private async Task<byte[]> DownloadChunkAsync(List<(string Format, Dictionary<string, string> Parameters)> links, string compressedMd5, CancellationToken ct)
    {
        var suffix = $"/{compressedMd5[..2]}/{compressedMd5[2..4]}/{compressedMd5}";
        var missing = 0;
        Exception? last = null;
        foreach (var (format, parameters) in links)
        {
            var url = parameters.Aggregate(format, (current, x) => current.Replace($"{{{x.Key}}}", x.Key == "path" ? x.Value + suffix : x.Value, StringComparison.Ordinal));
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                {
                    missing++;
                    continue;
                }
                response.EnsureSuccessStatusCode();
                await using var zlib = new ZLibStream(await response.Content.ReadAsStreamAsync(ct), CompressionMode.Decompress);
                using var data = new MemoryStream();
                await zlib.CopyToAsync(data, ct);
                return data.ToArray();
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
            {
                last = ex;
            }
        }
        throw missing == links.Count
            ? new GogContentMissingException($"No GOG CDN has chunk {compressedMd5}.")
            : new HttpRequestException($"Chunk {compressedMd5} could not be downloaded.", last);
    }

    /// <summary>The signed CDN links an owner gets for the product, as URL templates with their parameters.</summary>
    private async Task<List<(string Format, Dictionary<string, string> Parameters)>> GetSecureLinksAsync(ulong productId, CancellationToken ct)
    {
        using var json = await GetJsonAsync($"https://content-system.gog.com/products/{productId}/secure_link?generation=2&_version=2&path=/", auth: true, ct);
        return json.RootElement.GetProperty("urls").EnumerateArray()
            .Select(x => (
                x.GetProperty("url_format").GetString()!,
                x.GetProperty("parameters").EnumerateObject().ToDictionary(y => y.Name, y => y.Value.ValueKind == JsonValueKind.String ? y.Value.GetString()! : y.Value.GetRawText())))
            .ToList();
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpires)
            return _accessToken;

        var query = $"client_id={ClientId}&client_secret={ClientSecret}&grant_type=refresh_token&refresh_token={Uri.EscapeDataString(refreshToken)}";
        using var json = JsonDocument.Parse(await http.GetStringAsync($"https://auth.gog.com/token?{query}", ct));
        _accessToken = json.RootElement.GetProperty("access_token").GetString()!;
        _accessTokenExpires = DateTimeOffset.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32() - 300);
        return _accessToken;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, bool auth, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (auth)
            request.Headers.Authorization = new("Bearer", await GetAccessTokenAsync(ct));
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes is [0x78, 0x01 or 0x5E or 0x9C or 0xDA, ..])
        {
            using var zlib = new ZLibStream(new MemoryStream(bytes), CompressionMode.Decompress);
            return await JsonDocument.ParseAsync(zlib, cancellationToken: ct);
        }
        return JsonDocument.Parse(bytes);
    }
}
