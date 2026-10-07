using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace Bannerlord.ReferenceAssemblies;

/// <summary>
/// What GOG tells anyone without a login: the newest public builds of a product, and each build's
/// manifest. Logging in shows no more builds; the rest of GOG's list sits in password-protected branches.
/// GOGDB keeps the public builds GOG has stopped listing, back to the game's arrival on GOG in March 2021.
/// </summary>
internal sealed class GogClient(HttpClient http)
{
    public const string GogDbUrl = "https://www.gogdb.org/data/products/{0}/product.json";

    private static readonly Regex RxOffsetWithoutColon = new(@"([+-]\d{2})(\d{2})$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The Windows builds GOG lists for the product right now, newest first.</summary>
    public async Task<List<GogBuildEntry>> GetListedBuildsAsync(ulong productId, CancellationToken ct)
    {
        using var json = await GetJsonAsync($"https://content-system.gog.com/products/{productId}/os/windows/builds?generation=2", ct);
        var builds = new List<GogBuildEntry>();
        foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
        {
            if (item.TryGetProperty("public", out var isPublic) && isPublic.ValueKind == JsonValueKind.False)
                continue;
            builds.Add(Entry(
                item.GetProperty("build_id").GetString()!,
                item.GetProperty("date_published").GetString()!,
                item.GetProperty("branch").GetString(),
                item.GetProperty("version_name").GetString(),
                MetaIdOf(item.GetProperty("link").GetString()!)));
        }
        return builds;
    }

    /// <summary>The Windows builds GOGDB has seen for the product, listed or not.</summary>
    public async Task<List<GogBuildEntry>> GetGogDbBuildsAsync(ulong productId, string urlFormat, CancellationToken ct)
    {
        using var json = await GetJsonAsync(string.Format(CultureInfo.InvariantCulture, urlFormat, productId), ct);
        var builds = new List<GogBuildEntry>();
        foreach (var item in json.RootElement.GetProperty("builds").EnumerateArray())
        {
            if (item.GetProperty("os").GetString() != "windows" || item.GetProperty("generation").GetInt32() != 2)
                continue;
            if (item.TryGetProperty("public", out var isPublic) && isPublic.ValueKind == JsonValueKind.False)
                continue;
            var id = item.GetProperty("id");
            builds.Add(Entry(
                id.ValueKind == JsonValueKind.Number ? id.GetUInt64().ToString(CultureInfo.InvariantCulture) : id.GetString()!,
                item.GetProperty("date_published").GetString()!,
                item.GetProperty("branch").GetString(),
                item.GetProperty("version").GetString(),
                item.GetProperty("meta_id").GetString()!));
        }
        return builds;
    }

    /// <summary>The content depots a build manifest lists, without GOG's own Galaxy depots.</summary>
    public async Task<List<GogDepot>> GetDepotsAsync(string metaId, CancellationToken ct)
    {
        using var json = await GetJsonAsync(MetaUrl(metaId), ct);
        return json.RootElement.GetProperty("depots").EnumerateArray()
            .Where(x => !(x.TryGetProperty("isGogDepot", out var gog) && gog.ValueKind == JsonValueKind.True))
            .Select(x => new GogDepot(x.GetProperty("productId").GetString()!, x.GetProperty("manifest").GetString()!, x.GetProperty("size").GetInt64()))
            .OrderBy(x => x.ProductId, StringComparer.Ordinal).ThenBy(x => x.Manifest, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Build manifests are addressed by their hash. The links GOG hands out name several CDN hosts, and
    /// some of the older ones no longer resolve, so the manifest is always asked of cdn.gog.com.
    /// </summary>
    public static string MetaUrl(string metaId) => $"https://cdn.gog.com/content-system/v2/meta/{metaId[..2]}/{metaId[2..4]}/{metaId}";

    internal static string MetaIdOf(string link) => link.TrimEnd('/')[(link.TrimEnd('/').LastIndexOf('/') + 1)..];

    /// <summary>GOG writes offsets as +0000, GOGDB as +00:00.</summary>
    internal static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(RxOffsetWithoutColon.Replace(value, "$1:$2"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();

    private static GogBuildEntry Entry(string buildId, string date, string? branch, string? label, string metaId)
    {
        var entry = new GogBuildEntry
        {
            BuildId = buildId,
            Date = ParseDate(date),
            Branch = string.IsNullOrEmpty(branch) ? null : branch,
            Label = label ?? "",
            MetaId = metaId,
        };
        entry.ParseLabel();
        return entry;
    }

    /// <summary>GOG serves build manifests zlib-compressed and everything else as plain JSON.</summary>
    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        var bytes = await http.GetByteArrayAsync(url, ct);
        if (bytes is [0x78, 0x01 or 0x5E or 0x9C or 0xDA, ..])
        {
            using var zlib = new ZLibStream(new MemoryStream(bytes), CompressionMode.Decompress);
            return await JsonDocument.ParseAsync(zlib, cancellationToken: ct);
        }
        return JsonDocument.Parse(bytes);
    }
}
