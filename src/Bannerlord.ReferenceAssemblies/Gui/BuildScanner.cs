using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.DotNet.Signatures.Types;
using AsmResolver.PE.DotNet.Cil;

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Bannerlord.ReferenceAssemblies;

internal sealed record MovieSchema(int FormatVersion, List<MovieCall> Calls, List<UnresolvedMovieCall> Unresolved);

/// <summary>
/// A movie and the ViewModel it is loaded with, for one concrete class that makes the call. Module and
/// assembly are the class's: a DLC class inheriting a base screen's call is the DLC's. The ViewModel is null
/// for a movie loaded without a data source.
/// </summary>
internal sealed record MovieCall(
    string Movie,
    string? ViewModel,
    string? Module,
    string Assembly,
    string Caller,
    string Class,
    string? OverrideView,
    string? GameStateScreen,
    bool Paired,
    string Via,
    [property: JsonIgnore] GameAssembly ClassAssembly,
    [property: JsonIgnore] IReadOnlyList<string> ViewModelBases);

/// <summary>A movie name that comes from outside the build or is computed at runtime.</summary>
internal sealed record UnresolvedMovieCall(
    string? Module,
    string Assembly,
    string Caller,
    string Class,
    string? Movie,
    string? ViewModel,
    string Via,
    string Reason,
    [property: JsonIgnore] GameAssembly ClassAssembly);

/// <summary>What the scan saw, for the report: how the call sites reach their movie names.</summary>
internal sealed record MovieScanStats(int Sites, IReadOnlyDictionary<string, int> FirstStep, int DepthCapHits, int Truncated);

/// <summary>
/// Reads what only the method bodies of the real game assemblies say: which ViewModel each movie is loaded
/// with (movies.json), which sprite categories each class loads (spriteCategories.json), and which events
/// each widget raises (types.json). All three trace a call's arguments back to every literal they can be,
/// through locals, fields, constructor parameters, virtual members and registries. For movies the two
/// arguments of a call are traced together, so that a base class serving several subclasses pairs each
/// subclass's movie with that subclass's ViewModel.
/// </summary>
internal sealed partial class BuildScanner
{
    public const string LayerType = "TaleWorlds.Engine.GauntletUI.GauntletLayer";
    public const string LayerAssembly = "TaleWorlds.Engine.GauntletUI.dll";
    private const string StringType = "System.String";

    /// <summary>How far a trace may go. Real traces are a handful of steps; this only stops a runaway.</summary>
    private const int MaxDepth = 40;

    /// <summary>How many origins one value may have before the rest are dropped.</summary>
    private const int MaxOrigins = 256;

    private readonly GameAssemblies _build;
    private readonly Dictionary<string, List<(MethodDefinition Caller, int Index)>> _callSites = new(StringComparer.Ordinal);

    /// <summary>The call sites of the methods that put an element into a collection; see <see cref="ResolveElement"/>.</summary>
    private List<KeyValuePair<string, List<(MethodDefinition Caller, int Index)>>>? _collectionAdds;
    private readonly Dictionary<string, List<(MethodDefinition Method, int Index)>> _fieldStores = new(StringComparer.Ordinal);
    private readonly Dictionary<MethodDefinition, Flow> _flows = [];
    private readonly Dictionary<(MethodDefinition, int, TypeDefinition?, Want), List<Origin>> _memo = [];
    private readonly HashSet<(MethodDefinition, int, TypeDefinition?, Want)> _path = [];
    private readonly Dictionary<(MethodDefinition, string), (List<(MethodDefinition Method, int Index)> Stores, bool Unchanged)> _constructed = [];
    private readonly Dictionary<MethodDefinition, (IFieldDescriptor? Getter, IFieldDescriptor? Setter)> _accessors = [];
    private int _cycleCuts;
    private int _depthCapHits;
    private int _truncated;

    private enum Want
    {
        String,
        Type,

        /// <summary>A SpriteCategory, as the name of the category it is.</summary>
        Category,
    }

    /// <summary>
    /// One thing a value can be: a literal (a string, or a type for a ViewModel), or the reason it cannot be
    /// known. Choices records which call site or object each branch of the trace went through, so that two
    /// traces of one call can be matched up.
    /// </summary>
    private sealed record Origin(string? Value, TypeDefinition? Type, string? Reason, ImmutableList<string> Steps, ImmutableDictionary<string, string> Choices)
    {
        public bool Resolved => Reason is null;

        public Origin Prefix(string step) => this with { Steps = Steps.Insert(0, step) };

        public Origin Choose(string key, string value) => Choices.ContainsKey(key) ? this : this with { Choices = Choices.SetItem(key, value) };

        public static Origin Literal(string value, string step) => new(value, null, null, [step], ImmutableDictionary<string, string>.Empty);

        public static Origin OfType(string name, TypeDefinition? type, string step) => new(name, type, null, [step], ImmutableDictionary<string, string>.Empty);

        public static Origin Unknown(string reason, string step) => new(null, null, reason, [step], ImmutableDictionary<string, string>.Empty);

        /// <summary>No object at all: a movie loaded with a null data source.</summary>
        public static Origin Null(string step) => new(null, null, null, [step], ImmutableDictionary<string, string>.Empty);
    }

    public BuildScanner(GameAssemblies build)
    {
        _build = build;
        foreach (var assembly in build.Assemblies)
        foreach (var type in assembly.Definition.GetAllTypes())
        foreach (var method in type.Methods)
        {
            if (method.CilMethodBody is not { } body)
                continue;
            var code = body.Instructions;
            for (var i = 0; i < code.Count; i++)
            {
                switch (code[i].OpCode.Code)
                {
                    case CilCode.Call or CilCode.Callvirt or CilCode.Newobj when code[i].Operand is IMethodDescriptor target:
                        _callSites.GetOrAdd(GameAssemblies.MethodKey(target), () => []).Add((method, i));
                        break;
                    case CilCode.Stfld or CilCode.Stsfld when code[i].Operand is IFieldDescriptor field:
                        _fieldStores.GetOrAdd(GameAssemblies.FieldKey(field), () => []).Add((method, i));
                        break;
                }
            }
        }
    }

