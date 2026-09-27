using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;

using System.Text.Json.Serialization;

namespace Bannerlord.ReferenceAssemblies;

internal sealed record SpriteCategorySchema(
    int FormatVersion,
    List<AlwaysLoadedCategory> Always,
    List<AlwaysLoadedCategory> Missions,
    List<ClassCategories> Classes,
    List<UnresolvedCategory> Unresolved);

/// <summary>
/// A category loaded outside any view: by a SubModule when the game starts, or by the sprite data itself
/// through &lt;AlwaysLoad /&gt;, in which case there is no caller.
/// </summary>
internal sealed record AlwaysLoadedCategory(string Category, string? Module, string? Caller, string Via);

/// <summary>
/// The categories one concrete class loads itself, fully, and those it only prepares for a partial load,
/// which loads sheets one at a time. Module is the class's, as in movies.json. A class the loading class
/// creates, and that loads movies, gets the creator's categories too.
/// </summary>
internal sealed record ClassCategories(
    string Class,
    string? Module,
    List<string> Categories,
    List<string> Partial,
    List<string> Via,
    [property: JsonIgnore] GameAssembly ClassAssembly,
    [property: JsonIgnore] TypeDefinition ClassType);

internal sealed record UnresolvedCategory(string? Class, string? Module, string Caller, string Via, string Reason, [property: JsonIgnore] GameAssembly ClassAssembly);

internal sealed partial class BuildScanner
{
    private const string ResourceManagerType = "TaleWorlds.Engine.GauntletUI.UIResourceManager";
    private const string SpriteCategoryType = "TaleWorlds.TwoDimension.SpriteCategory";
    private const string SpriteDataType = "TaleWorlds.TwoDimension.SpriteData";
    private const string SubModuleType = "TaleWorlds.MountAndBlade.MBSubModuleBase";
    private const string MissionBehaviorType = "TaleWorlds.MountAndBlade.MissionBehavior";
    private const string AlwaysLoadStep = "every category the sprite data marks <AlwaysLoad />";

    /// <summary>
    /// The engine's own loading code, which only passes a caller's category along. The launcher
    /// (TaleWorlds.MountAndBlade.Launcher.Library.dll) is not among them, on purpose: its classes stay in, as
    /// its widgets and ViewModels do in types.json. Its ui_fonts_launcher category is defined in
    /// Native/LauncherGUI, which is not packed, so it is the one category no packed sprite data defines.
    /// </summary>
    private static readonly string[] CategoryImplementationAssemblies = ["TaleWorlds.Engine.GauntletUI.dll", "TaleWorlds.TwoDimension.dll"];

    /// <summary>The calls that load a category, and whether they load all of it or prepare a partial load.</summary>
    private enum CategoryLoad
    {
        /// <summary>UIResourceManager.LoadSpriteCategory(name): the argument is the name.</summary>
        ByName,

        /// <summary>SpriteCategory.Load(context, depot), or the Load(this SpriteCategory) extension: the object is the category.</summary>
        Full,

        /// <summary>SpriteCategory.InitializePartialLoad(): sheets are then loaded one at a time.</summary>
        Partial,
    }

