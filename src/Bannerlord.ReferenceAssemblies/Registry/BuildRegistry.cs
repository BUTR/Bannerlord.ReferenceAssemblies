using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bannerlord.ReferenceAssemblies;

/// <summary>
/// builds/&lt;appId&gt;.json: every build of the app we know about, with the depot manifests needed to
/// download it. Steam only exposes the manifests of current branches, so this file is what lets past
/// builds be requested again.
/// </summary>
internal sealed class BuildRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new UtcDateTimeOffsetConverter() },
    };

    public uint AppId { get; set; }

    public List<BuildEntry> Builds { get; set; } = [];

    [JsonIgnore]
    public string FilePath { get; private set; } = "";

    public BuildEntry? Find(uint buildId) => Builds.FirstOrDefault(x => x.BuildId == buildId);

    public static BuildRegistry Load(string path, App app)
    {
        var registry = File.Exists(path)
            ? JsonSerializer.Deserialize<BuildRegistry>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException($"Registry {path} is empty")
            : new BuildRegistry { AppId = app.AppId };
        if (registry.AppId != app.AppId)
            throw new InvalidDataException($"Registry {path} belongs to app {registry.AppId}, expected {app.AppId}");
        registry.FilePath = path;
        Log.Info($"Registry {path}: {registry.Builds.Count} builds, {registry.Builds.Count(x => !x.HasVersion)} without version");
        return registry;
    }

    public void Save()
    {
        Builds = Builds.OrderBy(x => x.Date).ThenBy(x => x.BuildId).ToList();
        if (Path.GetDirectoryName(FilePath) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions) + "\n");
    }

    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }
}
