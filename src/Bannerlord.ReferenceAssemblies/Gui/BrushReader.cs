using System.Text.RegularExpressions;
using System.Xml;

namespace Bannerlord.ReferenceAssemblies;

internal sealed record BrushSchema(int FormatVersion, List<BrushEntry> Brushes);

/// <summary>
/// A brush by what it refers to; every visual value is left out. File is the brush file's path under the
/// module's GUI folder, the path the game tells files apart by: a later module's file of the same path
/// replaces an earlier one's.
/// </summary>
internal sealed record BrushEntry(
    string Name,
    string Module,
    string File,
    string? BaseBrush,
    string? OverrideBrush,
    string? Font,
    List<BrushLayerEntry> Layers,
    List<BrushStyleEntry> Styles,
    List<string?> Animations,
    List<BrushEventSound> EventSounds,
    List<BrushStateSound> StateSounds);

internal sealed record BrushLayerEntry(string? Name, string? Sprite, string? OverlaySprite);

/// <summary>A style; its layers are the brush layers it changes, by name, with the sprites it gives them.</summary>
internal sealed record BrushStyleEntry(string? Name, string? Font, string? AnimationToPlayOnBegin, List<BrushLayerEntry> Layers);

internal sealed record BrushEventSound(string? Event, string? Audio);

internal sealed record BrushStateSound(string? State, string? Audio);

/// <summary>
/// Writes brushes.json, reading the brush files the way BrushFactory does. It loads Base.xml first, then the
/// other files of GUI/Brushes, not those of its subfolders; each child of &lt;Brushes&gt; is a brush, and a later
/// brush of a name replaces an earlier one. Of a brush it reads the first Layers, Styles, Animations and
/// SoundProperties element, and every child of each whatever its name, so Layers/Layer is a layer and
/// Style/StyleLayer a style layer. Anything else under a brush, such as a Brush/Style, the loader never reads,
/// and neither does this. A layer or style named twice in one brush is one, as the loader merges the second
/// into the first: what the second sets replaces the first's, the rest stays. So is a sound given twice for one
/// event or state, the later one standing.
/// </summary>
internal static class BrushReader
{
    private static readonly Regex BrushFile = new(@"^gui/[^/]+/GUI/Brushes/[^/]+\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The children of a brush that the loader reads.</summary>
    private static readonly string[] ReadChildren = ["Layers", "Styles", "Animations", "SoundProperties"];

    public static BrushSchema Read(string sourceFolder, IEnumerable<GuiModule> modules)
    {
        var brushes = new List<BrushEntry>();
        var ignored = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            var files = GuiPackager.GuiFiles(Path.Combine(sourceFolder, "Modules", module.Folder), module.Folder)
                .Where(x => BrushFile.IsMatch(x.Target))
                .OrderBy(x => !string.Equals(Path.GetFileName(x.Source), "Base.xml", StringComparison.OrdinalIgnoreCase))
                .ThenBy(x => x.Target, StringComparer.OrdinalIgnoreCase);

            // A later brush of a name takes the place of the earlier one, as in the loader's dictionary.
            var byName = new Dictionary<string, int>(StringComparer.Ordinal);
            var own = new List<BrushEntry>();
            foreach (var (source, target) in files)
            {
                var file = target[$"gui/{module.Folder}/GUI/".Length..];
                foreach (var brush in Load(source).SelectSingleNode("Brushes")?.ChildNodes.OfType<XmlElement>() ?? [])
                {
                    if (Attribute(brush, "Name") is not { } name)
                    {
                        Count(ignored, $"a brush without a Name in {module.Folder}/GUI/{file}");
                        continue;
                    }
                    foreach (var child in brush.ChildNodes.OfType<XmlElement>().GroupBy(x => x.Name))
                        if (!ReadChildren.Contains(child.Key) || child.Count() > 1)
                            Count(ignored, $"Brush/{child.Key}{(ReadChildren.Contains(child.Key) ? " after the first" : "")}");

                    var entry = ReadBrush(brush, name, module.Folder, file);
                    if (byName.TryGetValue(name, out var index))
                    {
                        Log.Info($"    brush {name} of {module.Folder} is defined again in {file}, which replaces the one in {own[index].File}");
                        own[index] = entry;
                    }
                    else
                    {
                        byName[name] = own.Count;
                        own.Add(entry);
                    }
                }
            }
            brushes.AddRange(own);
        }

        foreach (var (what, count) in ignored)
            Log.Info($"    not read by BrushFactory: {what} ({count})");
        return new BrushSchema(GuiPackager.FormatVersion, brushes);
    }