    public (MovieSchema Schema, MovieScanStats Stats) ScanMovies()
    {
        var calls = new List<MovieCall>();
        var unresolved = new List<UnresolvedMovieCall>();
        var firstSteps = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var sites = 0;

        foreach (var (caller, index) in LoadMovieSites())
        {
            sites++;
            var owner = _build.OwnerOf(caller.DeclaringType!)!;
            var host = HostType(caller.DeclaringType!);
            var flow = FlowOf(caller);
            var args = flow.Arguments(index);
            var callerName = CallerName(caller);

            foreach (var concrete in ConcreteClasses(caller, host))
            {
                // Inside a closure, `this` is the closure, so the object making the call is not known.
                var context = caller.IsStatic || host != caller.DeclaringType ? null : concrete;
                // An empty name loads nothing; the base encyclopedia page returns one for pages without a view.
                var movies = Resolve(caller, args[1], context, Want.String, 0).Where(x => x.Value is not "").ToList();
                // A null data source is recorded as none, but only when nothing else is ever passed: a field
                // cleared on the way out would otherwise add a null to every screen. A trace that could not
                // follow every branch, or found only the declared type, may be missing ViewModels, so its pairs
                // are not claimed as paired.
                var viewModelOrigins = Resolve(caller, args[2], context, Want.Type, 0);
                var complete = viewModelOrigins.Count > 0 && viewModelOrigins.All(x => x.Resolved);
                var viewModels = viewModelOrigins.Where(x => x.Resolved && x.Value is not null).ToList();
                if (viewModels.Count == 0 && complete)
                    viewModels.Add(viewModelOrigins[0]);
                if (viewModels.Count == 0)
                {
                    viewModels.Add(Origin.OfType(DeclaredType(caller, args[2]), null, "declared type"));
                    complete = false;
                }

                var classAssembly = _build.OwnerOf(concrete) ?? owner;
                var className = TypeNames.Definition(concrete);
                var overrideView = TypeAttribute(concrete, "OverrideView");
                var gameStateScreen = TypeAttribute(concrete, "GameStateScreen");

                var resolvedMovies = movies.Where(x => x.Resolved).ToList();
                var pairs = (from movie in resolvedMovies from viewModel in viewModels where Compatible(movie, viewModel) select (movie, viewModel)).ToList();
                var paired = true;
                if (pairs.Count == 0 && resolvedMovies.Count > 0)
                {
                    pairs = (from movie in resolvedMovies from viewModel in viewModels select (movie, viewModel)).ToList();
                    paired = false;
                }
                else if (resolvedMovies.Count > 1 && viewModels.Count > 1 && pairs.Count == resolvedMovies.Count * viewModels.Count)
                {
                    // Nothing tied the two traces together, so every combination stands.
                    paired = false;
                }
                paired &= complete;

                foreach (var (movie, viewModel) in pairs)
                {
                    var bases = viewModel.Type is { } viewModelType ? _build.SelfAndBases(viewModelType).Skip(1).Select(TypeNames.Definition).ToList() : [];
                    calls.Add(new MovieCall(movie.Value!, viewModel.Value, classAssembly.Module, classAssembly.FileName, callerName, className, overrideView, gameStateScreen, paired, Via(movie), classAssembly, bases));
                }

                foreach (var movie in movies.Where(x => !x.Resolved))
                    unresolved.Add(new UnresolvedMovieCall(classAssembly.Module, classAssembly.FileName, callerName, className, null, viewModels.FirstOrDefault()?.Value, Via(movie), movie.Reason!, classAssembly));
            }

            // How this site reaches its names, once per site rather than once per class.
            var first = Resolve(caller, args[1], null, Want.String, 0).Select(x => x.Steps.Count == 1 && x.Resolved ? "literal" : StepKind(x.Steps[0])).Distinct();
            foreach (var kind in first)
                firstSteps[kind] = firstSteps.GetValueOrDefault(kind) + 1;
        }

        var schema = new MovieSchema(
            GuiPackager.FormatVersion,
            calls.DistinctBy(x => (x.Movie, x.ViewModel, x.Caller, x.Class, x.Paired, x.Via)).OrderBy(x => x.Movie, StringComparer.Ordinal).ThenBy(x => x.ViewModel, StringComparer.Ordinal).ThenBy(x => x.Class, StringComparer.Ordinal).ThenBy(x => x.Caller, StringComparer.Ordinal).ThenBy(x => x.Via, StringComparer.Ordinal).ToList(),
            unresolved.DistinctBy(x => (x.Caller, x.Class, x.ViewModel, x.Via, x.Reason)).OrderBy(x => x.Caller, StringComparer.Ordinal).ThenBy(x => x.Class, StringComparer.Ordinal).ThenBy(x => x.Via, StringComparer.Ordinal).ToList());
        return (schema, new MovieScanStats(sites, firstSteps, _depthCapHits, _truncated));
    }