    /// <summary>
    /// Which sprite categories the build's code loads: each concrete class's own loads, resolved per class
    /// as movies are, the loads a SubModule makes when the game starts, and those of the default views every
    /// mission gets. A category looked up and never loaded does not count. With the movies, a class that
    /// loads categories and creates a class that loads movies passes its categories on to it.
    /// </summary>
    public SpriteCategorySchema ScanSpriteCategories(MovieSchema? movies = null)
    {
        var always = new List<AlwaysLoadedCategory>();
        var missions = new List<AlwaysLoadedCategory>();
        var byClass = new Dictionary<TypeDefinition, (SortedSet<string> Full, SortedSet<string> Partial, SortedSet<string> Via)>();
        var unresolved = new List<UnresolvedCategory>();

        foreach (var (caller, index, load) in CategoryLoadSites())
        {
            var host = HostType(caller.DeclaringType!);
            var owner = _build.OwnerOf(host)!;
            var callerName = CallerName(caller);
            var args = FlowOf(caller).Arguments(index);
            var atStart = _build.DerivesFrom(host, SubModuleType);

            foreach (var concrete in atStart ? [host] : ConcreteClasses(caller, host))
            {
                var inEveryMission = !atStart && IsDefaultMissionView(concrete);
                var context = caller.IsStatic || host != caller.DeclaringType ? null : concrete;
                var origins = Resolve(caller, args[0], context, load == CategoryLoad.ByName ? Want.String : Want.Category, 0);
                var classAssembly = _build.OwnerOf(concrete) ?? owner;
                foreach (var origin in origins)
                {
                    if (!origin.Resolved)
                    {
                        unresolved.Add(new UnresolvedCategory(atStart ? null : TypeNames.Definition(concrete), classAssembly.Module, callerName, Via(origin), origin.Reason!, classAssembly));
                        continue;
                    }
                    if (origin.Value is not { Length: > 0 } category)
                        continue;

                    if (atStart)
                    {
                        always.Add(new AlwaysLoadedCategory(category, owner.Module, callerName, Via(origin)));
                        continue;
                    }
                    if (inEveryMission)
                    {
                        missions.Add(new AlwaysLoadedCategory(category, classAssembly.Module, callerName, $"default view {TypeNames.Definition(concrete)}: {Via(origin)}"));
                        continue;
                    }
                    var entry = byClass.GetOrAdd(concrete, () => ([], [], []));
                    (load == CategoryLoad.Partial ? entry.Partial : entry.Full).Add(category);
                    entry.Via.Add($"{category}: {callerName}: {Via(origin)}");
                }
            }
        }

        if (movies is not null)
            PassToCreatedClasses(byClass, movies);

        var classes = byClass
            .Select(x => new ClassCategories(TypeNames.Definition(x.Key), _build.OwnerOf(x.Key)?.Module, [.. x.Value.Full], [.. x.Value.Partial.Except(x.Value.Full)], [.. x.Value.Via], _build.OwnerOf(x.Key)!, x.Key))
            .OrderBy(x => x.Class, StringComparer.Ordinal)
            .ToList();
        return new SpriteCategorySchema(
            GuiPackager.FormatVersion,
            always.Distinct().OrderBy(x => x.Category, StringComparer.Ordinal).ThenBy(x => x.Caller, StringComparer.Ordinal).ThenBy(x => x.Via, StringComparer.Ordinal).ToList(),
            missions.Distinct().OrderBy(x => x.Category, StringComparer.Ordinal).ThenBy(x => x.Caller, StringComparer.Ordinal).ThenBy(x => x.Via, StringComparer.Ordinal).ToList(),
            classes,
            unresolved.DistinctBy(x => (x.Class, x.Caller, x.Via, x.Reason)).OrderBy(x => x.Caller, StringComparer.Ordinal).ThenBy(x => x.Class, StringComparer.Ordinal).ThenBy(x => x.Via, StringComparer.Ordinal).ToList());
    }

    /// <summary>The classes that gained categories from the class that creates them, for the report.</summary>
    public List<(string Creator, string Created)> CreatedClasses { get; } = [];

    /// <summary>
    /// One class loading the categories and another the movie: the encyclopedia view loads ui_encyclopedia
    /// and creates EncyclopediaData, which loads the encyclopedia's movies. A join of movies.json and this file
    /// on class would find nothing there, so a class created (newobj) by a class that loads categories, and
    /// that has movies of its own, gets the creator's categories, with a via naming the creator. One level.
    /// </summary>
    private void PassToCreatedClasses(Dictionary<TypeDefinition, (SortedSet<string> Full, SortedSet<string> Partial, SortedSet<string> Via)> byClass, MovieSchema movies)
    {
        var withMovies = movies.Calls.Select(x => x.Class).Concat(movies.Unresolved.Select(x => x.Class)).ToHashSet(StringComparer.Ordinal);
        foreach (var (creator, entry) in byClass.ToList())
        {
            var created = _build.SelfAndBases(creator)
                .SelectMany(x => x.Methods)
                .Where(x => x.CilMethodBody is not null)
                .SelectMany(x => x.CilMethodBody!.Instructions)
                .Where(x => x.OpCode.Code == CilCode.Newobj && x.Operand is IMethodDescriptor)
                .Select(x => _build.Resolve(((IMethodDescriptor) x.Operand!).DeclaringType as ITypeDefOrRef))
                .OfType<TypeDefinition>()
                .Where(x => x != creator && withMovies.Contains(TypeNames.Definition(x)))
                .Distinct()
                .OrderBy(x => x.FullName, StringComparer.Ordinal);
            foreach (var type in created)
            {
                var target = byClass.GetOrAdd(type, () => ([], [], []));
                target.Full.UnionWith(entry.Full);
                target.Partial.UnionWith(entry.Partial);
                foreach (var category in entry.Full.Concat(entry.Partial))
                    target.Via.Add($"{category}: loaded by {TypeNames.Definition(creator)}, which creates {TypeNames.Definition(type)}");
                CreatedClasses.Add((TypeNames.Definition(creator), TypeNames.Definition(type)));
            }
        }
    }

    /// <summary>
    /// Whether the class is one of the views every mission gets. MissionScreen asks
    /// ViewCreatorManager.CreateDefaultMissionBehaviors for them: each mission behavior marked
    /// [DefaultView], or the class that overrides one through [OverrideView], which runs in its place.
    /// </summary>
    private bool IsDefaultMissionView(TypeDefinition type)
    {
        if (!_build.DerivesFrom(type, MissionBehaviorType))
            return false;
        if (HasAttribute(type, "DefaultView"))
            return true;
        return TypeAttribute(type, "OverrideView") is { } overridden && _build.FindType(overridden) is { } defaultType && HasAttribute(defaultType, "DefaultView");
    }

    private static bool HasAttribute(TypeDefinition type, string name) => type.CustomAttributes.Any(x => IsAttribute(x, name));

