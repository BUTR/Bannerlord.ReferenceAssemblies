using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.DotNet.Signatures.Types;

using System.Text.Json.Serialization;

namespace Bannerlord.ReferenceAssemblies;

internal sealed record TypeSchema(int FormatVersion, List<WidgetType> Widgets, List<ViewModelType> ViewModels, List<ObjectType> Objects, List<EnumType> Enums);

internal sealed record WidgetType(
    string Name,
    string Type,
    string? BaseType,
    string? Module,
    string Assembly,
    bool Abstract,
    List<string> Events,
    List<UnresolvedEvent> UnresolvedEvents,
    List<WidgetProperty> Properties);

/// <summary>A public property the loader can set. AssignedTypes is left out when only the declared type can be there.</summary>
internal sealed record WidgetProperty(
    string Name,
    string Type,
    bool CanRead,
    bool CanWrite,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<string>? AssignedTypes = null);

/// <summary>A class a dotted attribute reaches through a widget property, such as Brush in Brush.FontSize.</summary>
internal sealed record ObjectType(string Type, string? BaseType, string? Module, string Assembly, bool Abstract, List<WidgetProperty> Properties);

internal sealed record ViewModelType(string Type, string? BaseType, string? Module, string Assembly, bool Abstract, List<ViewModelProperty> Properties, List<ViewModelMethod> Methods);

internal sealed record ViewModelProperty(string Name, string Type, string Accessibility, bool CanRead, bool CanWrite);

internal sealed record ViewModelMethod(string Name, string Accessibility, string ReturnType, List<MethodParameter> Parameters);

internal sealed record MethodParameter(string Name, string Type);

internal sealed record EnumType(string Type, List<string> Members);

/// <summary>
/// Writes types.json: the widget classes and ViewModels of the build, and the objects widget properties
/// hold, as data. A mod compiles only against the modules it references and an analyzer cannot add
/// references, so the analyzer learns the rest of the build's types from this. It is read from the real
/// assemblies, never from the stripped reference ones.
/// </summary>
internal static class TypeSchemaReader
{
    public const string WidgetType = "TaleWorlds.GauntletUI.BaseTypes.Widget";
    public const string ViewModelType = "TaleWorlds.Library.ViewModel";

    /// <summary>How many property steps from a widget the objects go: Brush, then what Brush holds, then what that holds.</summary>
    private const int ObjectDepth = 3;

    /// <summary>
    /// The schema of the types declared in the assemblies the filter takes. The objects are the same for
    /// every filter, so a caller reading several packages passes them in, computed once.
    /// </summary>
    public static TypeSchema Read(GameAssemblies build, BuildScanner scanner, Func<GameAssembly, bool> include, IReadOnlyDictionary<string, ObjectNode>? objects = null)
    {
        var widgets = new List<WidgetType>();
        var viewModels = new List<ViewModelType>();
        var enums = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);

        foreach (var type in build.Types)
        {
            if (build.OwnerOf(type) is not { } owner || !include(owner) || !type.IsClass || type.IsInterface)
                continue;

            if (build.DerivesFrom(type, WidgetType))
            {
                var properties = new List<WidgetProperty>();
                // The loader sets an attribute only through a public instance property.
                foreach (var property in PublicProperties(type))
                {
                    var canRead = property.GetMethod is { IsPublic: true };
                    var canWrite = property.SetMethod is { IsPublic: true };
                    properties.Add(new WidgetProperty(property.Name!, TypeNames.Format(property.Signature!.ReturnType, type), canRead, canWrite, scanner.AssignedTypes(type, property)));
                    if (EnumOf(build, property.Signature.ReturnType) is { } enumType)
                        enums.TryAdd(enumType.FullName, enumType);
                }

                var (events, unresolvedEvents) = scanner.EventsOf(type);
                widgets.Add(new WidgetType(type.Name!, TypeNames.Definition(type), BaseType(type), owner.Module, owner.FileName, type.IsAbstract,
                    [.. events], [.. unresolvedEvents], properties.OrderBy(x => x.Name, StringComparer.Ordinal).ToList()));
            }
            else if (build.DerivesFrom(type, ViewModelType))
            {
                // The binding table takes properties and methods of every accessibility.
                var properties = type.Properties
                    .Where(x => !IsIndexer(x) && !IsStatic(x) && x.Signature is not null)
                    .Select(x => new ViewModelProperty(x.Name!, TypeNames.Format(x.Signature!.ReturnType, type), Accessibility(x), x.GetMethod is not null, x.SetMethod is not null))
                    .OrderBy(x => x.Name, StringComparer.Ordinal)
                    .ToList();

                var methods = type.Methods
                    .Where(x => !x.IsStatic && !x.IsConstructor && !x.IsSpecialName && x.Name?.ToString() is { Length: > 0 } name && name[0] != '<' && x.Signature is not null)
                    .Select(x => new ViewModelMethod(
                        x.Name!,
                        Accessibility(x),
                        TypeNames.Format(x.Signature!.ReturnType, type, x),
                        x.Parameters.Select(p => new MethodParameter(p.Name ?? $"arg{p.Index}", TypeNames.Format(p.ParameterType, type, x))).ToList()))
                    .OrderBy(x => x.Name, StringComparer.Ordinal)
                    .ThenBy(x => string.Join(",", x.Parameters.Select(p => p.Type)), StringComparer.Ordinal)
                    .ToList();

                viewModels.Add(new ViewModelType(TypeNames.Definition(type), BaseType(type), owner.Module, owner.FileName, type.IsAbstract, properties, methods));
            }
        }

