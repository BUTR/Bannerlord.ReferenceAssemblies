using AsmResolver.DotNet;

using System.Xml.Linq;

namespace Bannerlord.ReferenceAssemblies;

/// <summary>
/// The acceptance checks run on every pack, against the game's own prefabs and brushes, read from the XML the
/// packages are written from: what they name should be in the packed data. Nothing here changes a package; the exceptions are logged. Most are
/// mistakes in the game's own XML, which the data rightly says do nothing, and a new one is either such a
/// mistake or something the scanner missed.
/// </summary>
internal sealed class GuiChecks
{
    private const string FontsLauncherCategory = "ui_fonts_launcher";

    private readonly GameAssemblies _build;
    private readonly BuildScanner _scanner;
    private readonly Dictionary<string, TypeDefinition> _widgetTypes;
    private readonly Dictionary<string, XElement?> _prefabs = new(StringComparer.Ordinal);
    private readonly List<XElement> _brushes = [];
    private readonly HashSet<string> _spriteCategories = new(StringComparer.Ordinal);
    private readonly HashSet<string> _spriteDataSprites = new(StringComparer.Ordinal);

    public GuiChecks(string gameFolder, IReadOnlyList<GuiModule> modules, GameAssemblies build, BuildScanner scanner)
    {
        _build = build;
        _scanner = scanner;
        _widgetTypes = build.Types.Where(x => x.IsClass && build.DerivesFrom(x, TypeSchemaReader.WidgetType))
            .GroupBy(x => x.Name!.ToString(), StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        foreach (var module in modules)
        foreach (var (source, target) in GuiPackager.GuiFiles(Path.Combine(gameFolder, "Modules", module.Folder), module.Folder))
        {
            if (target.Contains("/GUI/Prefabs/", StringComparison.Ordinal))
                _prefabs[Path.GetFileNameWithoutExtension(source)] = XDocument.Load(source).Root;
            else if (target.Contains("/GUI/Brushes/", StringComparison.Ordinal) && XDocument.Load(source).Root is { } brushes)
                _brushes.Add(brushes);
            else if (target.EndsWith("SpriteData.xml", StringComparison.Ordinal) && XDocument.Load(source).Root is { } spriteData)
            {
                foreach (var name in spriteData.Element("SpriteCategories")?.Elements("SpriteCategory").Select(x => (string?) x.Element("Name")) ?? [])
                    if (name is { Length: > 0 })
                        _spriteCategories.Add(name.Trim());
                foreach (var name in spriteData.Element("Sprites")?.Elements().Select(x => (string?) x.Element("Name")) ?? [])
                    if (name is not null)
                        _spriteDataSprites.Add(name);
            }
        }
    }

    public void Run(SpriteCategorySchema categories, IReadOnlyCollection<string> fonts, IReadOnlyCollection<string> sounds,
        IReadOnlyCollection<BrushEntry> brushes, IReadOnlyCollection<SpriteEntry> sprites)
    {
        CheckCommands();
        CheckDottedAttributes();
        CheckCategoriesDefined(categories);
        CheckFonts(fonts);
        CheckSounds(sounds);
        CheckBrushReferences(brushes);
        CheckSprites(brushes, sprites);
    }

    /// <summary>
    /// Every brush a prefab names, by Brush, by BrushName in its constants, or by IconBrush, should be in
    /// brushes.json. The sounds are counted against the brush XML: the packed ones should be all the loader reads.
    /// </summary>
    private void CheckBrushReferences(IReadOnlyCollection<BrushEntry> brushes)
    {
        var known = brushes.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var exceptions = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var checkedCount = 0;
        foreach (var (_, attribute) in PrefabAttributes().Where(x => x.Attribute.Name.LocalName is "Brush" or "BrushName" or "IconBrush"))
        {
            if (!IsLiteral(attribute.Value))
                continue;
            checkedCount++;
            if (!known.Contains(attribute.Value))
                Count(exceptions, $"{attribute.Name.LocalName}=\"{attribute.Value}\"");
        }
        Report("Brush check", $"{checkedCount} brush name(s) in the prefabs checked against {known.Count} brush(es), {exceptions.Values.Sum()} naming no brush; "
                              + $"{brushes.Sum(x => x.EventSounds.Count)} event and {brushes.Sum(x => x.StateSounds.Count)} state sound(s) packed of "
                              + $"{LoaderSounds("EventSounds")} and {LoaderSounds("StateSounds")} in the brush XML", exceptions);

        // The loader reads the first SoundProperties of a brush, and in it the first list of each kind.
        int LoaderSounds(string kind) => _brushes.SelectMany(x => x.Elements())
            .Sum(x => x.Element("SoundProperties")?.Element(kind)?.Elements().Count() ?? 0);
    }

    /// <summary>
    /// Every sprite the prefabs and brushes name that the game's sprite data defines should be in sprites.json.
    /// The ones it does not define are reported: the game draws nothing for them.
    /// </summary>
    private void CheckSprites(IReadOnlyCollection<BrushEntry> brushes, IReadOnlyCollection<SpriteEntry> sprites)
    {
        var packed = sprites.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var named = PrefabAttributes().Where(x => x.Attribute.Name.LocalName.EndsWith("Sprite", StringComparison.Ordinal)).Select(x => x.Attribute.Value)
            .Concat(brushes.SelectMany(x => x.Layers.Concat(x.Styles.SelectMany(s => s.Layers))).SelectMany(x => new[] { x.Sprite, x.OverlaySprite }).OfType<string>())
            .Where(IsLiteral)
            .ToList();
        var missing = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var undefined = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var sprite in named)
        {
            if (!_spriteDataSprites.Contains(sprite))
                Count(undefined, sprite);
            else if (!packed.Contains(sprite))
                Count(missing, sprite);
        }
        Report("Sprite check", $"{named.Count} sprite name(s) in the prefabs and brushes checked against {packed.Count} packed sprite(s), {missing.Values.Sum()} defined by the sprite data but not packed", missing);
        Report("  not defined by any sprite data", $"{undefined.Count} name(s)", undefined);
    }

