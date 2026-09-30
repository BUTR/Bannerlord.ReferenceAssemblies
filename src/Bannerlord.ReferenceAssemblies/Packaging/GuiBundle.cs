using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bannerlord.ReferenceAssemblies;

/// <summary>One published per-build GUI package that goes into the bundle, and how to read its files.</summary>
/// <param name="Build">The registry's entry for the build the package was packed from.</param>
/// <param name="PackageId">Its id: the base GUI package, or a DLC's.</param>
/// <param name="PackageVersion">Its version on the feed.</param>
/// <param name="ReadFiles">Its files under gui/, by path relative to gui/. Read when needed, so the packages are never all in memory at once.</param>
internal sealed record GuiBundleSource(BuildEntry Build, string PackageId, string PackageVersion, Func<IReadOnlyDictionary<string, byte[]>> ReadFiles);

/// <summary>The files of a bundle under gui/, by path relative to gui/, and the hash they carry.</summary>
internal sealed record GuiBundleContent(IReadOnlyDictionary<string, byte[]> Files, string ContentHash);

/// <summary>
/// The GUI.v3.All package: every format 3 GUI package of the newest build of each game version, with each
/// distinct record stored once. See gui-packages-v2-all.md.
///
/// gui/bundle.json lists every game version and its packages, and says what each of their files is made of.
/// Every top-level array of a format 3 file becomes a table, gui/records.&lt;file&gt;.&lt;property&gt;.jsonl, one
/// record per line, and every other top-level value is written inline. Every prefab node becomes a line of
/// gui/nodes.jsonl, with its child elements as line indexes. Nothing here names a format 3 file or property
/// except prefabs.json's entries, which say where each tree is and what it is called.
/// </summary>
internal static class GuiBundle
{
    /// <summary>The data format of the records, which the id names: GUI.v3.All.</summary>
    public const int FormatVersion = GuiPackager.FormatVersion;

    /// <summary>The layout of the bundle itself. Raised when the layout changes and the data does not.</summary>
    public const int Layout = 1;

    public static readonly string Module = $"{GuiPackager.BaseModule}.All";

    public const string IndexFile = "bundle.json";
    public const string NodesFile = "nodes.jsonl";

    /// <summary>The index of prefab trees, and its list: the one format 3 file the bundle has to understand.</summary>
    public const string PrefabIndexFile = "prefabs.json";
    public const string PrefabIndexList = "prefabs";