    /// <summary>
    /// Whether an array of categories is the ones the sprite data marks AlwaysLoad, selected the way
    /// GauntletUISubModule.RefreshResources does: SpriteCategories.Values.Where(x => x.AlwaysLoad).ToArray().
    /// Those categories are read from the sprite data itself, so the load is known, not unresolved.
    /// </summary>
    private bool SelectsAlwaysLoad(MethodDefinition method, Flow flow, IReadOnlyList<int> array, int depth)
    {
        foreach (var producer in array.Where(x => x >= 0))
        {
            var instruction = flow.Code[producer];
            if (instruction.IsLdloc())
            {
                var local = instruction.GetLocalVariable(flow.Body.LocalVariables);
                for (var i = 0; i < flow.Code.Count; i++)
                    if (flow.Code[i].IsStloc() && flow.Code[i].GetLocalVariable(flow.Body.LocalVariables) == local && flow.IsReachable(i) && depth < MaxDepth
                        && SelectsAlwaysLoad(method, flow, flow.Stack(i)[^1], depth + 1))
                        return true;
                continue;
            }
            if (instruction.Operand is not IMethodDescriptor { Name.Value: "ToArray" or "ToList" } materialise || materialise.DeclaringType?.FullName != "System.Linq.Enumerable")
                continue;
            var source = flow.Arguments(producer);
            if (source.Count == 0)
                continue;
            foreach (var where in source[0].Where(x => x >= 0).Select(x => flow.Code[x]))
            {
                if (where.Operand is not IMethodDescriptor { Name.Value: "Where" } filter || filter.DeclaringType?.FullName != "System.Linq.Enumerable")
                    continue;
                // The predicate is a lambda of this method that reads SpriteCategory.AlwaysLoad.
                var lambdas = flow.Code.Where(x => x.OpCode.Code == CilCode.Ldftn).Select(x => _build.Resolve(x.Operand as IMethodDescriptor)).OfType<MethodDefinition>();
                if (lambdas.Any(x => x.CilMethodBody?.Instructions.Any(i => i.Operand is IFieldDescriptor { Name.Value: "AlwaysLoad" } or IMethodDescriptor { Name.Value: "get_AlwaysLoad" }) == true))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Every call that loads a sprite category, outside the engine code that implements loading.</summary>
    private IEnumerable<(MethodDefinition Caller, int Index, CategoryLoad Load)> CategoryLoadSites()
    {
        var loads = _callSites
            .Select(x => (x.Key, Sites: x.Value, Load: LoadOf(x.Key)))
            .Where(x => x.Load is not null)
            .OrderBy(x => x.Key, StringComparer.Ordinal);
        foreach (var (_, sites, load) in loads)
        {
            foreach (var (caller, index) in sites)
            {
                if (CategoryImplementationAssemblies.Contains(_build.OwnerOf(caller.DeclaringType!)?.FileName, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (!FlowOf(caller).IsReachable(index) || FlowOf(caller).Arguments(index).Count == 0)
                    continue;
                yield return (caller, index, load!.Value);
            }
        }

        static CategoryLoad? LoadOf(string key) => key switch
        {
            _ when key.StartsWith($"{ResourceManagerType}::LoadSpriteCategory(", StringComparison.Ordinal) => CategoryLoad.ByName,
            _ when key.StartsWith("TaleWorlds.Engine.GauntletUI.Extensions::Load(", StringComparison.Ordinal)
                   || key.StartsWith($"{SpriteCategoryType}::Load(", StringComparison.Ordinal) => CategoryLoad.Full,
            _ when key.StartsWith($"{SpriteCategoryType}::InitializePartialLoad(", StringComparison.Ordinal) => CategoryLoad.Partial,
            _ => null,
        };
    }

    /// <summary>
    /// Where a SpriteCategory comes from, as the name it is looked up by: UIResourceManager.GetSpriteCategory
    /// and LoadSpriteCategory, and an index into SpriteData.SpriteCategories. Null for any other call.
    /// </summary>
    private List<Origin>? CategoryLookup(MethodDefinition method, Flow flow, int index, string declaring, string name, IReadOnlyList<IReadOnlyList<int>> args,
        TypeDefinition? context, int depth)
    {
        if (declaring == ResourceManagerType && name is "GetSpriteCategory" or "LoadSpriteCategory" && args.Count == 1)
            return Resolve(method, args[0], context, Want.String, depth).Select(x => x.Prefix($"UIResourceManager.{name}")).ToList();

        // UIResourceManager.SpriteData.SpriteCategories[name]
        if (declaring.StartsWith("System.Collections.Generic.Dictionary", StringComparison.Ordinal) && name == "get_Item" && args.Count == 2
            && args[0].All(x => x >= 0 && flow.Code[x].OpCode.Code is CilCode.Call or CilCode.Callvirt
                                && flow.Code[x].Operand is IMethodDescriptor { Name.Value: "get_SpriteCategories" } getter && getter.DeclaringType?.FullName == SpriteDataType))
            return Resolve(method, args[1], context, Want.String, depth).Select(x => x.Prefix("SpriteData.SpriteCategories[...]")).ToList();

        return null;
    }
}
