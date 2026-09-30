using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.DotNet.Signatures.Types;
using AsmResolver.PE.DotNet.Cil;

namespace Bannerlord.ReferenceAssemblies;

/// <summary>An event name a widget raises that cannot be traced to a literal.</summary>
internal sealed record UnresolvedEvent(string Caller, string Via, string Reason);

internal sealed partial class BuildScanner
{
    private string EventFiredKey => $"{_build.WidgetType}::EventFired(";

    private Dictionary<TypeDefinition, (SortedSet<string> Events, List<UnresolvedEvent> Unresolved)>? _events;
    private readonly Dictionary<(TypeDefinition, PropertyDefinition), List<string>?> _assignedTypes = [];

    /// <summary>
    /// The events a widget type raises in its own methods, through Widget.EventFired(name, args): exactly
    /// the Command.&lt;name&gt; keys it can bind. Inherited ones are left to its base types. A subclass that
    /// changes the name a base method raises, through an override, raises that name as its own.
    /// </summary>
    public (IReadOnlyCollection<string> Events, IReadOnlyList<UnresolvedEvent> Unresolved) EventsOf(TypeDefinition widget)
    {
        _events ??= ScanEvents();
        return _events.TryGetValue(widget, out var found) ? (found.Events, found.Unresolved) : ([], []);
    }

    private Dictionary<TypeDefinition, (SortedSet<string> Events, List<UnresolvedEvent> Unresolved)> ScanEvents()
    {
        var result = new Dictionary<TypeDefinition, (SortedSet<string> Events, List<UnresolvedEvent> Unresolved)>();
        (SortedSet<string> Events, List<UnresolvedEvent> Unresolved) Entry(TypeDefinition type) =>
            result.GetOrAdd(type, () => (new SortedSet<string>(StringComparer.Ordinal), []));

        foreach (var (_, sites) in _callSites.Where(x => x.Key.StartsWith(EventFiredKey, StringComparison.Ordinal)).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            foreach (var (caller, index) in sites)
            {
                var flow = FlowOf(caller);
                if (!flow.IsReachable(index) || flow.Arguments(index) is not { Count: >= 2 } args)
                    continue;

                // EventFired is protected, so a widget raises it on itself, or on another widget of its own
                // hierarchy; the event belongs to the widget it is raised on.
                var host = HostType(caller.DeclaringType!);
                var raisedOn = flow.IsThis(args[0]) || host != caller.DeclaringType ? host : _build.Resolve(DeclaredTypeSignature(caller, args[0])) ?? host;
                var callerName = CallerName(caller);
                var onThis = raisedOn == host && host == caller.DeclaringType && !caller.IsStatic;

                var own = Resolve(caller, args[1], onThis ? host : null, Want.String, 0);
                var ownNames = own.Where(x => x.Resolved && x.Value is { Length: > 0 }).Select(x => x.Value!).ToHashSet(StringComparer.Ordinal);
                var entry = Entry(raisedOn);
                entry.Events.UnionWith(ownNames);
                entry.Unresolved.AddRange(own.Where(x => !x.Resolved).Select(x => new UnresolvedEvent(callerName, Via(x), x.Reason!)));

                if (!onThis)
                    continue;
                foreach (var derived in _build.SelfAndDerived(host).Skip(1))
                {
                    var extra = Resolve(caller, args[1], derived, Want.String, 0)
                        .Where(x => x.Resolved && x.Value is { Length: > 0 } && !ownNames.Contains(x.Value))
                        .Select(x => x.Value!)
                        .ToList();
                    if (extra.Count > 0)
                        Entry(derived).Events.UnionWith(extra);
                }
            }
        }

        foreach (var (_, entry) in result)
        {
            var distinct = entry.Unresolved.DistinctBy(x => (x.Caller, x.Via, x.Reason)).OrderBy(x => x.Caller, StringComparer.Ordinal).ThenBy(x => x.Via, StringComparer.Ordinal).ToList();
            entry.Unresolved.Clear();
            entry.Unresolved.AddRange(distinct);
        }
        return result;
    }

    /// <summary>
    /// What a widget type announces in its own methods, through one of PropertyOwnerObject's
    /// OnPropertyChanged(value, name) overloads, by name: the types the loader hands to the ViewModel property
    /// bound to that name. Found as <see cref="EventsOf"/> finds events: inherited ones are left to the base
    /// types, and a subclass whose override changes what a base method announces announces that as its own.
    /// </summary>
    public (IReadOnlyDictionary<string, SortedSet<string>> Announcements, IReadOnlyList<UnresolvedEvent> Unresolved) AnnouncementsOf(TypeDefinition widget)
    {
        _announcements ??= ScanAnnouncements();
        return _announcements.TryGetValue(widget, out var found) ? (found.Announcements, found.Unresolved) : (EmptyAnnouncements, []);
    }

    private static readonly IReadOnlyDictionary<string, SortedSet<string>> EmptyAnnouncements = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

