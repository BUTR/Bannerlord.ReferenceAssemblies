using AsmResolver.DotNet;

using NuGet.Packaging;

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;

namespace Bannerlord.ReferenceAssemblies;

/// <summary>One module as its SubModule.xml describes it, and whether a DLC brought it.</summary>
internal sealed record GuiModule(string Folder, string? Id, string? Version, string? ModuleType, List<Dictionary<string, string>> DependedModules, bool Dlc);

internal sealed record GuiManifest(int FormatVersion, string Package, string GameVersion, int? ChangeSet, uint BuildId, List<GuiModule> Modules);

/// <summary>The fonts the game loads, by name: the .fnt files of the resource folders' Fonts directories.</summary>
internal sealed record FontSchema(int FormatVersion, List<string> Fonts, List<string> Sources);

/// <summary>The names a brush can give as Audio: the UI sound events, without their prefix.</summary>
internal sealed record UiSoundSchema(int FormatVersion, string Prefix, List<string> Sounds, List<string> Sources);

/// <summary>
/// Turns a game folder into the GUI packages: one with every module of the game itself, and one per DLC
/// module. Each carries data written from the game's files, none of the files themselves: a JSON tree per
/// prefab with an index of them, the brushes and sprites by name and what they refer to, a manifest of its
/// modules, the movies the build loads with their ViewModels, and the build's widget and ViewModel types.
/// Nothing in them reaches a mod's compilation; build/&lt;id&gt;.props only lists the files for an analyzer to
/// pick up.
///
/// The DLC split follows the folders the download put each app's depots in: a module under a DLC app's
/// folder is that DLC's. The game folder holds the DLC too, copied over it as Steam installs a DLC, so the
/// assemblies of both are scanned together: a DLC screen usually inherits the base screen that loads its
/// movie.
/// </summary>
internal sealed class GuiPackager(Paths paths)
{
    /// <summary>
    /// The layout of the package contents, and the version in the package ids. A published package can never
    /// be repacked under its id, so a change the consumer has to handle differently gets a new family of ids,
    /// packed again for every build, old ones included. Format 1 (GUI.v1) carried the game's XML byte for byte;
    /// format 2 (GUI.v2) carried only data written from it. Format 3 (GUI.v3) adds what widgets announce and
    /// each ViewModel accessor's accessibility to types.json, which only regenerating every build gives. A
    /// consumer references the family it reads, and finds it for every build.
    /// </summary>
    public const int FormatVersion = 3;