    /// <summary>
    /// Compact, with the encoder the format 3 files are written with, so that &lt;, &amp; and non-ASCII text stay
    /// as they are. The prefab trees nest past System.Text.Json's default depth of 64.
    /// </summary>
    internal static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        MaxDepth = 512,
    };

    internal static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 512 };

    /// <summary>
    /// The files as the same items the per-build packages give; the Package metadata tells the two apart. It lists
    /// only the top of gui/, .jsonl as well as .json, because format 3's recursive gui/**/*.json would miss the tables.
    /// </summary>
    internal static string Props(string packageId) =>
        $"""
         <Project>
           <ItemGroup>
             <BannerlordGameGuiData Include="$(MSBuildThisFileDirectory)../gui/*.json;$(MSBuildThisFileDirectory)../gui/*.jsonl" Visible="false" Package="{packageId}" />
           </ItemGroup>
         </Project>

         """.Replace("\r\n", "\n");

    public static string TableName(string file, string property) => $"records.{Path.GetFileNameWithoutExtension(file)}.{property}.jsonl";

    /// <summary>
    /// Builds the bundle. The packages are ordered here, not by the caller: game versions ascending, the base
    /// package before the DLC packages by id. That order numbers the lines, so the same packages always give
    /// the same bytes.
    /// </summary>
    public static GuiBundleContent Build(IEnumerable<GuiBundleSource> sources, string basePackageId)
    {
        var tables = new SortedDictionary<string, Table>(StringComparer.Ordinal);
        var nodes = new Table();
        var versions = new JsonArray();

        foreach (var version in Order(sources, basePackageId))
        {
            var packages = new JsonArray();
            foreach (var source in version)
                packages.Add(Package(source, tables, nodes));
            versions.Add(new JsonObject
            {
                ["gameVersion"] = version.Key,
                ["packages"] = packages,
            });
        }

        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [NodesFile] = nodes.ToBytes(),
        };
        foreach (var (name, table) in tables)
            files[name] = table.ToBytes();

        files[IndexFile] = Bytes(Index(versions, null));
        var hash = Hash(files);
        files[IndexFile] = Bytes(Index(versions, hash));
        return new GuiBundleContent(files, hash);
    }

    /// <summary>The packages by game version, in the order that numbers the lines.</summary>
    internal static IEnumerable<IGrouping<string, GuiBundleSource>> Order(IEnumerable<GuiBundleSource> sources, string basePackageId) =>
        sources
            .OrderBy(x => x.Build.Version!, GameVersionComparer.Instance)
            .ThenBy(x => !string.Equals(x.PackageId, basePackageId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.PackageId, StringComparer.Ordinal)
            .GroupBy(x => x.Build.Version!);

    private static JsonObject Index(JsonArray versions, string? hash)
    {
        var index = new JsonObject
        {
            ["formatVersion"] = FormatVersion,
            ["layout"] = Layout,
        };
        if (hash is not null)
            index["contentHash"] = hash;
        index["versions"] = versions.DeepClone();
        return index;
    }

    private static JsonObject Package(GuiBundleSource source, SortedDictionary<string, Table> tables, Table nodes)
    {
        var build = source.Build;
        var content = source.ReadFiles();
        var topLevel = content.Keys.Where(x => !x.Contains('/')).Order(StringComparer.Ordinal).ToList();
        var nested = content.Keys.Where(x => x.Contains('/')).ToHashSet(StringComparer.Ordinal);

        var files = new JsonArray();
        JsonArray? prefabs = null;
        foreach (var path in topLevel)
        {
            if (!path.EndsWith(".json", StringComparison.Ordinal))
                throw new InvalidDataException($"{source.PackageId} {source.PackageVersion}: gui/{path} is not JSON; the bundle only splits JSON objects.");
            if (Parse(content[path]) is not JsonObject document)
                throw new InvalidDataException($"{source.PackageId} {source.PackageVersion}: gui/{path} is not a JSON object.");

            var properties = new JsonArray();
            foreach (var (name, value) in document)
            {
                if (value is JsonArray { Count: > 0 } array)
                {
                    var tableName = TableName(path, name);
                    if (!tables.TryGetValue(tableName, out var table))
                        tables[tableName] = table = new Table();
                    var lines = array.Select(x => table.Intern(x?.ToJsonString(Json) ?? "null")).ToList();
                    properties.Add(new JsonObject { ["name"] = name, ["table"] = tableName, ["runs"] = Runs(lines) });
                }
                else
                {
                    // Scalars, and empty arrays: a table with no lines would be a file for nothing.
                    properties.Add(new JsonObject { ["name"] = name, ["value"] = value?.DeepClone() });
                }
            }
            files.Add(new JsonObject { ["path"] = path, ["properties"] = properties });

            if (path == PrefabIndexFile)
                prefabs = document[PrefabIndexList] as JsonArray;
        }

        var trees = new JsonArray();
        foreach (var entry in prefabs ?? new JsonArray())
        {
            var (name, module, file) = PrefabEntry(entry, source);
            if (!nested.Remove(file))
                throw new InvalidDataException($"{source.PackageId} {source.PackageVersion}: {PrefabIndexFile} lists gui/{file}, which is not in the package, or lists it twice.");
            if (Parse(content[file]) is not JsonObject tree
                || tree.Select(x => x.Key).SequenceEqual(["formatVersion", "name", "module", "root"]) is false
                || tree["formatVersion"]?.GetValue<int>() != FormatVersion
                || tree["name"]?.GetValue<string>() != name
                || tree["module"]?.GetValue<string>() != module
                || tree["root"] is not JsonObject root)
                throw new InvalidDataException($"{source.PackageId} {source.PackageVersion}: gui/{file} is not the tree of {module}/{name} that {PrefabIndexFile} describes, {{ formatVersion {FormatVersion}, name, module, root }}.");
            trees.Add(Intern(root, nodes, file, source));
        }
        if (nested.Count > 0)
            throw new InvalidDataException($"{source.PackageId} {source.PackageVersion}: {nested.Count} file(s) below gui/ are not trees {PrefabIndexFile} lists, such as gui/{nested.Order(StringComparer.Ordinal).First()}.");

        return new JsonObject
        {
            ["packageId"] = source.PackageId,
            ["packageVersion"] = source.PackageVersion,
            ["buildId"] = build.BuildId,
            ["changeSet"] = build.ChangeSet,
            ["date"] = build.Date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["branches"] = new JsonArray(build.Branches.Select(x => (JsonNode?) x).ToArray()),
            ["files"] = files,
            ["trees"] = trees,
        };
    }

    internal static (string Name, string Module, string File) PrefabEntry(JsonNode? entry, GuiBundleSource source) =>
        entry is JsonObject { } x && x["name"]?.GetValue<string>() is { } name && x["module"]?.GetValue<string>() is { } module && x["file"]?.GetValue<string>() is { } file
            ? (name, module, file)
            : throw new InvalidDataException($"{source.PackageId} {source.PackageVersion}: an entry of {PrefabIndexFile} has no name, module or file.");

    /// <summary>A node's line: the node as it is, with each child element replaced by its own line index. Children first.</summary>
    private static int Intern(JsonObject node, Table nodes, string file, GuiBundleSource source)
    {
        var line = new JsonObject();
        foreach (var (key, value) in node)
        {
            if (key == "c" && value is JsonArray children)
            {
                var written = new JsonArray();
                foreach (var child in children)
                {
                    written.Add(child switch
                    {
                        JsonObject element => JsonValue.Create(Intern(element, nodes, file, source)),
                        JsonValue text when text.GetValueKind() == JsonValueKind.String => text.DeepClone(),
                        _ => throw new InvalidDataException($"{source.PackageId} {source.PackageVersion}: gui/{file} has a child that is neither a node nor text: {child?.ToJsonString(Json)}"),
                    });
                }
                line[key] = written;
            }
            else
            {
                line[key] = value?.DeepClone();
            }
        }
        return nodes.Intern(line.ToJsonString(Json));
    }

    /// <summary>Line indexes as [first, last] runs of consecutive lines.</summary>
    internal static JsonArray Runs(IReadOnlyList<int> lines)
    {
        var runs = new JsonArray();
        for (var i = 0; i < lines.Count;)
        {
            var first = lines[i];
            var last = first;
            while (++i < lines.Count && lines[i] == last + 1)
                last = lines[i];
            runs.Add(new JsonArray(first, last));
        }
        return runs;
    }

    /// <summary>
    /// The first 32 hex digits of the SHA-256 over the files in ordinal path order, each as gui/&lt;path&gt;, a
    /// newline, its length, a newline and its bytes. bundle.json is hashed without its contentHash.
    /// </summary>
    internal static string Hash(IReadOnlyDictionary<string, byte[]> files)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, bytes) in files.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes($"gui/{path}\n{bytes.Length}\n"));
            sha.AppendData(bytes);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset())[..32];
    }

    internal static JsonNode? Parse(byte[] bytes) => JsonNode.Parse(bytes, documentOptions: DocumentOptions);

    /// <summary>A format 3 file as the round trip compares it: parsed, and written compact with the bundle's options.</summary>
    internal static string Normalize(byte[] bytes) => Parse(bytes)?.ToJsonString(Json) ?? "null";

    private static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString(Json) + "\n");

    /// <summary>Distinct lines, each numbered from 0 in the order first seen.</summary>
    private sealed class Table
    {
        private readonly List<string> _lines = [];
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

        public int Intern(string line)
        {
            if (line.Contains('\n'))
                throw new InvalidDataException("A compact JSON line holds a newline.");
            if (!_index.TryGetValue(line, out var index))
            {
                _index[line] = index = _lines.Count;
                _lines.Add(line);
            }
            return index;
        }

        public byte[] ToBytes()
        {
            var text = new StringBuilder();
            foreach (var line in _lines)
                text.Append(line).Append('\n');
            return Encoding.UTF8.GetBytes(text.ToString());
        }
    }
}