    private Dictionary<TypeDefinition, (SortedDictionary<string, SortedSet<string>> Announcements, List<UnresolvedEvent> Unresolved)>? _announcements;

    private Dictionary<TypeDefinition, (SortedDictionary<string, SortedSet<string>> Announcements, List<UnresolvedEvent> Unresolved)> ScanAnnouncements()
    {
        var result = new Dictionary<TypeDefinition, (SortedDictionary<string, SortedSet<string>> Announcements, List<UnresolvedEvent> Unresolved)>();
        (SortedDictionary<string, SortedSet<string>> Announcements, List<UnresolvedEvent> Unresolved) Entry(TypeDefinition type) =>
            result.GetOrAdd(type, () => (new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal), []));

        foreach (var (_, sites) in _callSites.Where(x => x.Key.StartsWith(OnPropertyChangedKey, StringComparison.Ordinal)).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            foreach (var (caller, index) in sites)
            {
                var flow = FlowOf(caller);
                if (!flow.IsReachable(index) || flow.Arguments(index) is not { Count: >= 3 } args || flow.Code[index].Operand is not IMethodDescriptor { Signature.ParameterTypes.Count: 2 } target)
                    continue;

                // As for events: OnPropertyChanged is protected, so the widget it is raised on is this, or
                // another widget of the caller's own hierarchy.
                var host = HostType(caller.DeclaringType!);
                var raisedOn = flow.IsThis(args[0]) || host != caller.DeclaringType ? host : _build.Resolve(DeclaredTypeSignature(caller, args[0])) ?? host;
                var callerName = CallerName(caller);
                var onThis = raisedOn == host && host == caller.DeclaringType && !caller.IsStatic;

                var own = Announced(caller, args, target, onThis ? host : null);
                var entry = Entry(raisedOn);
                foreach (var (name, type) in own.Pairs)
                    Add(entry.Announcements, name, type);
                entry.Unresolved.AddRange(own.Unresolved.Select(x => new UnresolvedEvent(callerName, Via(x), x.Reason!)));

                if (!onThis)
                    continue;
                foreach (var derived in _build.SelfAndDerived(host).Skip(1))
                {
                    var extra = Announced(caller, args, target, derived).Pairs.Where(x => !own.Pairs.Contains(x)).ToList();
                    foreach (var (name, type) in extra)
                        Add(Entry(derived).Announcements, name, type);
                }
            }
        }

        foreach (var (_, entry) in result)
        {
            var distinct = entry.Unresolved.DistinctBy(x => (x.Caller, x.Via, x.Reason)).OrderBy(x => x.Caller, StringComparer.Ordinal).ThenBy(x => x.Via, StringComparer.Ordinal).ToList();
            entry.Unresolved.Clear();
            entry.Unresolved.AddRange(distinct);
        }
        return result;