    /// <summary>
    /// The module part of the package ids, with the format version: Bannerlord.ReferenceAssemblies.GUI.v3, and
    /// .GUI.v3.&lt;DlcModule&gt; for a DLC.
    /// </summary>
    public static readonly string BaseModule = $"GUI.v{FormatVersion}";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Packs the game folder, with the DLC folders naming which modules are DLC, and returns the package paths.</summary>
    public IReadOnlyList<string> Pack(string gameFolder, IReadOnlyList<string> dlcFolders, PackageSpec spec)
    {
        var dlcModules = FindDlcModules(dlcFolders);
        var modules = ReadModules(gameFolder, dlcModules);
        if (modules.Count == 0)
            throw new InvalidDataException($"No modules under {Path.Combine(gameFolder, "Modules")}.");

        var (scanned, generated) = GameAssemblies.Find(gameFolder, spec.BinFolders);
        var build = new GameAssemblies(scanned);
        Log.Info($"  scanning {build.Assemblies.Count} assemblies for movies and types");
        var scanner = new BuildScanner(build);
        var (movies, stats) = scanner.ScanMovies();
        Log.Info($"  {stats.Sites} LoadMovie call(s); {movies.Calls.Count} resolved pair(s), {movies.Calls.Count(x => !x.Paired)} unpaired, {movies.Unresolved.Count} unresolved");
        Log.Info($"  first step of each trace: {string.Join(", ", stats.FirstStep.Select(x => $"{x.Key} {x.Value}"))}");
        if (stats.DepthCapHits > 0 || stats.Truncated > 0)
            Log.Info($"  WARNING: {stats.DepthCapHits} trace(s) reached the depth cap, {stats.Truncated} value(s) had their origins cut short");
        foreach (var unresolved in movies.Unresolved.DistinctBy(x => (x.Caller, x.Via)))
            Log.Info($"  unresolved: {unresolved.Caller}: {unresolved.Reason} ({unresolved.Via})");
        CheckAgainstGenerated(generated, movies);

        var categories = scanner.ScanSpriteCategories(movies);
        Log.Info($"  sprite categories: {categories.Classes.Count} class(es) load their own, {categories.Always.Count} load(s) at start, {categories.Missions.Count} in every mission, {categories.Unresolved.Count} unresolved");
        foreach (var always in categories.Always)
            Log.Info($"    at start: {always.Category} ({always.Caller})");
        foreach (var mission in categories.Missions)
            Log.Info($"    in every mission: {mission.Category} ({mission.Via})");
        foreach (var (creator, created) in scanner.CreatedClasses)
            Log.Info($"    created: {created} gets the categories of {creator}, which creates it");
        var objects = TypeSchemaReader.Objects(build, scanner);
        foreach (var (name, node) in objects.OrderBy(x => x.Value.Depth).ThenBy(x => x.Key, StringComparer.Ordinal))
            Log.Info($"    object at {node.Depth} step(s): {name}");
        foreach (var unresolved in categories.Unresolved)
            Log.Info($"    unresolved: {unresolved.Caller}: {unresolved.Reason} ({unresolved.Via})");

        var packed = new List<PackedGui>();
        var content = new PackContent(build, scanner, movies, categories, objects);
        packed.Add(PackOne(spec, null,
            modules.Where(x => !x.Dlc).ToList(), gameFolder, content,
            owner => owner is null || !dlcModules.ContainsKey(owner)));

        foreach (var (module, dlcFolder) in dlcModules.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            packed.Add(PackOne(spec, module,
                modules.Where(x => string.Equals(x.Folder, module, StringComparison.OrdinalIgnoreCase)).ToList(), dlcFolder, content,
                owner => string.Equals(owner, module, StringComparison.OrdinalIgnoreCase)));
        }

        new GuiChecks(gameFolder, modules, build, scanner).Run(
            categories,
            Fonts(gameFolder, modules, includeEngine: true).Fonts,
            UiSounds(gameFolder, modules).Sounds,
            packed.SelectMany(x => x.Brushes.Brushes).ToList(),
            packed.SelectMany(x => x.Sprites.Sprites).ToList());
        return packed.Select(x => x.Path).ToList();
    }

    /// <summary>A package written, with the brushes and sprites it lists for the checks across all of them.</summary>
    private sealed record PackedGui(string Path, BrushSchema Brushes, SpriteSchema Sprites);

    /// <summary>What the scan of the build found, split between the packages by module.</summary>
    private sealed record PackContent(GameAssemblies Build, BuildScanner Scanner, MovieSchema Movies, SpriteCategorySchema Categories,
        IReadOnlyDictionary<string, TypeSchemaReader.ObjectNode> Objects);