/// <summary>Game versions such as v1.2.10 by their numbers, so v1.2.10 comes after v1.2.9.</summary>
internal sealed class GameVersionComparer : IComparer<string>
{
    public static readonly GameVersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var byNumber = Comparer<Version?>.Default.Compare(Parse(x), Parse(y));
        return byNumber != 0 ? byNumber : StringComparer.Ordinal.Compare(x, y);
    }

    private static Version? Parse(string? version) =>
        version is [_, .. var rest] && Version.TryParse(rest, out var parsed) ? parsed : null;
}

/// <summary>
/// Reads a bundle back: the format 3 files of each package, rebuilt from bundle.json and the tables. The
/// round trip check uses it on the bytes as packed, and it is the reference for a consumer doing the same.
/// It records every line it reads, so the checks can tell whether any line goes unused.
/// </summary>
internal sealed class GuiBundleReader
{
    private readonly IReadOnlyDictionary<string, byte[]> _files;
    private readonly Dictionary<string, string[]> _tables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> _reached = new(StringComparer.Ordinal);
    private Node[]? _nodes;

    public GuiBundleReader(IReadOnlyDictionary<string, byte[]> files)
    {
        _files = files;
        Index = GuiBundle.Parse(Read(GuiBundle.IndexFile)) as JsonObject ?? throw new InvalidDataException($"{GuiBundle.IndexFile} is not a JSON object.");
    }

