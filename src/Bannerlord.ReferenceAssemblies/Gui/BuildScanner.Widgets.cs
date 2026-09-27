using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures.Types;

namespace Bannerlord.ReferenceAssemblies;

/// <summary>An event name a widget raises that cannot be traced to a literal.</summary>
internal sealed record UnresolvedEvent(string Caller, string Via, string Reason);

internal sealed partial class BuildScanner
{
    private const string EventFiredKey = $"{TypeSchemaReader.WidgetType}::EventFired(";

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
        if (_build.DerivesFrom(declared, TypeSchemaReader.WidgetType))
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