    /// <summary>
    /// The modules each DLC folder brought, by module folder name. A DLC GUI file outside its own modules
    /// would have overwritten a base file when the DLC was copied over the game folder, and the base
    /// package would carry the DLC's bytes; no DLC does that so far, so it stops the packing.
    /// </summary>
    internal static Dictionary<string, string> FindDlcModules(IReadOnlyList<string> dlcFolders)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dlcFolder in dlcFolders)
        {
            var modules = Path.Combine(dlcFolder, "Modules");
            var own = Directory.Exists(modules)
                ? Directory.EnumerateDirectories(modules).Where(x => File.Exists(Path.Combine(x, "SubModule.xml"))).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];

            foreach (var file in Directory.EnumerateFiles(dlcFolder, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(dlcFolder, file).Replace('\\', '/');
                if (!App.GuiFileFilter.IsMatch(relative))
                    continue;
                if (!own.Contains(relative.Split('/')[1]))
                    throw new InvalidDataException($"The DLC in {dlcFolder} ships {relative}, outside its own modules ({string.Join(", ", own)}); it would overwrite the game's file of that name.");
            }

            foreach (var module in own)
                if (!result.TryAdd(module, dlcFolder))
                    throw new InvalidDataException($"Module {module} comes with two DLCs: {result[module]} and {dlcFolder}.");
        }
        return result;
    }

    /// <summary>Every module of the game folder, from its SubModule.xml, in load order.</summary>
    internal static List<GuiModule> ReadModules(string gameFolder, IReadOnlyDictionary<string, string> dlcModules)
    {
        var modulesFolder = Path.Combine(gameFolder, "Modules");
        if (!Directory.Exists(modulesFolder))
            return [];

        var modules = new List<GuiModule>();
        foreach (var folder in Directory.EnumerateDirectories(modulesFolder))
        {
            var manifest = Path.Combine(folder, "SubModule.xml");
            if (!File.Exists(manifest))
                continue;
            var root = XDocument.Load(manifest).Root;
            var name = Path.GetFileName(folder);
            var dlc = dlcModules.ContainsKey(name);
            var module = new GuiModule(
                name,
                Value(root, "Id"),
                Value(root, "Version"),
                Value(root, "ModuleType"),
                root?.Element("DependedModules")?.Elements("DependedModule")
                    .Select(x => x.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value))
                    .ToList() ?? [],
                dlc);
            if (dlc && module.ModuleType != "OfficialOptional")
                Log.Info($"  WARNING: DLC module {name} is {module.ModuleType ?? "untyped"} rather than OfficialOptional");
            modules.Add(module);
        }
        return LoadOrder(modules);

        static string? Value(XElement? root, string element) =>
            root?.Elements(element).Select(x => (string?) x.Attribute("value")).FirstOrDefault(x => x is not null)?.Trim();
    }

    /// <summary>Dependencies first, ties by folder name, so the order is the same on every run.</summary>
    internal static List<GuiModule> LoadOrder(IReadOnlyList<GuiModule> modules)
    {
        var byId = modules.Where(x => x.Id is not null).GroupBy(x => x.Id!, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var ordered = new List<GuiModule>();
        var placed = new HashSet<GuiModule>();
        var remaining = modules.OrderBy(x => x.Folder, StringComparer.OrdinalIgnoreCase).ToList();
        while (remaining.Count > 0)
        {
            // A cycle, or a dependency on a module that is not here, must not stall the order.
            var next = remaining.FirstOrDefault(x => Dependencies(x).All(placed.Contains)) ?? remaining[0];
            ordered.Add(next);
            placed.Add(next);
            remaining.Remove(next);
        }
        return ordered;

        IEnumerable<GuiModule> Dependencies(GuiModule module) =>
            module.DependedModules.Select(x => x.GetValueOrDefault("Id")).OfType<string>()
                .Select(x => byId.GetValueOrDefault(x)).OfType<GuiModule>().Where(x => x != module);
    }

    /// <summary>The base package when dlc is null, else the package of that DLC module.</summary>
    private PackedGui PackOne(PackageSpec spec, string? dlc, List<GuiModule> modules, string sourceFolder,
        PackContent content, Func<string?, bool> ownsModule)
    {
        var (build, scanner, movies, categories, objects) = content;
        var packageId = spec.PackageId(dlc is null ? BaseModule : $"{BaseModule}.{dlc}");
        var staging = Path.Combine(paths.Gui(spec.BuildId), packageId);
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);

        var files = new List<(string Source, string Target)>();
        var prefabs = new List<PrefabEntry>();
        foreach (var module in modules)
        foreach (var (source, target) in GuiFiles(Path.Combine(sourceFolder, "Modules", module.Folder), module.Folder).Where(x => x.Target.Contains("/GUI/Prefabs/", StringComparison.Ordinal)))
        {
            var file = Path.ChangeExtension(target["gui/".Length..], ".json");
            var name = Path.GetFileNameWithoutExtension(source);
            var document = PrefabTrees.Load(source);
            var tree = PrefabTrees.Write(document, name, module.Folder);
            // The tree must be the document the patches run against, or an XPath could answer otherwise.
            if (PrefabTrees.Difference(document, PrefabTrees.Rebuild(tree)) is { } difference)
                throw new InvalidDataException($"The tree of {module.Folder}/{target[$"gui/{module.Folder}/".Length..]} does not rebuild the prefab the game loads: {difference}");
            var treePath = Path.Combine(staging, "gui", file);
            Directory.CreateDirectory(Path.GetDirectoryName(treePath)!);
            File.WriteAllBytes(treePath, tree);
            files.Add((treePath, $"gui/{file}"));
            prefabs.Add(PrefabTrees.Entry(document, name, module.Folder, file));
        }
        var brushes = BrushReader.Read(sourceFolder, modules);
        var sprites = SpriteReader.Read(sourceFolder, modules);

        var manifest = new GuiManifest(FormatVersion, packageId, spec.GameVersion, spec.ChangeSet, spec.BuildId, modules);
        var ownMovies = new MovieSchema(FormatVersion,
            movies.Calls.Where(x => OwnsCall(x.ClassAssembly.Module, x.ViewModelAssembly?.Module, dlc is not null, ownsModule)).ToList(),
            movies.Unresolved.Where(x => ownsModule(x.ClassAssembly.Module)).ToList());
        var types = TypeSchemaReader.Read(build, scanner, x => ownsModule(x.Module), objects);
        var ownCategories = new SpriteCategorySchema(FormatVersion,
            categories.Always.Where(x => ownsModule(x.Module)).Concat(AlwaysLoadedBySpriteData(sourceFolder, modules))
                .OrderBy(x => x.Category, StringComparer.Ordinal).ThenBy(x => x.Caller, StringComparer.Ordinal).ToList(),
            categories.Missions.Where(x => ownsModule(x.Module)).ToList(),
            categories.Classes.Where(x => ownsModule(x.ClassAssembly.Module)).ToList(),
            categories.Unresolved.Where(x => ownsModule(x.ClassAssembly.Module)).ToList());
        var fonts = Fonts(sourceFolder, modules, includeEngine: dlc is null);
        var sounds = UiSounds(sourceFolder, modules);

        AddJson("manifest.json", manifest);
        AddJson("movies.json", ownMovies);
        AddJson("types.json", types);
        AddJson("spriteCategories.json", ownCategories);
        AddJson("fonts.json", fonts);
        AddJson("uiSounds.json", sounds);
        AddJson("prefabs.json", new PrefabIndex(FormatVersion, prefabs));
        AddJson("brushes.json", brushes);
        AddJson("sprites.json", sprites);
        files.Add((WriteText(staging, $"{packageId}.props", Props(packageId)), $"build/{packageId}.props"));

        Log.Info($"  {packageId}: {string.Join(", ", modules.Select(m => $"{m.Folder} {prefabs.Count(x => x.Module == m.Folder)}"))} prefab(s), {brushes.Brushes.Count} brush(es), "
                 + $"{sprites.Sprites.Count} sprite(s) in {sprites.Categories.Count} categories; "
                 + $"{ownMovies.Calls.Count} movie pair(s), {types.Widgets.Count} widget(s) ({types.Widgets.Count(x => x.Events.Count > 0)} raising events), "
                 + $"{types.ViewModels.Count} ViewModel(s), {types.Objects.Count} object type(s), {ownCategories.Classes.Count} class(es) loading sprite categories, "
                 + $"{fonts.Fonts.Count} font(s), {sounds.Sounds.Count} UI sound(s)");

        // The feed check reads the buildId and appId tags back; see NuGetFeed.
        var builder = NuGetPackages.New(
            packageId,
            spec.Version,
            dlc is null ? "Bannerlord Game GUI" : $"Bannerlord Game GUI: {dlc}",
            $"The UI of {(dlc is null ? "Mount & Blade II: Bannerlord" : $"the {dlc} DLC of Mount & Blade II: Bannerlord")} as data, for analyzers that check UI patches: a JSON tree of each prefab, the brushes and sprites, the movies it loads and the types it binds to. Carries none of the game's files, and adds nothing to compilation.",
            new[] { "bannerlord", "gui", "prefabs" }.Concat(spec.FeedTags).Append(spec.ModuleVersionTag(dlc)));
        builder.DevelopmentDependency = true;
        foreach (var (source, target) in files.OrderBy(x => x.Target, StringComparer.Ordinal))
            builder.Files.Add(new PhysicalPackageFile { SourcePath = source, TargetPath = target });

        Directory.CreateDirectory(paths.Final);
        var path = NuGetPackages.Save(builder, paths.Final);
        Log.Info($"  {Path.GetFileName(path)} ({new FileInfo(path).Length / 1024} KiB)");
        return new PackedGui(path, brushes, sprites);

        void AddJson<T>(string name, T value) => files.Add((Write(staging, name, value), $"gui/{name}"));
    }

    /// <summary>
    /// Whether a package has a movie call: the base package when neither the class nor the ViewModel is a DLC's,
    /// a DLC's package when either is its own. A base screen that a DLC hands a ViewModel of its own loads
    /// that pairing only with the DLC.
    /// </summary>
    internal static bool OwnsCall(string? classModule, string? viewModelModule, bool dlcPackage, Func<string?, bool> ownsModule) =>
        dlcPackage
            ? ownsModule(classModule) || viewModelModule is not null && ownsModule(viewModelModule)
            : ownsModule(classModule) && ownsModule(viewModelModule ?? classModule);

    /// <summary>
    /// The fonts the game loads. FontFactory.LoadAllFonts adds a font for every .fnt file in the Fonts folder
    /// of each resource folder, by its file name, and a brush naming any other font gets the language's
    /// default. Those folders are the engine's GUI/GauntletUI/Fonts, in the base package, and each module's
    /// GUI/Fonts. The Languages XML beside them only maps these fonts to other languages, and is not packed.
    /// </summary>
    internal static FontSchema Fonts(string sourceFolder, IEnumerable<GuiModule> modules, bool includeEngine)
    {
        var folders = new List<string>();
        if (includeEngine)
            folders.Add("GUI/GauntletUI/Fonts");
        folders.AddRange(modules.Select(x => $"Modules/{x.Folder}/GUI/Fonts"));

        var fonts = new SortedSet<string>(StringComparer.Ordinal);
        var sources = new List<string>();
        foreach (var folder in folders)
        {
            var path = Path.Combine(sourceFolder, folder);
            var found = Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.fnt", SearchOption.AllDirectories).Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList() : [];
            if (found.Count == 0)
                continue;
            fonts.UnionWith(found);
            sources.Add(folder);
        }
        return new FontSchema(FormatVersion, [.. fonts], sources);
    }

    /// <summary>
    /// The UI sound names: TwoDimensionEnginePlatform.PlaySound plays SoundEvent.PlaySound2D("event:/ui/" +
    /// the name), so the names a brush can give are the module sound events under that prefix, without it.
    /// Only the names are kept; the sound data is not packed.
    /// </summary>
    internal static UiSoundSchema UiSounds(string sourceFolder, IEnumerable<GuiModule> modules)
    {
        const string prefix = "event:/ui/";
        var sounds = new SortedSet<string>(StringComparer.Ordinal);
        var sources = new List<string>();
        foreach (var module in modules)
        {
            var data = Path.Combine(sourceFolder, "Modules", module.Folder, "ModuleData");
            if (!Directory.Exists(data))
                continue;
            foreach (var file in Directory.EnumerateFiles(data, "sound_event_data*.xml").Order(StringComparer.Ordinal))
            {
                var paths = XDocument.Load(file).Descendants("event").Select(x => (string?) x.Attribute("path")).OfType<string>()
                    .Where(x => x.StartsWith(prefix, StringComparison.Ordinal)).Select(x => x[prefix.Length..]).ToList();
                if (paths.Count == 0)
                    continue;
                sounds.UnionWith(paths);
                sources.Add($"{module.Folder}/ModuleData/{Path.GetFileName(file)}");
            }
        }
        return new UiSoundSchema(FormatVersion, prefix, [.. sounds], sources);
    }

    /// <summary>
    /// The categories a module's sprite data marks &lt;AlwaysLoad /&gt;: the engine loads them with the sprite
    /// data, before any screen, so they belong with the ones SubModules load at start.
    /// </summary>
    internal static IEnumerable<AlwaysLoadedCategory> AlwaysLoadedBySpriteData(string sourceFolder, IEnumerable<GuiModule> modules)
    {
        foreach (var module in modules)
        {
            var gui = Path.Combine(sourceFolder, "Modules", module.Folder, "GUI");
            if (!Directory.Exists(gui))
                continue;
            foreach (var file in Directory.EnumerateFiles(gui, "*SpriteData.xml").Order(StringComparer.Ordinal))
            {
                var categories = XDocument.Load(file).Root?.Element("SpriteCategories")?.Elements("SpriteCategory") ?? [];
                foreach (var category in categories.Where(x => x.Element("AlwaysLoad") is not null))
                    if ((string?) category.Element("Name") is { Length: > 0 } name)
                        yield return new AlwaysLoadedCategory(name.Trim(), module.Folder, null, $"<AlwaysLoad /> in {module.Folder}/GUI/{Path.GetFileName(file)}");
            }
        }
    }

    /// <summary>The prefab, brush and sprite data XML of a module the packages are written from, each with gui/&lt;module&gt;/ and the path it has in the module.</summary>
    internal static IEnumerable<(string Source, string Target)> GuiFiles(string moduleFolder, string module)
    {
        if (!Directory.Exists(moduleFolder))
            return [];
        return Directory.EnumerateFiles(moduleFolder, "*.xml", SearchOption.AllDirectories)
            .Select(x => (Source: x, Relative: Path.GetRelativePath(moduleFolder, x).Replace('\\', '/')))
            .Where(x => App.GuiFileFilter.IsMatch($"Modules/{module}/{x.Relative}"))
            .Select(x => (x.Source, $"gui/{module}/{x.Relative}"))
            .OrderBy(x => x.Item2, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The files as items, and nothing else: turning them into compiler input is the consumer's business,
    /// so the package does not tie itself to one analyzer. Every file is JSON, so one item type covers them;
    /// the pattern is recursive because the prefab trees sit below gui/, under their module.
    /// </summary>
    internal static string Props(string packageId) =>
        $"""
         <Project>
           <ItemGroup>
             <BannerlordGameGuiData Include="$(MSBuildThisFileDirectory)../gui/**/*.json" Visible="false" Package="{packageId}" />
           </ItemGroup>
         </Project>

         """.Replace("\r\n", "\n");

    /// <summary>
    /// The AutoGenerated assemblies compile each prefab into a class named &lt;Movie&gt;__&lt;ViewModel type&gt;,
    /// dots turned into underscores. Every such pair should be among the resolved calls, allowing for the
    /// call's ViewModel being a subclass of the one the prefab was generated for.
    /// </summary>
    private static void CheckAgainstGenerated(IReadOnlyList<(string Path, string? Module)> generated, MovieSchema movies)
    {
        if (generated.Count == 0)
            return;

        var byMovie = movies.Calls.ToLookup(x => x.Movie, StringComparer.Ordinal);
        var checkedCount = 0;
        var mismatches = new List<string>();
        foreach (var (path, _) in generated)
        {
            ModuleDefinition module;
            try
            {
                module = ModuleDefinition.FromFile(path);
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var type in module.GetAllTypes())
            {
                var name = type.Name?.ToString() ?? "";
                var split = name.IndexOf("__", StringComparison.Ordinal);
                // The prefabs a movie pulls in get classes of their own, named after the movie's with a
                // _Dependency_ suffix; only the movie's own class names the ViewModel it is loaded with.
                if (split <= 0 || type.DeclaringType is not null || name.Contains("_Dependency_", StringComparison.Ordinal))
                    continue;
                checkedCount++;
                var movie = name[..split];
                var viewModel = name[(split + 2)..];
                var candidates = byMovie[movie].ToList();
                if (candidates.Count == 0)
                    mismatches.Add($"{Path.GetFileName(path)}: {movie} is never loaded");
                else if (!candidates.Any(x => x.ViewModel is { } loaded && Mangle(loaded) == viewModel || x.ViewModelBases.Any(b => Mangle(b) == viewModel)))
                    mismatches.Add($"{Path.GetFileName(path)}: {movie} is generated for {viewModel} but loaded with {string.Join(", ", candidates.Select(x => x.ViewModel).Distinct())}");
            }
        }

        Log.Info($"  AutoGenerated check: {checkedCount} prefab class(es), {mismatches.Count} mismatch(es)");
        foreach (var mismatch in mismatches.Order(StringComparer.Ordinal))
            Log.Info($"    {mismatch}");

        static string Mangle(string type) => type.Replace('.', '_').Replace('+', '_');
    }

    private static string Write<T>(string folder, string name, T value) =>
        WriteText(folder, name, JsonSerializer.Serialize(value, JsonOptions) + "\n");

    private static string WriteText(string folder, string name, string text)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }
}