    public JsonObject Index { get; }

    public int FormatVersion => Index["formatVersion"]!.GetValue<int>();

    public string? ContentHash => Index["contentHash"]?.GetValue<string>();

    /// <summary>Every package in the bundle, with the game version it is listed under.</summary>
    public IEnumerable<(string GameVersion, JsonObject Package)> Packages =>
        from version in Index["versions"]!.AsArray().OfType<JsonObject>()
        from package in version["packages"]!.AsArray().OfType<JsonObject>()
        select (version["gameVersion"]!.GetValue<string>(), package);

    /// <summary>The lines of a table, read once.</summary>
    public string[] Lines(string table)
    {
        if (!_tables.TryGetValue(table, out var lines))
        {
            var text = Encoding.UTF8.GetString(Read(table));
            if (text.Length > 0 && !text.EndsWith('\n'))
                throw new InvalidDataException($"{table} does not end with a newline.");
            _tables[table] = lines = text.Length == 0 ? [] : text[..^1].Split('\n');
        }
        return lines;
    }

    /// <summary>Every table the index names, and the node table.</summary>
    public IEnumerable<string> Tables => _files.Keys.Where(x => x.EndsWith(".jsonl", StringComparison.Ordinal)).Order(StringComparer.Ordinal);

    /// <summary>The lines of each table that some package was rebuilt from.</summary>
    public IReadOnlyDictionary<string, HashSet<int>> Reached => _reached;

