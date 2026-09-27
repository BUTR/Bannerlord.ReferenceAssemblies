using System.Text.RegularExpressions;
using System.Xml;

namespace Bannerlord.ReferenceAssemblies;

internal sealed record SpriteSchema(int FormatVersion, List<SpriteCategoryEntry> Categories, List<SpriteEntry> Sprites);

internal sealed record SpriteCategoryEntry(string Name, string Module, bool AlwaysLoad);

/// <summary>A sprite a Sprite value can name, with the category of the sprite part it is drawn from. Kind is generic or nineRegion.</summary>
internal sealed record SpriteEntry(string Name, string? Category, string Kind);

/// <summary>
/// Writes sprites.json, reading the sprite data the way SpriteData.LoadSpriteDataFromFile does: the names are
/// the Name elements' text as it stands, a category has &lt;AlwaysLoad /&gt; or not, and a GenericSprite or a
/// NineRegionSprite takes the category of the SpritePart it names. A later entry of a name replaces an earlier
/// one. Sheet sizes and part coordinates are left out.
/// </summary>
internal static class SpriteReader
{
    private static readonly Regex SpriteDataFile = new(@"^gui/[^/]+/GUI/[^/]+SpriteData\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The module the game's root sprite data (<see cref="RootFile"/>) is read as.</summary>
    private const string RootModule = "Native";

    public static SpriteSchema Read(string sourceFolder, IEnumerable<GuiModule> modules)
    {
        var categories = new Ordered<SpriteCategoryEntry>();
        var sprites = new Ordered<SpriteEntry>();
        // One table across the files, as the loader keeps one: a sprite may name a part of an earlier file.
        var partCategories = new Dictionary<string, string?>(StringComparer.Ordinal);
        var files = new List<(string Source, string Module)>();
        if (RootFile(sourceFolder) is { } root)
            files.Add((root, RootModule));
        foreach (var module in modules)
            files.AddRange(GuiPackager.GuiFiles(Path.Combine(sourceFolder, "Modules", module.Folder), module.Folder).Where(x => SpriteDataFile.IsMatch(x.Target)).Select(x => (x.Source, module.Folder)));

        foreach (var (source, module) in files)
        {
            var document = new XmlDocument();
            document.Load(source);
            if (document["SpriteData"] is not { } data)
                continue;

            foreach (var category in Elements(data["SpriteCategories"]))
                if (category["Name"]?.InnerText is { } name)
                    categories.Set(name, new SpriteCategoryEntry(name, module, category.ChildNodes.OfType<XmlElement>().Any(x => x.Name == "AlwaysLoad")));

            foreach (var part in Elements(data["SpriteParts"]))
                if (part["Name"]?.InnerText is { } name)
                    partCategories[name] = part["CategoryName"]?.InnerText;

            foreach (var sprite in Elements(data["Sprites"]))
            {
                var kind = sprite.Name switch
                {
                    "GenericSprite" => "generic",
                    "NineRegionSprite" => "nineRegion",
                    _ => null,
                };
                if (kind is null || sprite["Name"]?.InnerText is not { } name)
                {
                    Log.Info($"    not read by SpriteData: {sprite.Name} in {Path.GetRelativePath(sourceFolder, source).Replace('\\', '/')}");
                    continue;
                }
                var part = sprite["SpritePartName"]?.InnerText;
                string? category = null;
                if (part is null || !partCategories.TryGetValue(part, out category))
                    Log.Info($"    sprite {name} of {module} is drawn from sprite part {part ?? "(none)"}, which the sprite data does not define");
                sprites.Set(name, new SpriteEntry(name, category, kind));
            }
        }
        return new SpriteSchema(GuiPackager.FormatVersion, categories.Values, sprites.Values);
    }

    /// <summary>
    /// The game's sprite data at the root of the game folder, GUI/GauntletUI/spriteData.xml, which builds up to e1.5.3
    /// have and later ones do not; null when the build has none. Its name is matched regardless of case, as the
    /// download filter matches it.
    /// </summary>
    public static string? RootFile(string gameFolder)
    {
        var folder = Path.Combine(gameFolder, "GUI", "GauntletUI");
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.xml").FirstOrDefault(x => string.Equals(Path.GetFileName(x), "spriteData.xml", StringComparison.OrdinalIgnoreCase))
            : null;
    }

    private static IEnumerable<XmlElement> Elements(XmlNode? node) => node?.ChildNodes.OfType<XmlElement>() ?? [];

    /// <summary>Entries by name in the order first seen, a later entry replacing the earlier one in its place, as a Dictionary does.</summary>
    private sealed class Ordered<T>
    {
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

        public List<T> Values { get; } = [];

        public void Set(string name, T value)
        {
            if (_index.TryGetValue(name, out var index))
            {
                Values[index] = value;
                return;
            }
            _index[name] = Values.Count;
            Values.Add(value);
        }
    }
}