    private static BrushEntry ReadBrush(XmlElement brush, string name, string module, string file)
    {
        var sounds = brush.SelectSingleNode("SoundProperties");
        var animations = Children(brush.SelectSingleNode("Animations")).Select(x => Attribute(x, "Name")).ToList();
        // Brush.AddAnimation adds to a dictionary, so a second animation of a name throws and the file does not load.
        foreach (var animation in animations.GroupBy(x => x).Where(x => x.Count() > 1))
            Log.Info($"    WARNING: brush {name} of {module} has animation {animation.Key} twice; the game fails to load {file}");

        return new BrushEntry(
            name,
            module,
            file,
            Attribute(brush, "BaseBrush"),
            Attribute(brush, "OverrideBrush"),
            Attribute(brush, "Font"),
            MergeLayers(Children(brush.SelectSingleNode("Layers")).Select(Layer)),
            Merge(Children(brush.SelectSingleNode("Styles")).Select(x => new BrushStyleEntry(Attribute(x, "Name"), Attribute(x, "Font"), Attribute(x, "AnimationToPlayOnBegin"), MergeLayers(Children(x).Select(Layer)))),
                x => x.Name,
                (first, second) => new BrushStyleEntry(first.Name, second.Font ?? first.Font, second.AnimationToPlayOnBegin ?? first.AnimationToPlayOnBegin, MergeLayers(first.Layers.Concat(second.Layers)))),
            animations,
            Merge(Children(sounds?.SelectSingleNode("EventSounds")).Select(x => new BrushEventSound(Attribute(x, "EventName"), Attribute(x, "Audio"))), x => x.Event, (_, second) => second),
            Merge(Children(sounds?.SelectSingleNode("StateSounds")).Select(x => new BrushStateSound(Attribute(x, "StateName"), Attribute(x, "Audio"))), x => x.State, (_, second) => second));

        static BrushLayerEntry Layer(XmlElement layer) => new(Attribute(layer, "Name"), Attribute(layer, "Sprite"), Attribute(layer, "OverlaySprite"));

        static List<BrushLayerEntry> MergeLayers(IEnumerable<BrushLayerEntry> layers) =>
            Merge(layers, x => x.Name, (first, second) => new BrushLayerEntry(first.Name, second.Sprite ?? first.Sprite, second.OverlaySprite ?? first.OverlaySprite));
    }

    /// <summary>
    /// Entries of one name made one, in the place of the first, as the loader's get-or-add does. An entry
    /// without a name is kept as it is: the loader fails on it.
    /// </summary>
    private static List<T> Merge<T>(IEnumerable<T> entries, Func<T, string?> name, Func<T, T, T> merge)
    {
        var result = new List<T>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (name(entry) is not { } key)
            {
                result.Add(entry);
                continue;
            }
            if (index.TryGetValue(key, out var at))
            {
                result[at] = merge(result[at], entry);
                continue;
            }
            index[key] = result.Count;
            result.Add(entry);
        }
        return result;
    }

    /// <summary>A brush file as BrushFactory loads it: comments dropped.</summary>
    private static XmlDocument Load(string path)
    {
        var document = new XmlDocument();
        // The game passes the StreamReader in and never closes it; the file is read the same way here, and closed.
        using var text = new StreamReader(path);
        using var reader = XmlReader.Create(text, new XmlReaderSettings { IgnoreComments = true });
        document.Load(reader);
        return document;
    }

    private static IEnumerable<XmlElement> Children(XmlNode? node) => node?.ChildNodes.OfType<XmlElement>() ?? [];

    private static string? Attribute(XmlElement element, string name) => element.GetAttributeNode(name)?.Value;

    private static void Count(SortedDictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
}
