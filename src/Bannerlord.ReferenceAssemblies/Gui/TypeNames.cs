using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures.Types;

namespace Bannerlord.ReferenceAssemblies;

/// <summary>
/// How the GUI packages write a type: namespace-qualified, nested types after a +, generic arguments in
/// angle brackets without the arity suffix, arrays ending in [], built-in types by their full names. A
/// generic type definition is written with its parameter names, e.g. TaleWorlds.Library.MBBindingList&lt;T&gt;.
/// </summary>
internal static class TypeNames
{
    /// <summary>
    /// A type signature. With a context, generic parameters are written by name; without one, by
    /// position (!0 for a type's, !!0 for a method's), which is what signature keys need.
    /// </summary>
    public static string Format(TypeSignature signature, IHasGenericParameters? typeContext, IHasGenericParameters? methodContext = null) => signature switch
    {
        CorLibTypeSignature corLib => $"{corLib.Namespace}.{corLib.Name}",
        GenericInstanceTypeSignature generic => $"{Format(generic.GenericType)}<{string.Join(",", generic.TypeArguments.Select(x => Format(x, typeContext, methodContext)))}>",
        SzArrayTypeSignature array => $"{Format(array.BaseType, typeContext, methodContext)}[]",
        ArrayTypeSignature array => $"{Format(array.BaseType, typeContext, methodContext)}[{new string(',', Math.Max(0, array.Dimensions.Count - 1))}]",
        ByReferenceTypeSignature byRef => $"{Format(byRef.BaseType, typeContext, methodContext)}&",
        PointerTypeSignature pointer => $"{Format(pointer.BaseType, typeContext, methodContext)}*",
        GenericParameterSignature parameter => GenericParameterName(parameter, typeContext, methodContext),
        TypeSpecificationSignature wrapped => Format(wrapped.BaseType, typeContext, methodContext),
        TypeDefOrRefSignature plain => Format(plain.Type),
        _ => signature.FullName,
    };

    /// <summary>A type definition or reference, without generic arguments.</summary>
    public static string Format(ITypeDefOrRef type)
    {
        if (type is TypeSpecification { Signature: { } signature })
            return Format(signature, null);
        var name = StripArity(type.Name?.ToString() ?? "");
        return type.DeclaringType is { } declaring
            ? $"{Format(declaring)}+{name}"
            : string.IsNullOrEmpty(type.Namespace) ? name : $"{type.Namespace}.{name}";
    }

    /// <summary>A type as the GUI packages list it: a generic definition with its parameter names.</summary>
    public static string Definition(TypeDefinition type) =>
        type.GenericParameters.Count == 0
            ? Format(type)
            : $"{Format(type)}<{string.Join(",", type.GenericParameters.Select(x => x.Name?.ToString()))}>";

    /// <summary>A type definition or reference with the arguments a base type specification carries.</summary>
    public static string Format(ITypeDefOrRef type, IHasGenericParameters? typeContext) =>
        type is TypeSpecification { Signature: { } signature } ? Format(signature, typeContext) : Format(type);

    private static string GenericParameterName(GenericParameterSignature parameter, IHasGenericParameters? typeContext, IHasGenericParameters? methodContext)
    {
        var owner = parameter.ParameterType == GenericParameterType.Method ? methodContext : typeContext;
        if (owner is not null && parameter.Index < owner.GenericParameters.Count)
            return owner.GenericParameters[parameter.Index].Name?.ToString() ?? "";
        return parameter.ParameterType == GenericParameterType.Method ? $"!!{parameter.Index}" : $"!{parameter.Index}";
    }

    private static string StripArity(string name) => name.IndexOf('`') is >= 0 and var tick ? name[..tick] : name;
}