        static void Add(SortedDictionary<string, SortedSet<string>> announcements, string name, string type)
        {
            if (!announcements.TryGetValue(name, out var types))
                announcements[name] = types = new SortedSet<string>(StringComparer.Ordinal);
            types.Add(type);
        }
    }

    private const string PropertyOwnerObjectType = "TaleWorlds.GauntletUI.PropertyOwnerObject";
    private const string OnPropertyChangedKey = $"{PropertyOwnerObjectType}::OnPropertyChanged(";

    /// <summary>
    /// The (name, type) pairs one OnPropertyChanged call announces, for the given `this` class when known,
    /// and the names or types that cannot be traced. The name is traced as an event's is; [CallerMemberName]
    /// compiles to a literal. The type is the typed overload's parameter, the generic overload's argument, or,
    /// for the object overload of the early builds, the value's own type before it was boxed.
    /// </summary>
    private (HashSet<(string Name, string Type)> Pairs, List<Origin> Unresolved) Announced(MethodDefinition caller, IReadOnlyList<IReadOnlyList<int>> args, IMethodDescriptor target, TypeDefinition? context)
    {
        var names = Resolve(caller, args[2], context, Want.String, 0);
        var unresolved = names.Where(x => !x.Resolved).ToList();
        var pairs = new HashSet<(string, string)>();

        var types = AnnouncedTypes(caller, args[1], target, context);
        unresolved.AddRange(types.Where(x => !x.Resolved));
        foreach (var name in names.Where(x => x.Resolved && x.Value is { Length: > 0 }))
        foreach (var type in types.Where(x => x.Resolved && x.Value is not null))
            pairs.Add((name.Value!, type.Value!));
        return (pairs, unresolved);
    }

    /// <summary>The types a value passed to an OnPropertyChanged overload is announced as. A literal null announces none.</summary>
    private List<Origin> AnnouncedTypes(MethodDefinition caller, IReadOnlyList<int> value, IMethodDescriptor target, TypeDefinition? context)
    {
        var flow = FlowOf(caller);
        var producers = value.Where(x => x < 0 || flow.Code[x].OpCode.Code != CilCode.Ldnull).ToList();
        if (producers.Count == 0)
            return [];

        var parameter = target.Signature!.ParameterTypes[0];
        if (parameter is GenericParameterSignature { ParameterType: GenericParameterType.Method, Index: 0 })
        {
            if (target is not MethodSpecification { Signature.TypeArguments: [var argument] })
                return [Origin.Unknown("generic argument not known", $"OnPropertyChanged in {Display(caller)}")];
            return ResolveTypeSignature(caller, argument, context, 0, "OnPropertyChanged<T>");
        }
        if (TypeNames.Format(parameter, null) != "System.Object")
            return [Origin.OfType(TypeNames.Format(parameter, null), null, $"OnPropertyChanged({TypeNames.Format(parameter, null)})")];

        // The object overload boxes whatever it is given, so the value's own type is what the loader passes on.
        var result = new List<Origin>();
        foreach (var producer in producers)
        {
            var instruction = producer < 0 ? null : flow.Code[producer];
            TypeSignature? type = instruction switch
            {
                null => null,
                { OpCode.Code: CilCode.Box, Operand: ITypeDefOrRef boxed } => boxed.ToTypeSignature(),
                { OpCode.Code: CilCode.Ldstr } => caller.Module!.CorLibTypeFactory.String,
                { OpCode.Code: CilCode.Newobj, Operand: IMethodDescriptor { DeclaringType: ITypeDefOrRef created } } => created.ToTypeSignature(),
                { OpCode.Code: CilCode.Castclass or CilCode.Isinst, Operand: ITypeDefOrRef cast } => cast.ToTypeSignature(),
                _ when instruction.IsLdloc() => instruction.GetLocalVariable(flow.Body.LocalVariables)?.VariableType,
                _ when instruction.IsLdarg() && instruction.GetParameter(caller.Parameters) is { } argument =>
                    argument == caller.Parameters.ThisParameter ? caller.DeclaringType!.ToTypeSignature() : argument.ParameterType,
                _ => DeclaredTypeSignature(caller, [producer]),
            };
            var name = type is null ? "System.Object" : TypeNames.Format(type, caller.DeclaringType, caller);
            result.Add(Origin.OfType(name, null, $"OnPropertyChanged(object) of {name}"));
        }
        return result;
    }

    /// <summary>
    /// The concrete types a widget's constructor chain stores into one of its properties, for a property
    /// whose declared type is an interface, an abstract class or a class with subclasses. Every concrete
    /// widget deriving from the declaring type counts, so a subclass that installs another layout adds its
    /// type. Null when only the declared type can be there, or the property has no field to trace.
    /// </summary>
    public List<string>? AssignedTypes(TypeDefinition widget, PropertyDefinition property)
    {
        if (_assignedTypes.TryGetValue((widget, property), out var known))
            return known;
        return _assignedTypes[(widget, property)] = FindAssignedTypes(widget, property);
    }

    private List<string>? FindAssignedTypes(TypeDefinition widget, PropertyDefinition property)
    {
        if (property.Signature?.ReturnType is not { } returnType || _build.Resolve(returnType) is not { } declared)
            return null;
        if (!declared.IsInterface && !declared.IsAbstract && !_build.Assignable(declared).Skip(1).Any())
            return null;
        // A widget held by a widget is never reached through a dotted attribute, only objects are.
        if (_build.DerivesFrom(declared, _build.WidgetType))
            return null;

        var field = (property.GetMethod is { } getter ? Accessors(getter).Getter : null) ?? (property.SetMethod is { } setter ? Accessors(setter).Setter : null);
        if (field is null)
            return null;
        var fieldKey = GameAssemblies.FieldKey(field);

        var types = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var concrete in _build.SelfAndDerived(widget).Where(x => !x.IsAbstract))
        foreach (var constructor in concrete.Methods.Where(x => x.IsConstructor && !x.IsStatic))
        foreach (var (method, store) in ConstructedStores(constructor, fieldKey).Stores)
        {
            var stack = FlowOf(method).Stack(store);
            if (stack.Count == 0)
                continue;
            foreach (var origin in Resolve(method, stack[^1], concrete, Want.Type, 0))
                if (origin.Resolved && origin.Value is not null)
                    types.Add(origin.Value);
        }

        return types.Count == 0 || types.Count == 1 && types.Min == TypeNames.Definition(declared) ? null : [.. types];
    }

    /// <summary>The declared type of a value, from the instruction that pushes it.</summary>
    private TypeSignature? DeclaredTypeSignature(MethodDefinition method, IReadOnlyList<int> producers)
    {
        var flow = FlowOf(method);
        foreach (var producer in producers.Where(x => x >= 0))
        {
            var instruction = flow.Code[producer];
            if (instruction.Operand is IFieldDescriptor field)
                return field.Signature?.FieldType;
            if (instruction.Operand is IMethodDescriptor called && called.Signature is { } signature)
                return signature.ReturnType;
        }
        return null;
    }
}