    /// <summary>A value written out, rather than a binding (@), a constant (!) or a prefab parameter (*).</summary>
    private static bool IsLiteral(string value) => value.Length > 0 && value[0] is not ('@' or '!' or '*');

    /// <summary>
    /// Every Command.&lt;name&gt; attribute should name an event the element's widget class, or one of its
    /// bases, raises. An element named after a prefab stands for that prefab's root widget.
    /// </summary>
    private void CheckCommands()
    {
        var checkedCount = 0;
        var unknownElement = 0;
        var exceptions = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (element, attribute) in PrefabAttributes().Where(x => x.Attribute.Name.LocalName.StartsWith("Command.", StringComparison.Ordinal)))
        {
            if (WidgetOf(element.Name.LocalName, 0) is not { } widget)
            {
                unknownElement++;
                continue;
            }
            checkedCount++;
            var name = attribute.Name.LocalName["Command.".Length..];
            if (!_build.SelfAndBases(widget).Any(x => _scanner.EventsOf(x).Events.Contains(name)))
                Count(exceptions, $"{widget.Name} Command.{name}");
        }
        Report("Command check", $"{checkedCount} Command.* attribute(s) checked, {unknownElement} on elements naming no widget or prefab, {exceptions.Values.Sum()} naming an event the widget does not raise", exceptions);
    }

    /// <summary>
    /// Every dotted attribute, such as Brush.FontSize, should follow a widget property with a getter, then the
    /// members of the object it holds (its declared type, or a type the widget assigns to it), to a member.
    /// </summary>
    private void CheckDottedAttributes()
    {
        var checkedCount = 0;
        var exceptions = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (element, attribute) in PrefabAttributes())
        {
            var name = attribute.Name.LocalName;
            // Command.* binds an event, CommandParameter.* its argument, Parameter.* a value passed to a prefab: none is a path.
            if (!name.Contains('.') || name.Split('.')[0] is "Command" or "CommandParameter" or "Parameter" || WidgetOf(element.Name.LocalName, 0) is not { } widget)
                continue;
            checkedCount++;
            if (!Resolves(widget, name.Split('.')))
                Count(exceptions, $"{widget.Name} {name}");
        }
        Report("Dotted attribute check", $"{checkedCount} dotted attribute(s) checked, {exceptions.Values.Sum()} resolving to no member", exceptions);
    }

    private bool Resolves(TypeDefinition widget, string[] path)
    {
        // The first step is a widget property, whose value may be any type the widgets assign to it.
        var candidates = new List<TypeDefinition> { widget };
        for (var i = 0; i < path.Length; i++)
        {
            var last = i == path.Length - 1;
            var next = new List<TypeDefinition>();
            foreach (var type in candidates)
            foreach (var owner in _build.SelfAndBases(type))
            {
                if (owner.Properties.FirstOrDefault(x => x.Name == path[i] && (last ? x.GetMethod is { IsPublic: true } || x.SetMethod is { IsPublic: true } : x.GetMethod is { IsPublic: true })) is not { } property)
                    continue;
                if (last)
                    return true;
                if (_build.Resolve(property.Signature?.ReturnType) is { } held)
                    next.Add(held);
                foreach (var assigned in i == 0 ? _scanner.AssignedTypes(owner, property) ?? [] : [])
                    if (_build.FindType(assigned) is { } assignedType)
                        next.Add(assignedType);
                break;
            }
            candidates = next;
        }
        return false;
    }

    /// <summary>
    /// Every category spriteCategories.json names should be defined in the packed sprite data. The launcher's
    /// ui_fonts_launcher is the one allowed exception: its sprite data is in Native/LauncherGUI, not packed.
    /// </summary>
    private void CheckCategoriesDefined(SpriteCategorySchema categories)
    {
        var named = categories.Always.Select(x => x.Category)
            .Concat(categories.Missions.Select(x => x.Category))
            .Concat(categories.Classes.SelectMany(x => x.Categories.Concat(x.Partial)));
        var exceptions = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var category in named.Where(x => !_spriteCategories.Contains(x) && x != FontsLauncherCategory))
            Count(exceptions, category);
        Report("Sprite category check", $"{_spriteCategories.Count} categories in the packed sprite data, {exceptions.Count} named but not defined (besides {FontsLauncherCategory})", exceptions);
    }

    /// <summary>Every font the brushes and prefabs name should be a font the game loads; an unknown one falls back to the language's default font.</summary>
    private void CheckFonts(IReadOnlyCollection<string> fonts)
    {
        var known = fonts.ToHashSet(StringComparer.Ordinal);
        var exceptions = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var checkedCount = 0;
        var named = _brushes.SelectMany(x => x.Descendants()).Select(x => (string?) x.Attribute("Font"))
            .Concat(PrefabAttributes().Where(x => x.Attribute.Name.LocalName == "Brush.Font").Select(x => (string?) x.Attribute.Value));
        foreach (var font in named.OfType<string>().Where(x => x.Length > 0 && !x.StartsWith('@') && !x.StartsWith('!')))
        {
            checkedCount++;
            if (!known.Contains(font))
                Count(exceptions, font);
        }
        Report("Font check", $"{checkedCount} font name(s) checked against {known.Count} font(s), {exceptions.Values.Sum()} unknown", exceptions);
    }

    /// <summary>Every Audio a brush gives should be a UI sound event: TwoDimensionEnginePlatform.PlaySound plays "event:/ui/" + the name.</summary>
    private void CheckSounds(IReadOnlyCollection<string> sounds)
    {
        var known = sounds.ToHashSet(StringComparer.Ordinal);
        var exceptions = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var values = _brushes.SelectMany(x => x.Descendants()).Select(x => (string?) x.Attribute("Audio")).OfType<string>().Where(x => x.Length > 0).ToList();
        foreach (var audio in values.Where(x => !known.Contains(x)))
            Count(exceptions, audio);
        Report("Sound check", $"{values.Count} Audio value(s), {values.Distinct().Count()} distinct, checked against {known.Count} UI sound(s), {exceptions.Values.Sum()} unknown", exceptions);
    }

    private IEnumerable<(XElement Element, XAttribute Attribute)> PrefabAttributes() =>
        from root in _prefabs.Values.OfType<XElement>()
        from element in root.Descendants()
        from attribute in element.Attributes()
        select (element, attribute);

    /// <summary>The widget class an element stands for: its own name, or for a prefab used as an element, that prefab's root widget.</summary>
    private TypeDefinition? WidgetOf(string element, int depth)
    {
        if (_widgetTypes.TryGetValue(element, out var type))
            return type;
        if (depth > 8 || !_prefabs.TryGetValue(element, out var prefab) || prefab?.Element("Window")?.Elements().FirstOrDefault() is not { } first)
            return null;
        return WidgetOf(first.Name.LocalName, depth + 1);
    }

    private static void Count(SortedDictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

    private static void Report(string check, string summary, SortedDictionary<string, int> exceptions)
    {
        Log.Info($"  {check}: {summary}");
        foreach (var (key, count) in exceptions)
            Log.Info($"    {key} ({count})");
    }
}