    /// <summary>
    /// Every call of GauntletLayer.LoadMovie(string, ViewModel) outside the layer's own assembly, whose
    /// calls only pass their caller's arguments along. The return type changed over the versions, so only
    /// the declaring type, the name and the parameter types are matched.
    /// </summary>
    private IEnumerable<(MethodDefinition Caller, int Index)> LoadMovieSites()
    {
        const string loadMovie = $"{LayerType}::LoadMovie(";
        foreach (var (_, sites) in _callSites.Where(x => x.Key.StartsWith(loadMovie, StringComparison.Ordinal)).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            foreach (var site in sites)
            {
                if (site.Caller.CilMethodBody!.Instructions[site.Index].Operand is not IMethodDescriptor { Signature: { } signature } || !IsLoadMovie(signature))
                    continue;
                if (string.Equals(_build.OwnerOf(site.Caller.DeclaringType!)?.FileName, LayerAssembly, StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return site;
            }
        }

        static bool IsLoadMovie(MethodSignature signature) =>
            signature.HasThis
            && signature.ParameterTypes.Count == 2
            && TypeNames.Format(signature.ParameterTypes[0], null) == StringType
            && TypeNames.Format(signature.ParameterTypes[1], null) == TypeSchemaReader.ViewModelType;
    }

    /// <summary>The type a compiler-generated closure or state machine belongs to.</summary>
    private static TypeDefinition HostType(TypeDefinition type)
    {
        while (type.Name?.ToString().StartsWith('<') == true && type.DeclaringType is { } outer)
            type = outer;
        return type;
    }

    /// <summary>
    /// The classes a call is resolved for: every concrete class that runs the calling method, which is the
    /// declaring type and each subclass that inherits the method or calls into it from an override.
    /// </summary>
    private IEnumerable<TypeDefinition> ConcreteClasses(MethodDefinition caller, TypeDefinition host)
    {
        var classes = _build.SelfAndDerived(host)
            .Where(x => !x.IsAbstract && !x.IsInterface)
            .Where(x => host != caller.DeclaringType || !caller.IsVirtual || MostDerived(x, caller) is var impl && (impl == caller || Calls(impl, caller)))
            .OrderBy(x => x.FullName, StringComparer.Ordinal)
            .ToList();
        return classes.Count > 0 ? classes : [host];
    }

    private static bool Calls(MethodDefinition method, MethodDefinition target)
    {
        var key = GameAssemblies.MethodKey(target);
        return method.CilMethodBody?.Instructions.Any(x => x.OpCode.Code is CilCode.Call && x.Operand is IMethodDescriptor called && GameAssemblies.MethodKey(called) == key) == true;
    }

    private static bool Compatible(Origin a, Origin b) =>
        a.Choices.All(x => !b.Choices.TryGetValue(x.Key, out var other) || other == x.Value);

    private static string Via(Origin origin) => string.Join(" <- ", origin.Steps);

    private static string StepKind(string step) => step.Split(' ', 2)[0];

    /// <summary>
    /// Every origin of the values the given instructions push. The context is the concrete class whose
    /// `this` the method runs on, when that is known.
    /// </summary>
    private List<Origin> Resolve(MethodDefinition method, IReadOnlyList<int> producers, TypeDefinition? context, Want want, int depth)
    {
        var result = new List<Origin>();
        foreach (var producer in producers)
        {
            result.AddRange(Resolve(method, producer, context, want, depth));
            if (result.Count > MaxOrigins)
            {
                _truncated++;
                return result.Take(MaxOrigins).ToList();
            }
        }
        return result;
    }

    private List<Origin> Resolve(MethodDefinition method, int producer, TypeDefinition? context, Want want, int depth)
    {
        if (producer < 0)
            return [Origin.Unknown("built from a runtime value", "exception object")];

        var key = (method, producer, context, want);
        if (_memo.TryGetValue(key, out var known))
            return known;
        if (!_path.Add(key))
        {
            // A cycle: the values on it come in through its other edges, which the trace follows anyway.
            _cycleCuts++;
            return [];
        }
        if (depth > MaxDepth)
        {
            _depthCapHits++;
            _path.Remove(key);
            return [Origin.Unknown($"trace deeper than {MaxDepth} steps", "depth cap")];
        }

        var cuts = _cycleCuts;
        List<Origin> result;
        try
        {
            result = ResolveInstruction(method, producer, context, want, depth + 1);
        }
        finally
        {
            _path.Remove(key);
        }

        // A result cut short by a cycle depends on where the trace entered the cycle, so it is not kept.
        if (cuts == _cycleCuts)
            _memo[key] = result;
        return result;
    }

    private List<Origin> ResolveInstruction(MethodDefinition method, int index, TypeDefinition? context, Want want, int depth)
    {
        var flow = FlowOf(method);
        var instruction = flow.Code[index];

        if (instruction.IsLdloc())
            return ResolveLocal(method, flow, instruction.GetLocalVariable(flow.Body.LocalVariables), context, want, depth);
        if (instruction.IsLdarg())
            return ResolveArgument(method, instruction.GetParameter(method.Parameters), context, want, depth);

        switch (instruction.OpCode.Code)
        {
            case CilCode.Ldstr:
                return want == Want.String ? [Origin.Literal((string) instruction.Operand!, Quote((string) instruction.Operand!))] : [];

            case CilCode.Ldnull:
                return want == Want.Type ? [Origin.Null("null")] : [];

            case CilCode.Ldfld or CilCode.Ldsfld when instruction.Operand is IFieldDescriptor field:
                var isStatic = instruction.OpCode.Code == CilCode.Ldsfld;
                return ResolveField(method, flow, index, field, isStatic, isStatic ? [] : flow.Stack(index)[^1], context, want, depth);

            case CilCode.Call or CilCode.Callvirt or CilCode.Newobj when instruction.Operand is IMethodDescriptor target:
                return ResolveCall(method, flow, index, target, context, want, depth);

            case CilCode.Castclass or CilCode.Isinst or CilCode.Unbox_Any or CilCode.Box:
                return Resolve(method, flow.Stack(index)[^1], context, want, depth);

            case CilCode.Ldtoken when want == Want.Type:
                return ResolveTypeToken(method, instruction.Operand, context, depth);

            case CilCode.Ldelem_Ref or CilCode.Ldelem:
                if (want == Want.Category && SelectsAlwaysLoad(method, flow, flow.Stack(index)[^2], depth))
                    return [Origin.Null(AlwaysLoadStep)];
                return [Origin.Unknown("built from a runtime value", $"array element in {Display(method)}")];

            default:
                return [Origin.Unknown("built from a runtime value", $"{instruction.OpCode.Mnemonic} in {Display(method)}")];
        }
    }

    private List<Origin> ResolveLocal(MethodDefinition method, Flow flow, CilLocalVariable? local, TypeDefinition? context, Want want, int depth)
    {
        var step = $"local {LocalName(local)}";
        if (local is null)
            return [Origin.Unknown("built from a runtime value", step)];

        var stores = Enumerable.Range(0, flow.Code.Count)
            .Where(i => flow.Code[i].IsStloc() && flow.Code[i].GetLocalVariable(flow.Body.LocalVariables) == local && flow.IsReachable(i))
            .ToList();
        var addressTaken = flow.Code.Any(x => x.OpCode.Code is CilCode.Ldloca or CilCode.Ldloca_S && x.Operand == local);

        var result = new List<Origin>();
        foreach (var store in stores)
            result.AddRange(Resolve(method, flow.Stack(store)[^1], context, want, depth).Select(x => x.Prefix(step)));
        if (addressTaken)
            result.Add(Origin.Unknown("built from a runtime value", $"{step}, written through its address"));
        return result;
    }

    private List<Origin> ResolveArgument(MethodDefinition method, Parameter? parameter, TypeDefinition? context, Want want, int depth)
    {
        if (parameter is null)
            return [Origin.Unknown("built from a runtime value", $"argument of {Display(method)}")];

        // `this` is only ever a ViewModel when the class loading the movie is one itself.
        if (parameter == method.Parameters.ThisParameter)
        {
            var self = context ?? method.DeclaringType!;
            return want == Want.Type ? [Origin.OfType(TypeNames.Definition(self), self, "this")] : [Origin.Unknown("built from a runtime value", "this")];
        }

        var step = $"parameter {parameter.Name ?? $"#{parameter.Index}"} of {Display(method)}";
        var parameterChoice = $"P:{GameAssemblies.MethodKey(method)}";
        var objectChoice = $"O:{method.DeclaringType!.FullName}";
        var chain = context is null ? null : _build.SelfAndBases(context).ToHashSet();
        var result = new List<Origin>();
        var sites = SitesCalling(method).ToList();
        foreach (var (caller, index) in sites)
        {
            var callFlow = FlowOf(caller);
            if (!callFlow.IsReachable(index))
                continue;
            var instruction = callFlow.Code[index];
            var args = callFlow.Arguments(index);
            var isNew = instruction.OpCode.Code == CilCode.Newobj;

            // A constructor of the context's own chain, reached through base(...) or this(...) from another
            // constructor of the chain, runs on the same object; anywhere else it is some other object.
            TypeDefinition? siteContext = null;
            if (method.IsConstructor && chain is not null && chain.Contains(method.DeclaringType!))
            {
                var isChainCall = !isNew && caller.IsConstructor && chain.Contains(caller.DeclaringType!) && callFlow.IsThis(args[0]);
                if (isChainCall)
                    siteContext = context;
                else if (!(isNew && method.DeclaringType == context))
                    continue;
            }
            else if (!method.IsStatic && !method.IsConstructor && !isNew && chain is not null)
            {
                // Called on `this` by the chain, it runs on the same object; by a sibling class, on another.
                // Called on another object, it runs on this class's object only if that object can be one.
                if (callFlow.IsThis(args[0]))
                {
                    if (!chain.Contains(caller.DeclaringType!))
                        continue;
                    siteContext = context;
                }
                else if (!CouldBe(caller, args[0], context!, depth))
                {
                    continue;
                }
            }

            // A call pops `this` first; newobj creates it instead.
            var argument = parameter.Index + (!isNew && !method.IsStatic ? 1 : 0);
            if (argument >= args.Count)
                continue;

            var site = SiteId(caller, index);
            var siteStep = $"{Display(caller)}: {(isNew ? "new" : method.IsConstructor ? "base(...)" : "call")}";
            var objectId = isNew ? site : !method.IsStatic ? string.Join("|", args[0].Select(x => SiteId(caller, x))) : null;

            foreach (var origin in Resolve(caller, args[argument], siteContext, want, depth))
            {
                var chosen = origin.Prefix(siteStep).Prefix(step).Choose(parameterChoice, site);
                if (objectId is not null)
                    chosen = chosen.Choose(objectChoice, objectId);
                result.Add(chosen);
            }
        }

        if (result.Count == 0 && sites.Count == 0)
            result.Add(Origin.Unknown("called from outside the build or through a delegate", step));
        return result;
    }

    /// <summary>
    /// The values a field read can see. On `this` of a known class that is, in order of preference: what the
    /// reading method itself stored on every path to the read; otherwise what the class's constructor chain
    /// leaves, which is the last store on each path through it, so a subclass overwriting a base initializer
    /// replaces it; together with the stores made outside the constructors, which may run at any time.
    /// Anywhere else, every store in the build. An auto-property counts as its backing field.
    /// </summary>
    private List<Origin> ResolveField(MethodDefinition method, Flow flow, int index, IFieldDescriptor field, bool isStatic, IReadOnlyList<int> receiver,
        TypeDefinition? context, Want want, int depth)
    {
        var step = FieldStep(field);
        var fieldKey = GameAssemblies.FieldKey(field);
        var readsThis = !isStatic && flow.IsThis(receiver);
        var chain = readsThis && context is not null ? _build.SelfAndBases(context).ToHashSet() : null;

        List<(MethodDefinition Method, int Index, TypeDefinition? Context)>? stores = null;
        if (isStatic || readsThis)
            stores = StoresBefore(method, flow, index, fieldKey, isStatic)?.Select(x => (x.Method, x.Index, isStatic ? null : context)).ToList();
        if (stores is not { Count: > 0 })
            stores = chain is not null ? ObjectStores(context!, chain, fieldKey) : AnyStores(fieldKey);

        var result = new List<Origin>();
        foreach (var (storer, store, storeContext) in stores)
        {
            var storeFlow = FlowOf(storer);
            if (!storeFlow.IsReachable(store))
                continue;
            var stack = storeFlow.Stack(store);
            if (stack.Count < (isStatic ? 1 : 2))
                continue;
            var storeOnThis = !isStatic && storeFlow.IsThis(stack[^2]);
            var objectId = !isStatic && !storeOnThis ? string.Join("|", stack[^2].Select(x => SiteId(storer, x))) : null;
            foreach (var origin in Resolve(storer, stack[^1], storeContext, want, depth))
            {
                var chosen = origin.Prefix(step);
                if (objectId is not null)
                    chosen = chosen.Choose($"O:{field.DeclaringType?.FullName}", objectId);
                result.Add(chosen);
            }
        }

        if (stores.Count == 0)
            result.Add(Origin.Unknown(NoStoreReason(field, fieldKey), step));
        return result;
    }

    /// <summary>
    /// Why a field has no store to trace. A widget property with a public setter and nothing in the code
    /// setting it takes its value from the prefab XML, which is data, not code.
    /// </summary>
    private string NoStoreReason(IFieldDescriptor field, string fieldKey)
    {
        if (_build.Resolve(field) is not { DeclaringType: { } declaring })
            return "built from a runtime value";
        var fromXml = _build.DerivesFrom(declaring, TypeSchemaReader.WidgetType)
                      && declaring.Properties.Any(x => x.SetMethod is { IsPublic: true } setter && Accessors(setter).Setter is { } set && GameAssemblies.FieldKey(set) == fieldKey);
        return fromXml ? "set from the prefab XML" : "never assigned in the build";
    }

    /// <summary>
    /// The stores a method made to the field before the given instruction, when every path to it passes
    /// one; null when some path reaches the method's start first. In a constructor, the base(...) or
    /// this(...) call stands for whatever that constructor stores, and a path goes on past it where that
    /// constructor may leave the field alone.
    /// </summary>
    private List<(MethodDefinition Method, int Index)>? StoresBefore(MethodDefinition method, Flow flow, int index, string fieldKey, bool isStatic)
    {
        var result = new List<(MethodDefinition, int)>();
        var reachesStart = flow.WalkBack(index, i =>
        {
            if (IsStore(flow, i, fieldKey, isStatic))
            {
                result.Add((method, i));
                return true;
            }
            if (isStatic || IsChainConstructorCall(method, flow, i) is not { } constructor)
                return false;
            var (stores, unchanged) = ConstructedStores(constructor, fieldKey);
            result.AddRange(stores);
            return !unchanged;
        });
        return reachesStart || result.Count == 0 ? null : result;
    }

    /// <summary>
    /// The stores whose value a constructor leaves in the field when it returns, through its base(...) and
    /// this(...) calls, and whether some path through it leaves the field as it found it.
    /// </summary>
    private (List<(MethodDefinition Method, int Index)> Stores, bool Unchanged) ConstructedStores(MethodDefinition constructor, string fieldKey)
    {
        if (_constructed.TryGetValue((constructor, fieldKey), out var known))
            return known;
        _constructed[(constructor, fieldKey)] = ([], true);
        if (constructor.CilMethodBody is null)
            return ([], true);

        var flow = FlowOf(constructor);
        var stores = new List<(MethodDefinition, int)>();
        var unchanged = false;
        for (var i = 0; i < flow.Code.Count; i++)
        {
            if (flow.Code[i].OpCode.Code != CilCode.Ret || !flow.IsReachable(i))
                continue;
            if (StoresBefore(constructor, flow, i, fieldKey, isStatic: false) is { } found)
                stores.AddRange(found);
            else
                unchanged = true;
        }
        return _constructed[(constructor, fieldKey)] = (stores.Distinct().ToList(), unchanged || stores.Count == 0);
    }

    /// <summary>
    /// The stores that can have set the field of an object of the given class: what its constructors leave,
    /// what the chain's other methods store into the object at any later time, and what other code stores
    /// into some object of the type from outside.
    /// </summary>
    private List<(MethodDefinition Method, int Index, TypeDefinition? Context)> ObjectStores(TypeDefinition context, HashSet<TypeDefinition> chain, string fieldKey)
    {
        var result = new List<(MethodDefinition, int, TypeDefinition?)>();
        foreach (var constructor in context.Methods.Where(x => x.IsConstructor && !x.IsStatic))
            result.AddRange(ConstructedStores(constructor, fieldKey).Stores.Select(x => (x.Method, x.Index, (TypeDefinition?) context)));

        foreach (var (storer, store) in _fieldStores.GetValueOrDefault(fieldKey) ?? [])
        {
            var storeFlow = FlowOf(storer);
            if (!storeFlow.IsReachable(store))
                continue;
            var stack = storeFlow.Stack(store);
            if (stack.Count < 2)
                continue;
            if (!storeFlow.IsThis(stack[^2]))
            {
                if (CouldBe(storer, stack[^2], context, 0))
                    result.Add((storer, store, null));
            }
            else if (chain.Contains(storer.DeclaringType!) && !storer.IsConstructor && !(Accessors(storer).Setter is { } own && GameAssemblies.FieldKey(own) == fieldKey))
                result.Add((storer, store, context));
        }

        // An auto-property's setter is a store at each of its call sites.
        foreach (var setter in chain.SelectMany(x => x.Methods).Where(x => Accessors(x).Setter is { } field && GameAssemblies.FieldKey(field) == fieldKey))
        {
            foreach (var (caller, site) in SitesCalling(setter))
            {
                var callFlow = FlowOf(caller);
                if (!callFlow.IsReachable(site) || callFlow.Arguments(site) is not { Count: 2 } args)
                    continue;
                if (!callFlow.IsThis(args[0]))
                {
                    if (CouldBe(caller, args[0], context, 0))
                        result.Add((caller, site, null));
                }
                else if (chain.Contains(caller.DeclaringType!) && !caller.IsConstructor)
                    result.Add((caller, site, context));
            }
        }
        return result.Distinct().ToList();
    }

    /// <summary>
    /// Whether an object other than `this` can be of the given class. It cannot when every origin of it is an
    /// object created as some other class; anything the trace cannot pin down might be.
    /// </summary>
    private bool CouldBe(MethodDefinition method, IReadOnlyList<int> receiver, TypeDefinition type, int depth)
    {
        var origins = Resolve(method, receiver, null, Want.Type, depth);
        return origins.Count == 0 || origins.Any(x => !x.Resolved || x.Type is null || x.Type == type);
    }

    /// <summary>Every store to the field in the build, with no object known.</summary>
    private List<(MethodDefinition Method, int Index, TypeDefinition? Context)> AnyStores(string fieldKey) =>
        (_fieldStores.GetValueOrDefault(fieldKey) ?? []).Select(x => (x.Method, x.Index, (TypeDefinition?) null)).ToList();

    /// <summary>Whether the instruction stores the field, on `this` for an instance field, directly or through an auto-property's setter.</summary>
    private bool IsStore(Flow flow, int index, string fieldKey, bool isStatic)
    {
        var instruction = flow.Code[index];
        var stack = flow.Stack(index);
        switch (instruction.OpCode.Code)
        {
            case CilCode.Stfld or CilCode.Stsfld when instruction.Operand is IFieldDescriptor stored && GameAssemblies.FieldKey(stored) == fieldKey:
                return isStatic || stack.Count >= 2 && flow.IsThis(stack[^2]);
            case CilCode.Call or CilCode.Callvirt when !isStatic && instruction.Operand is IMethodDescriptor called
                                                       && _build.Resolve(called) is { } setter && Accessors(setter).Setter is { } field && GameAssemblies.FieldKey(field) == fieldKey:
                return stack.Count >= 2 && flow.IsThis(stack[^2]);
            default:
                return false;
        }
    }

    /// <summary>The constructor a constructor's base(...) or this(...) call runs, or null for any other instruction.</summary>
    private MethodDefinition? IsChainConstructorCall(MethodDefinition method, Flow flow, int index)
    {
        if (!method.IsConstructor || flow.Code[index].OpCode.Code != CilCode.Call || flow.Code[index].Operand is not IMethodDescriptor called)
            return null;
        if (_build.Resolve(called) is not { IsConstructor: true, IsStatic: false } constructor)
            return null;
        var args = flow.Arguments(index);
        return args.Count > 0 && flow.IsThis(args[0]) ? constructor : null;
    }

    /// <summary>
    /// The backing field of a method that only reads it (ldarg.0, ldfld, ret), as an auto-property's getter
    /// does, or of a property setter that stores its value straight into a field (ldarg.0, ldarg.1, stfld),
    /// whatever else it does besides, as an auto-property's setter and most widget setters do.
    /// </summary>
    private (IFieldDescriptor? Getter, IFieldDescriptor? Setter) Accessors(MethodDefinition method)
    {
        if (_accessors.TryGetValue(method, out var known))
            return known;

        (IFieldDescriptor?, IFieldDescriptor?) result = (null, null);
        if (!method.IsStatic && method.CilMethodBody is { } body)
        {
            var code = body.Instructions.Where(x => x.OpCode.Code != CilCode.Nop).ToList();
            if (code.Count == 3 && code[0].IsLdarg() && code[0].GetParameter(method.Parameters) == method.Parameters.ThisParameter
                && code[1].OpCode.Code == CilCode.Ldfld && code[2].OpCode.Code == CilCode.Ret)
                result = ((IFieldDescriptor) code[1].Operand!, null);
            else if (method.IsSetMethod && method.Parameters.Count == 1)
            {
                for (var i = 2; i < code.Count; i++)
                {
                    if (code[i].OpCode.Code == CilCode.Stfld && code[i - 1].IsLdarg() && code[i - 1].GetParameter(method.Parameters) == method.Parameters[0]
                        && code[i - 2].IsLdarg() && code[i - 2].GetParameter(method.Parameters) == method.Parameters.ThisParameter)
                    {
                        result = (null, (IFieldDescriptor) code[i].Operand!);
                        break;
                    }
                }
            }
        }
        return _accessors[method] = result;
    }

    /// <summary>A field by its name, an auto-property's backing field by the property's.</summary>
    private static string FieldStep(IFieldDescriptor field) =>
        field.Name?.ToString() is { } name && name.StartsWith('<') && name.IndexOf(">k__BackingField", StringComparison.Ordinal) is > 1 and var end
            ? $"property {name[1..end]}"
            : $"field {field.Name}";

    private List<Origin> ResolveCall(MethodDefinition method, Flow flow, int index, IMethodDescriptor target, TypeDefinition? context, Want want, int depth)
    {
        var code = flow.Code[index].OpCode.Code;
        var args = flow.Arguments(index);
        var declaring = target.DeclaringType is ITypeDefOrRef type ? TypeNames.Format(type) : target.DeclaringType?.FullName ?? "";
        var name = target.Name?.ToString() ?? "";

        if (code == CilCode.Newobj)
        {
            if (want == Want.String)
                return [Origin.Unknown("built from a runtime value", $"new {declaring}")];
            var created = _build.Resolve(target.DeclaringType as ITypeDefOrRef);
            var createdName = target.DeclaringType is ITypeDefOrRef reference ? TypeNames.Format(reference, method.DeclaringType) : declaring;
            return [Origin.OfType(createdName, created, $"new {createdName}")];
        }

        if (want == Want.Category && CategoryLookup(method, flow, index, declaring, name, args, context, depth) is { } category)
            return category;

        switch (declaring, name)
        {
            case (StringType, "Concat"):
                return Concat(method, args, context, depth);
            case (StringType, "Format"):
                return Format(method, args, context, depth);
            case ("System.Type", "GetTypeFromHandle") when want == Want.Type:
                return Resolve(method, args[0], context, want, depth);
            case ("System.Activator", "CreateInstance") when want == Want.Type:
                return target is MethodSpecification { Signature.TypeArguments.Count: 1 } generic
                    ? ResolveTypeSignature(method, generic.Signature.TypeArguments[0], context, depth, "Activator.CreateInstance<T>")
                    : Resolve(method, args[0], context, want, depth).Select(x => x.Prefix("Activator.CreateInstance")).ToList();
        }

        if (declaring.StartsWith("System.Collections.Generic.", StringComparison.Ordinal) && name is "get_Item" or "TryGetValue" or "get_Value" or "First" or "FirstOrDefault")
            return ResolveElement(method, flow, index, target, context, want, depth);

        if (_build.Resolve(target) is not { } definition)
            return [Origin.Unknown(ExternalReason(declaring), $"{declaring}.{name}")];

        var onThis = context is not null && !definition.IsStatic && args.Count > 0 && flow.IsThis(args[0]);
        IEnumerable<MethodDefinition> implementations;
        if (code == CilCode.Callvirt && definition.IsVirtual)
            implementations = onThis ? [MostDerived(context!, definition)] : Implementations(definition);
        else
            implementations = [definition];

        // An auto-property is a field read where it is called, so the caller's own stores before the read count.
        var candidates = implementations.Distinct().ToList();
        if (candidates is [var only] && Accessors(only).Getter is { } backing)
            return ResolveField(method, flow, index, backing, false, args[0], context, want, depth);

        var result = new List<Origin>();
        var virtualChoice = $"V:{GameAssemblies.MethodKey(definition)}";
        foreach (var implementation in candidates)
        {
            if (implementation.CilMethodBody is null)
                continue;
            var implementationFlow = FlowOf(implementation);
            var step = $"return of {Display(implementation)}";
            var implementationContext = onThis ? context : null;
            var implementationType = implementation.DeclaringType!.FullName;
            for (var i = 0; i < implementationFlow.Code.Count; i++)
            {
                if (implementationFlow.Code[i].OpCode.Code != CilCode.Ret || !implementationFlow.IsReachable(i) || implementationFlow.Stack(i).Count == 0)
                    continue;
                foreach (var origin in Resolve(implementation, implementationFlow.Stack(i)[^1], implementationContext, want, depth))
                    result.Add(origin.Prefix(step).Choose(virtualChoice, implementationType));
            }
        }

        if (result.Count == 0 && candidates.All(x => x.CilMethodBody is null))
            result.Add(Origin.Unknown("built from a runtime value", $"{Display(definition)} has no body in the build"));
        return result;
    }

    /// <summary>
    /// An element read from a collection: every value added to that collection, when it is a field of the
    /// build. The record the tooltips keep in a registry is reached through its own fields instead, by
    /// whatever the caller reads off the element.
    /// </summary>
    private List<Origin> ResolveElement(MethodDefinition method, Flow flow, int index, IMethodDescriptor target, TypeDefinition? context, Want want, int depth)
    {
        var args = flow.Arguments(index);
        var receiver = args[0].Select(x => flow.Code[x]).Where(x => x.OpCode.Code is CilCode.Ldfld or CilCode.Ldsfld).Select(x => x.Operand).OfType<IFieldDescriptor>().ToList();
        var step = $"element of {(receiver.Count > 0 ? $"field {receiver[0].Name}" : "a collection")}";
        if (receiver.Count == 0 || target.Name == "TryGetValue")
            return [Origin.Unknown("built from a runtime value", step)];

        var fieldKeys = receiver.Select(GameAssemblies.FieldKey).ToHashSet(StringComparer.Ordinal);
        var collection = target.DeclaringType is { } type ? $"{GameAssemblies.BaseName(type)}::" : "";
        // Every key the test below takes ends its method name so; the rest of the index is never looked at.
        _collectionAdds ??= _callSites.Where(x => x.Key.Split('(')[0] is var name
            && (name.EndsWith("::Add", StringComparison.Ordinal) || name.EndsWith("::set_Item", StringComparison.Ordinal)
                || name.EndsWith("::TryAdd", StringComparison.Ordinal) || name.EndsWith("::Insert", StringComparison.Ordinal))).ToList();
        var result = new List<Origin>();
        foreach (var (key, sites) in _collectionAdds)
        {
            if (!key.StartsWith(collection, StringComparison.Ordinal) || key[collection.Length..].Split('(')[0] is not ("Add" or "set_Item" or "TryAdd" or "Insert"))
                continue;
            foreach (var (caller, site) in sites)
            {
                var siteFlow = FlowOf(caller);
                if (!siteFlow.IsReachable(site))
                    continue;
                var siteArgs = siteFlow.Arguments(site);
                if (!siteArgs[0].Any(x => siteFlow.Code[x].Operand is IFieldDescriptor f && fieldKeys.Contains(GameAssemblies.FieldKey(f))))
                    continue;
                result.AddRange(Resolve(caller, siteArgs[^1], null, want, depth).Select(x => x.Prefix(step)));
            }
        }

        if (result.Count == 0)
            result.Add(Origin.Unknown("built from a runtime value", step));
        return result;
    }

    private List<Origin> Concat(MethodDefinition method, IReadOnlyList<IReadOnlyList<int>> args, TypeDefinition? context, int depth)
    {
        var parts = args.Select(x => Resolve(method, x, context, Want.String, depth)).ToList();
        if (parts.Any(x => x.Count == 0 || x.Any(o => !o.Resolved)) || parts.Aggregate(1L, (n, x) => n * x.Count) > MaxOrigins)
            return [Origin.Unknown("built from a runtime value", $"string.Concat in {Display(method)}")];

        IEnumerable<Origin> combined = [Origin.Literal("", "string.Concat")];
        foreach (var part in parts)
            combined = from left in combined from right in part where Compatible(left, right) select left with { Value = left.Value + right.Value, Choices = left.Choices.SetItems(right.Choices) };
        return combined.Select(x => x with { Steps = [$"string.Concat -> {Quote(x.Value!)}"] }).ToList();
    }

    private List<Origin> Format(MethodDefinition method, IReadOnlyList<IReadOnlyList<int>> args, TypeDefinition? context, int depth)
    {
        var parts = args.Select(x => Resolve(method, x, context, Want.String, depth)).ToList();
        if (parts.Any(x => x.Count != 1 || !x[0].Resolved))
            return [Origin.Unknown("built from a runtime value", $"string.Format in {Display(method)}")];

        var value = string.Format(CultureInfo.InvariantCulture, parts[0][0].Value!, parts.Skip(1).Select(x => (object?) x[0].Value).ToArray());
        return [Origin.Literal(value, $"string.Format -> {Quote(value)}")];
    }

    private List<Origin> ResolveTypeToken(MethodDefinition method, object? operand, TypeDefinition? context, int depth) => operand switch
    {
        TypeSpecification { Signature: { } signature } => ResolveTypeSignature(method, signature, context, depth, "typeof"),
        ITypeDefOrRef type => [Origin.OfType(TypeNames.Format(type), _build.Resolve(type), $"typeof({TypeNames.Format(type)})")],
        _ => [],
    };

    /// <summary>A type written in the code: a concrete one as it is, a generic parameter from what each caller or subclass supplies.</summary>
    private List<Origin> ResolveTypeSignature(MethodDefinition method, TypeSignature signature, TypeDefinition? context, int depth, string how)
    {
        if (signature is not GenericParameterSignature parameter)
            return [Origin.OfType(TypeNames.Format(signature, method.DeclaringType, method), _build.Resolve(signature), $"{how}({TypeNames.Format(signature, method.DeclaringType, method)})")];

        if (parameter.ParameterType == GenericParameterType.Type)
        {
            // Supplied by the context's chain, where a subclass derives from the generic base with an argument.
            if (context is not null)
            {
                foreach (var type in _build.SelfAndBases(context))
                {
                    if (type.BaseType is TypeSpecification { Signature: GenericInstanceTypeSignature generic } && _build.Resolve(generic.GenericType) == method.DeclaringType && parameter.Index < generic.TypeArguments.Count)
                        return ResolveTypeSignature(method, generic.TypeArguments[parameter.Index], null, depth, how);
                }
            }
            return [Origin.Unknown("generic argument not known", $"{how}(!{parameter.Index}) in {Display(method)}")];
        }

        var step = $"type argument {(parameter.Index < method.GenericParameters.Count ? method.GenericParameters[parameter.Index].Name : $"!!{parameter.Index}")} of {Display(method)}";
        var parameterChoice = $"P:{GameAssemblies.MethodKey(method)}";
        var result = new List<Origin>();
        foreach (var (caller, index) in SitesCalling(method))
        {
            if (FlowOf(caller).Code[index].Operand is not MethodSpecification { Signature: { } instance } || parameter.Index >= instance.TypeArguments.Count)
                continue;
            if (depth > MaxDepth)
            {
                _depthCapHits++;
                break;
            }
            foreach (var origin in ResolveTypeSignature(caller, instance.TypeArguments[parameter.Index], null, depth + 1, how))
                result.Add(origin.Prefix($"{Display(caller)}: call").Prefix(step).Choose(parameterChoice, SiteId(caller, index)));
        }
        if (result.Count == 0)
            result.Add(Origin.Unknown("called from outside the build or through a delegate", step));
        return result;
    }

    /// <summary>The call sites that can reach a method: calls of it, and virtual calls of the members it overrides.</summary>
    private IEnumerable<(MethodDefinition Caller, int Index)> SitesCalling(MethodDefinition method)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { GameAssemblies.MethodKey(method) };
        if (method.IsVirtual && !method.IsNewSlot)
            foreach (var overridden in BaseMethods(method))
                keys.Add(GameAssemblies.MethodKey(overridden));
        return keys.SelectMany(x => _callSites.GetValueOrDefault(x) ?? []);
    }

    /// <summary>The virtual members of base types that a method overrides.</summary>
    private IEnumerable<MethodDefinition> BaseMethods(MethodDefinition method) =>
        _build.SelfAndBases(method.DeclaringType!).Skip(1).SelectMany(x => x.Methods).Where(x => x.IsVirtual && SameSlot(x, method));

    /// <summary>The implementation of a virtual method that runs on an object of the given class.</summary>
    private MethodDefinition MostDerived(TypeDefinition type, MethodDefinition method) =>
        _build.SelfAndBases(type).SelectMany(x => x.Methods.Where(m => m == method || m.IsVirtual && SameSlot(m, method))).FirstOrDefault() ?? method;

    /// <summary>
    /// The implementations a virtual or interface call can run: the one each concrete class of the build
    /// runs. A base implementation that every concrete class overrides never runs, so it is not among them.
    /// </summary>
    private IEnumerable<MethodDefinition> Implementations(MethodDefinition method) =>
        _build.Assignable(method.DeclaringType!)
            .Where(x => !x.IsAbstract && !x.IsInterface)
            .Select(x => method.DeclaringType!.IsInterface ? InterfaceImplementation(x, method) : MostDerived(x, method))
            .OfType<MethodDefinition>()
            .Distinct();

    /// <summary>The method a class runs for an interface method: by name, or by the name an explicit implementation gets.</summary>
    private MethodDefinition? InterfaceImplementation(TypeDefinition type, MethodDefinition method) =>
        _build.SelfAndBases(type)
            .SelectMany(x => x.Methods)
            .FirstOrDefault(x => !x.IsStatic && (x.Name == method.Name || x.Name?.ToString().EndsWith($".{method.Name}", StringComparison.Ordinal) == true)
                                 && x.Signature?.ParameterTypes.Count == method.Signature?.ParameterTypes.Count);

    /// <summary>
    /// Whether two methods fill the same virtual slot: the same name and parameters. Across a generic base
    /// the parameters are written differently on each side, so there the count has to do.
    /// </summary>
    private static bool SameSlot(MethodDefinition a, MethodDefinition b)
    {
        if (a.Name != b.Name || a.Signature is null || b.Signature is null || a.Signature.ParameterTypes.Count != b.Signature.ParameterTypes.Count)
            return false;
        if (a.DeclaringType!.GenericParameters.Count > 0 || b.DeclaringType!.GenericParameters.Count > 0)
            return true;
        return GameAssemblies.SignatureKey(a.Signature) == GameAssemblies.SignatureKey(b.Signature);
    }

    /// <summary>The declared type of a ViewModel argument, when the trace finds nothing more specific.</summary>
    private string DeclaredType(MethodDefinition method, IReadOnlyList<int> producers)
    {
        var flow = FlowOf(method);
        foreach (var producer in producers.Where(x => x >= 0))
        {
            var instruction = flow.Code[producer];
            TypeSignature? type = instruction switch
            {
                _ when instruction.IsLdloc() => instruction.GetLocalVariable(flow.Body.LocalVariables)?.VariableType,
                _ when instruction.IsLdarg() => instruction.GetParameter(method.Parameters)?.ParameterType,
                { Operand: IFieldDescriptor field } => field.Signature?.FieldType,
                { OpCode.Code: CilCode.Call or CilCode.Callvirt, Operand: IMethodDescriptor called } => called.Signature?.ReturnType,
                { OpCode.Code: CilCode.Castclass or CilCode.Isinst, Operand: ITypeDefOrRef cast } => cast.ToTypeSignature(),
                _ => null,
            };
            if (type is not null)
                return TypeNames.Format(type, method.DeclaringType, method);
        }
        return TypeSchemaReader.ViewModelType;
    }

    /// <summary>The type argument of an attribute such as [OverrideView(typeof(...))] on the class, by the attribute's name.</summary>
    private static string? TypeAttribute(TypeDefinition type, string name)
    {
        foreach (var attribute in type.CustomAttributes.Where(x => IsAttribute(x, name)))
        {
            if (attribute.Signature?.FixedArguments.FirstOrDefault()?.Element is TypeSignature argument)
                return TypeNames.Format(argument, type);
        }
        return null;
    }

    /// <summary>Whether an attribute is the one of the name, written with or without its Attribute suffix.</summary>
    private static bool IsAttribute(CustomAttribute attribute, string name) =>
        attribute.Constructor?.DeclaringType?.Name?.ToString() is { } attributeName && (attributeName == name || attributeName == $"{name}Attribute");

    private static string ExternalReason(string declaring) => declaring switch
    {
        _ when declaring.StartsWith("System.IO.", StringComparison.Ordinal) || declaring.StartsWith("System.Xml", StringComparison.Ordinal) => "read from a file or config",
        _ when declaring.StartsWith("System.Reflection.", StringComparison.Ordinal) => "produced through reflection",
        _ => "built from a runtime value",
    };

    private static string Display(MethodDefinition method) => $"{TypeNames.Format(method.DeclaringType!).Split('.')[^1]}.{method.Name}";

    private static string SiteId(MethodDefinition method, int index) => $"{GameAssemblies.MethodKey(method)}@{index}";

    /// <summary>A calling method as the schemas name it: Type::Method.</summary>
    private static string CallerName(MethodDefinition method) => $"{TypeNames.Format(method.DeclaringType!)}::{method.Name}";

    private static string LocalName(CilLocalVariable? local) => local is null ? "?" : $"V_{local.Index}";

    private static string Quote(string value) => $"\"{value}\"";

    private Flow FlowOf(MethodDefinition method)
    {
        if (!_flows.TryGetValue(method, out var flow))
            _flows[method] = flow = new Flow(method);
        return flow;
    }

    /// <summary>
    /// Which instructions can have pushed each value on the evaluation stack, before each instruction of a
    /// method. A conditional or a branch merges the stacks of both paths, so a value can have several
    /// producers; -1 stands for the exception a handler starts with.
    /// </summary>
    private sealed class Flow
    {
        private readonly int[][]?[] _before;
        private readonly List<int>[] _predecessors;

        public CilMethodBody Body { get; }
        public IList<CilInstruction> Code { get; }
        private MethodDefinition Method { get; }

        public Flow(MethodDefinition method)
        {
            Method = method;
            Body = method.CilMethodBody!;
            Code = Body.Instructions;
            _before = new int[][]?[Code.Count];

            _predecessors = new List<int>[Code.Count];
            for (var i = 0; i < Code.Count; i++)
                _predecessors[i] = [];

            var indexByOffset = new Dictionary<int, int>();
            for (var i = 0; i < Code.Count; i++)
                indexByOffset[Code[i].Offset] = i;

            var work = new Stack<int>();
            Merge(0, []);
            foreach (var handler in Body.ExceptionHandlers)
            {
                int[][] withException = handler.HandlerType is CilExceptionHandlerType.Exception or CilExceptionHandlerType.Filter ? [[-1]] : [];
                if (handler.HandlerStart is { } start && indexByOffset.TryGetValue(start.Offset, out var handlerIndex))
                    Merge(handlerIndex, withException);
                if (handler.FilterStart is { } filter && indexByOffset.TryGetValue(filter.Offset, out var filterIndex))
                    Merge(filterIndex, [[-1]]);
            }

            while (work.TryPop(out var i))
            {
                var stack = _before[i]!;
                var instruction = Code[i];
                int[][] after;
                if (instruction.OpCode.Code == CilCode.Dup && stack.Length > 0)
                {
                    after = [.. stack, stack[^1]];
                }
                else
                {
                    // A negative count means the instruction empties the stack, as leave and throw do.
                    var popCount = instruction.GetStackPopCount(Body);
                    var pop = popCount < 0 ? stack.Length : Math.Min(stack.Length, popCount);
                    var push = instruction.GetStackPushCount();
                    after = push > 0 ? [.. stack[..^pop], [i]] : stack[..^pop];
                }

                var flowControl = instruction.OpCode.FlowControl;
                var leaves = instruction.OpCode.Code is CilCode.Leave or CilCode.Leave_S;
                foreach (var target in Targets(instruction))
                {
                    if (!indexByOffset.TryGetValue(target, out var targetIndex))
                        continue;
                    AddPredecessor(targetIndex, i);
                    Merge(targetIndex, leaves ? [] : after);
                }
                if (flowControl is not (CilFlowControl.Branch or CilFlowControl.Return or CilFlowControl.Throw) && i + 1 < Code.Count)
                {
                    AddPredecessor(i + 1, i);
                    Merge(i + 1, after);
                }
            }

            void AddPredecessor(int index, int predecessor)
            {
                if (!_predecessors[index].Contains(predecessor))
                    _predecessors[index].Add(predecessor);
            }

            void Merge(int index, int[][] stack)
            {
                var existing = _before[index];
                if (existing is null)
                {
                    _before[index] = stack;
                    work.Push(index);
                    return;
                }
                if (existing.Length != stack.Length)
                    return;

                var changed = false;
                var merged = new int[existing.Length][];
                for (var slot = 0; slot < existing.Length; slot++)
                {
                    merged[slot] = existing[slot];
                    if (stack[slot].All(existing[slot].Contains))
                        continue;
                    merged[slot] = existing[slot].Union(stack[slot]).Order().ToArray();
                    changed = true;
                }
                if (!changed)
                    return;
                _before[index] = merged;
                work.Push(index);
            }
        }

        private static IEnumerable<int> Targets(CilInstruction instruction) => instruction.Operand switch
        {
            ICilLabel label => [label.Offset],
            IEnumerable<ICilLabel> labels => labels.Select(x => x.Offset),
            _ => [],
        };

        public bool IsReachable(int index) => _before[index] is not null;

        /// <summary>
        /// Visits the instructions on the paths leading to the given one, nearest first, and stops a path
        /// where the visitor says so. Returns whether some path reaches the method's start, or a handler's.
        /// </summary>
        public bool WalkBack(int index, Func<int, bool> stop)
        {
            var reachesStart = _predecessors[index].Count == 0;
            var seen = new HashSet<int>();
            var work = new Stack<int>(_predecessors[index]);
            while (work.TryPop(out var i))
            {
                if (!seen.Add(i) || stop(i))
                    continue;
                if (_predecessors[i].Count == 0)
                    reachesStart = true;
                foreach (var predecessor in _predecessors[i])
                    work.Push(predecessor);
            }
            return reachesStart;
        }

        /// <summary>The stack before an instruction, bottom first.</summary>
        public IReadOnlyList<IReadOnlyList<int>> Stack(int index) => _before[index] ?? [];

        /// <summary>The producers of each value a call pops, `this` first.</summary>
        public IReadOnlyList<IReadOnlyList<int>> Arguments(int index)
        {
            var stack = Stack(index);
            var count = Math.Clamp(Code[index].GetStackPopCount(Body), 0, stack.Count);
            return stack.Skip(stack.Count - count).ToList();
        }

        /// <summary>Whether every producer of a value is ldarg.0 in an instance method, so the value is `this`.</summary>
        public bool IsThis(IReadOnlyList<int> producers) =>
            !Method.IsStatic
            && producers.Count > 0
            && producers.All(x => x >= 0 && Code[x].IsLdarg() && Code[x].GetParameter(Method.Parameters) == Method.Parameters.ThisParameter);
    }
}
