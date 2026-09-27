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

    public static SpriteSchema Read(string sourceFolder, IEnumerable<GuiModule> modules)
    {
        var categories = new Ordered<SpriteCategoryEntry>();
        var sprites = new Ordered<SpriteEntry>();
        // One table across the files, as the loader keeps one: a sprite may name a part of an earlier file.
        var partCategories = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var module in modules)
        foreach (var (source, _) in GuiPackager.GuiFiles(Path.Combine(sourceFolder, "Modules", module.Folder), module.Folder).Where(x => SpriteDataFile.IsMatch(x.Target)))
        {
            var document = new XmlDocument();
            document.Load(source);
            if (document["SpriteData"] is not { } data)
                continue;

            foreach (var category in Elements(data["SpriteCategories"]))
                if (category["Name"]?.InnerText is { } name)
                    categories.Set(name, new SpriteCategoryEntry(name, module.Folder, category.ChildNodes.OfType<XmlElement>().Any(x => x.Name == "AlwaysLoad")));

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
                    Log.Info($"    not read by SpriteData: {sprite.Name} in {module.Folder}/GUI/{Path.GetFileName(source)}");
                    continue;
                }
                var part = sprite["SpritePartName"]?.InnerText;
                string? category = null;
                if (part is null || !partCategories.TryGetValue(part, out category))
                    Log.Info($"    sprite {name} of {module.Folder} is drawn from sprite part {part ?? "(none)"}, which the sprite data does not define");
                sprites.Set(name, new SpriteEntry(name, category, kind));
            }
        }
        return new SpriteSchema(GuiPackager.FormatVersion, categories.Values, sprites.Values);
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
