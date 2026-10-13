using Microsoft.CodeAnalysis;

namespace Balalaika.Workspace;

public static class DisplayFormats
{
    /// <summary>Namespace-qualified name without the global:: prefix, e.g. Acme.Domain.Order.</summary>
    public static readonly SymbolDisplayFormat FullyQualified = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <summary>Namespace-qualified member with parameter types, e.g. Acme.Domain.Order.SetStatus(Status, Guid), so overloads stay distinct.</summary>
    public static readonly SymbolDisplayFormat FullyQualifiedWithParameters = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <summary>Signature as it would appear in a hover tooltip: return type, name, parameters with types and names.</summary>
    public static readonly SymbolDisplayFormat Signature = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
                       | SymbolDisplayMemberOptions.IncludeType
                       | SymbolDisplayMemberOptions.IncludeModifiers
                       | SymbolDisplayMemberOptions.IncludeAccessibility
                       | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        kindOptions: SymbolDisplayKindOptions.IncludeTypeKeyword | SymbolDisplayKindOptions.IncludeMemberKeyword,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
                          | SymbolDisplayParameterOptions.IncludeName
                          | SymbolDisplayParameterOptions.IncludeDefaultValue
                          | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        delegateStyle: SymbolDisplayDelegateStyle.NameAndSignature,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                              | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
                              | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>Compact member signature for lists: name and parameter types only.</summary>
    public static readonly SymbolDisplayFormat Compact = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                              | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static string KindOf(ISymbol symbol)
    {
        switch (symbol)
        {
            case INamedTypeSymbol t:
                if (t.IsRecord)
                {
                    return t.TypeKind == TypeKind.Struct ? "record struct" : "record";
                }

                return t.TypeKind switch
                {
                    TypeKind.Class => "class",
                    TypeKind.Interface => "interface",
                    TypeKind.Struct => "struct",
                    TypeKind.Enum => "enum",
                    TypeKind.Delegate => "delegate",
                    _ => t.TypeKind.ToString().ToLowerInvariant(),
                };
            case IMethodSymbol m:
                return m.MethodKind switch
                {
                    MethodKind.Constructor or MethodKind.StaticConstructor => "constructor",
                    MethodKind.UserDefinedOperator or MethodKind.Conversion or MethodKind.BuiltinOperator => "operator",
                    MethodKind.Destructor => "destructor",
                    _ => "method",
                };
            case IPropertySymbol p:
                return p.IsIndexer ? "indexer" : "property";
            case IFieldSymbol f:
                return f.ContainingType?.TypeKind == TypeKind.Enum ? "enum_member" : "field";
            case IEventSymbol:
                return "event";
            case INamespaceSymbol:
                return "namespace";
            default:
                return symbol.Kind.ToString().ToLowerInvariant();
        }
    }

    /// <summary>Reflection-style metadata name for decompilation: Ns.Outer+Inner`1.</summary>
    public static string ReflectionName(INamedTypeSymbol type)
    {
        type = type.OriginalDefinition;
        Stack<string> parts = new();
        INamedTypeSymbol? current = type;
        while (current is not null)
        {
            parts.Push(current.MetadataName);
            current = current.ContainingType;
        }

        string nested = string.Join("+", parts);
        INamespaceSymbol? ns = type.ContainingNamespace;
        if (ns is null || ns.IsGlobalNamespace)
        {
            return nested;
        }

        return ns.ToDisplayString() + "." + nested;
    }
}