    /// <summary>A package's format 3 files, by path under gui/, each as compact JSON (see <see cref="GuiBundle.Normalize"/>).</summary>
    public Dictionary<string, string> Rebuild(JsonObject package)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in package["files"]!.AsArray().OfType<JsonObject>())
        {
            var text = new StringBuilder("{");
            var first = true;
            foreach (var property in file["properties"]!.AsArray().OfType<JsonObject>())
            {
                if (!first)
                    text.Append(',');
                first = false;
                text.Append(Name(property["name"]!.GetValue<string>())).Append(':');
                if (property["table"]?.GetValue<string>() is { } table)
                {
                    var lines = Lines(table);
                    text.Append('[');
                    var firstLine = true;
                    foreach (var line in Expand(table, property["runs"]!.AsArray(), lines.Length))
                    {
                        if (!firstLine)
                            text.Append(',');
                        firstLine = false;
                        text.Append(lines[line]);
                    }
                    text.Append(']');
                }
                else
                {
                    text.Append(property["value"]?.ToJsonString(GuiBundle.Json) ?? "null");
                }
            }
            files[file["path"]!.GetValue<string>()] = text.Append('}').ToString();
        }

        var trees = package["trees"]!.AsArray();
        var entries = files.TryGetValue(GuiBundle.PrefabIndexFile, out var prefabs)
            ? JsonNode.Parse(prefabs)![GuiBundle.PrefabIndexList] as JsonArray ?? new JsonArray()
            : new JsonArray();
        if (entries.Count != trees.Count)
            throw new InvalidDataException($"{package["packageId"]} lists {trees.Count} tree(s) for {entries.Count} {GuiBundle.PrefabIndexFile} entries.");
        for (var i = 0; i < trees.Count; i++)
        {
            var entry = entries[i]!;
            var tree = new StringBuilder("{\"formatVersion\":").Append(FormatVersion)
                .Append(",\"name\":").Append(Name(entry["name"]!.GetValue<string>()))
                .Append(",\"module\":").Append(Name(entry["module"]!.GetValue<string>()))
                .Append(",\"root\":");
            WriteNode(tree, trees[i]!.GetValue<int>());
            files[entry["file"]!.GetValue<string>()] = tree.Append('}').ToString();
        }
        return files;
    }

    private IEnumerable<int> Expand(string table, JsonArray runs, int count)
    {
        var reached = ReachedOf(table);
        foreach (var run in runs.OfType<JsonArray>())
        {
            var (first, last) = (run[0]!.GetValue<int>(), run[1]!.GetValue<int>());
            if (first < 0 || last < first || last >= count)
                throw new InvalidDataException($"The run [{first}, {last}] of {table} is outside its {count} line(s).");
            for (var line = first; line <= last; line++)
            {
                reached.Add(line);
                yield return line;
            }
        }
    }

    private void WriteNode(StringBuilder text, int index)
    {
        var nodes = Nodes();
        if (index < 0 || index >= nodes.Length)
            throw new InvalidDataException($"Node {index} is outside the {nodes.Length} line(s) of {GuiBundle.NodesFile}.");
        ReachedOf(GuiBundle.NodesFile).Add(index);
        var node = nodes[index];
        text.Append(node.Head);
        if (node.Children is null)
            return;
        text.Append('[');
        for (var i = 0; i < node.Children.Length; i++)
        {
            if (i > 0)
                text.Append(',');
            if (node.Children[i] is int child)
                WriteNode(text, child);
            else
                text.Append((string) node.Children[i]);
        }
        text.Append(']').Append(node.Tail);
    }

    /// <summary>
    /// Every line of nodes.jsonl, split once around its "c": the text before the children, the children (a line
    /// index, or the raw JSON of a text), and the text after them.
    /// </summary>
    public Node[] Nodes()
    {
        if (_nodes is not null)
            return _nodes;
        var lines = Lines(GuiBundle.NodesFile);
        var nodes = new Node[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            using var document = JsonDocument.Parse(lines[i]);
            var head = new StringBuilder("{");
            var tail = new StringBuilder();
            object[]? children = null;
            var first = true;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var into = children is null ? head : tail;
                if (!first)
                    into.Append(',');
                first = false;
                into.Append(Name(property.Name)).Append(':');
                if (property.Name == "c" && property.Value.ValueKind == JsonValueKind.Array)
                {
                    children = property.Value.EnumerateArray().Select(x => x.ValueKind switch
                    {
                        JsonValueKind.Number when x.GetInt32() is var child && child < i && child >= 0 => (object) child,
                        JsonValueKind.Number => throw new InvalidDataException($"Node {i} of {GuiBundle.NodesFile} names child {x.GetInt32()}, which is not an earlier line."),
                        JsonValueKind.String => x.GetRawText(),
                        _ => throw new InvalidDataException($"Node {i} of {GuiBundle.NodesFile} has a child that is neither a node nor text."),
                    }).ToArray();
                }
                else
                {
                    into.Append(property.Value.GetRawText());
                }
            }
            nodes[i] = children is null
                ? new Node(head.Append('}').ToString(), null, "")
                : new Node(head.ToString(), children, tail.Append('}').ToString());
        }
        return _nodes = nodes;
    }

    /// <summary>A node line split around its children: see <see cref="Nodes"/>. A node without children is all Head.</summary>
    public sealed record Node(string Head, object[]? Children, string Tail);

    private HashSet<int> ReachedOf(string table)
    {
        if (!_reached.TryGetValue(table, out var reached))
            _reached[table] = reached = [];
        return reached;
    }

    private byte[] Read(string file) =>
        _files.TryGetValue(file, out var bytes) ? bytes : throw new InvalidDataException($"The bundle has no gui/{file}.");

    private static string Name(string name) => JsonSerializer.Serialize(name, GuiBundle.Json);
}