        var objectTypes = new List<ObjectType>();
        foreach (var node in (objects ?? Objects(build, scanner)).Values)
        {
            var type = node.Definition;
            if (build.OwnerOf(type) is not { } owner || !include(owner))
                continue;
            var properties = new List<WidgetProperty>();
            foreach (var property in PublicProperties(type))
            {
                var propertyType = node.Instantiate(property.Signature!.ReturnType);
                properties.Add(new WidgetProperty(property.Name!, TypeNames.Format(propertyType, type), property.GetMethod is { IsPublic: true }, property.SetMethod is { IsPublic: true }));
                if (EnumOf(build, propertyType) is { } enumType)
                    enums.TryAdd(enumType.FullName, enumType);
            }
            var baseType = node.BaseSignature is { } baseSignature ? TypeNames.Format(baseSignature, type) : null;
            objectTypes.Add(new ObjectType(node.Name, baseType, owner.Module, owner.FileName, type.IsAbstract, properties.OrderBy(x => x.Name, StringComparer.Ordinal).ToList()));
        }

        return new TypeSchema(
            GuiPackager.FormatVersion,
            widgets.OrderBy(x => x.Type, StringComparer.Ordinal).ToList(),
            viewModels.OrderBy(x => x.Type, StringComparer.Ordinal).ToList(),
            objectTypes.OrderBy(x => x.Type, StringComparer.Ordinal).ToList(),
            enums.Values
                .Select(x => new EnumType(TypeNames.Definition(x), x.Fields.Where(f => f.IsLiteral && f.IsStatic).Select(f => f.Name!.ToString()).Order(StringComparer.Ordinal).ToList()))
                .OrderBy(x => x.Type, StringComparer.Ordinal)
                .ToList());
    }

    /// <summary>
    /// A class a dotted attribute can reach, as the type the path holds there: a generic class only as a
    /// closed instance, such as MBReadOnlyList&lt;Widget&gt;, whose members then have its arguments filled in.
    /// Depth is the number of property steps from a widget.
    /// </summary>
    internal sealed record ObjectNode(TypeDefinition Definition, GenericInstanceTypeSignature? Instance, int Depth)
    {
        public string Name => Instance is null ? TypeNames.Definition(Definition) : TypeNames.Format(Instance, null);

        public TypeSignature Instantiate(TypeSignature signature) =>
            Instance is null ? signature : signature.InstantiateGenericTypes(new GenericContext(Instance, null));

        public TypeSignature? BaseSignature => Definition.BaseType switch
        {
            null => null,
            TypeSpecification { Signature: { } signature } => Instantiate(signature),
            var plain => plain.ToTypeSignature(false),
        };
    }

    /// <summary>
    /// The classes of the build that dotted attributes can reach, by name. Starting from the declared type of
    /// every widget property with a getter, with the concrete types the widgets assign to it, and stepping on
    /// through those classes' own properties with a getter, to <see cref="ObjectDepth"/> steps: a path reads
    /// each step before it sets the last. The game itself resolves only one dot; UIExtenderEx fixes that, and
    /// resolves any number. Base classes come along, so that inherited members can be looked up. Taken over
    /// every widget of the build, so that each package lists the ones its own assemblies define, whichever
    /// package's widget reaches them.
    /// </summary>
    internal static Dictionary<string, ObjectNode> Objects(GameAssemblies build, BuildScanner scanner)
    {
        var found = new Dictionary<string, ObjectNode>(StringComparer.Ordinal);
        var level = new List<ObjectNode>();
        // The assigned types are named as TypeNames writes them, which FindType does not take for a generic.
        Dictionary<string, TypeDefinition>? byDefinition = null;
        foreach (var widget in build.Types.Where(x => x.IsClass && build.DerivesFrom(x, WidgetType)))
        foreach (var property in ReadableProperties(widget))
        {
            if (ObjectOf(build, property.Signature!.ReturnType, 1) is { } declared)
                level.Add(declared);
            foreach (var assigned in scanner.AssignedTypes(widget, property) ?? [])
                if ((build.FindType(assigned) ?? ByDefinition().GetValueOrDefault(assigned)) is { } type && ObjectOf(build, type.ToTypeSignature(false), 1) is { } node)
                    level.Add(node);
        }

        while (level.Count > 0)
        {
            var next = new List<ObjectNode>();
            foreach (var node in level)
            {
                if (!found.TryAdd(node.Name, node) || node.Depth >= ObjectDepth)
                    continue;
                foreach (var property in ReadableProperties(node.Definition))
                    if (ObjectOf(build, node.Instantiate(property.Signature!.ReturnType), node.Depth + 1) is { } held && !found.ContainsKey(held.Name))
                        next.Add(held);
            }
            level = next;
        }

        foreach (var node in found.Values.ToList())
        {
            var current = node;
            while (current.BaseSignature is { } baseSignature && ObjectOf(build, baseSignature, node.Depth) is { } baseNode && found.TryAdd(baseNode.Name, baseNode))
                current = baseNode;
        }
        return found;

        // The first type of a name wins, as a scan of the build in order would find it.
        Dictionary<string, TypeDefinition> ByDefinition()
        {
            if (byDefinition is null)
            {
                byDefinition = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
                foreach (var type in build.Types)
                    byDefinition.TryAdd(TypeNames.Definition(type), type);
            }
            return byDefinition;
        }
    }

    /// <summary>The object a type signature stands for, or null: not a class of the build, or an open generic.</summary>
    private static ObjectNode? ObjectOf(GameAssemblies build, TypeSignature signature, int depth)
    {
        if (ObjectClass(build, build.Resolve(signature)) is not { } type)
            return null;
        if (type.GenericParameters.Count == 0)
            return new ObjectNode(type, null, depth);
        return signature is GenericInstanceTypeSignature generic && IsClosed(generic) ? new ObjectNode(type, generic, depth) : null;

        static bool IsClosed(TypeSignature signature) => signature switch
        {
            GenericParameterSignature => false,
            GenericInstanceTypeSignature generic => generic.TypeArguments.All(IsClosed),
            TypeSpecificationSignature wrapped => IsClosed(wrapped.BaseType),
            _ => true,
        };
    }

    /// <summary>A class of the build a dotted attribute can step into: not a widget, a ViewModel, an enum, a struct or a delegate.</summary>
    private static TypeDefinition? ObjectClass(GameAssemblies build, TypeDefinition? type) =>
        type is { IsClass: true, IsInterface: false, IsEnum: false, IsValueType: false, IsDelegate: false }
        && build.OwnerOf(type) is not null
        && !build.DerivesFrom(type, WidgetType)
        && !build.DerivesFrom(type, ViewModelType)
        && type.Name?.ToString().StartsWith('<') != true
            ? type
            : null;

    /// <summary>The public instance properties, not indexers, a type declares, that a path can read and so step through.</summary>
    private static IEnumerable<PropertyDefinition> ReadableProperties(TypeDefinition type) =>
        PublicProperties(type).Where(x => x.GetMethod is { IsPublic: true });

    /// <summary>The public instance properties, not indexers, a type declares.</summary>
    private static IEnumerable<PropertyDefinition> PublicProperties(TypeDefinition type) =>
        type.Properties.Where(x => !IsIndexer(x) && !IsStatic(x) && x.Signature is not null && (x.GetMethod is { IsPublic: true } || x.SetMethod is { IsPublic: true }));

    private static string? BaseType(TypeDefinition type) => type.BaseType is { } baseType ? TypeNames.Format(baseType, type) : null;

    private static bool IsIndexer(PropertyDefinition property) => property.Signature?.ParameterTypes.Count > 0;

    private static bool IsStatic(PropertyDefinition property) => (property.GetMethod ?? property.SetMethod)?.IsStatic == true;

    /// <summary>The enum a property holds, directly or as a Nullable, when the build defines it.</summary>
    private static TypeDefinition? EnumOf(GameAssemblies build, TypeSignature signature)
    {
        if (signature is GenericInstanceTypeSignature { TypeArguments.Count: 1 } generic && generic.GenericType.FullName == "System.Nullable`1")
            signature = generic.TypeArguments[0];
        return build.Resolve(signature) is { IsEnum: true } enumType ? enumType : null;
    }

    /// <summary>The most accessible of a property's accessors.</summary>
    private static string Accessibility(PropertyDefinition property) =>
        new[] { property.GetMethod, property.SetMethod }.OfType<MethodDefinition>().Select(Access).MaxBy(x => x.Rank).Name;

    public static string Accessibility(MethodDefinition method) => Access(method).Name;

    /// <summary>An accessibility, with its rank: the higher, the more accessible.</summary>
    private static (int Rank, string Name) Access(MethodDefinition method) => method switch
    {
        { IsPublic: true } => (5, "public"),
        { IsFamilyOrAssembly: true } => (4, "protected internal"),
        { IsFamily: true } => (3, "protected"),
        { IsAssembly: true } => (2, "internal"),
        { IsFamilyAndAssembly: true } => (1, "private protected"),
        _ => (0, "private"),
    };
}
