using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Balalaika.Index;

namespace Balalaika.Workspace;

/// <summary>
/// Finds the declaration a piece of syntax sits in: the nearest method, constructor, property (also from inside an
/// accessor), event, field (from its initializer or its type), enum member, delegate, or, failing those, the type
/// (base lists, attributes, type-level constraints, positional record parameters). Lambdas and local functions report
/// their containing member, not themselves. Top-level statements report <see cref="EnclosingDisplay.TopLevel"/>.
/// Used by <see cref="DocumentExtractor"/> for the index and by the live <c>find_references</c> path, so both print
/// the same name for the same source.
/// </summary>
public static class EnclosingMember
{
    /// <summary>The enclosing declaration node, a <see cref="GlobalStatementSyntax"/> for top-level statements, or null outside any declaration (usings, assembly attributes).</summary>
    public static SyntaxNode? FindDeclaration(SyntaxNode node)
    {
        for (SyntaxNode? current = node; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax }:
                case BaseFieldDeclarationSyntax:
                case MethodDeclarationSyntax or ConstructorDeclarationSyntax or DestructorDeclarationSyntax
                    or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax
                    or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax or EnumMemberDeclarationSyntax
                    or DelegateDeclarationSyntax or BaseTypeDeclarationSyntax or GlobalStatementSyntax:
                    return current;
            }
        }

        return null;
    }

    /// <summary>The symbol a node returned by <see cref="FindDeclaration"/> declares. A field declaration with several variables yields the first.</summary>
    public static ISymbol? Resolve(SemanticModel model, SyntaxNode declaration, CancellationToken cancellationToken)
    {
        return declaration switch
        {
            GlobalStatementSyntax => null,
            BaseFieldDeclarationSyntax field => field.Declaration.Variables.Count > 0 ? model.GetDeclaredSymbol(field.Declaration.Variables[0], cancellationToken) : null,
            _ => model.GetDeclaredSymbol(declaration, cancellationToken),
        };
    }

    /// <summary>Doc id of the declaration enclosing <paramref name="node"/>, <see cref="EnclosingDisplay.TopLevel"/> for top-level statements, null when there is none.</summary>
    public static string? DocIdOf(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken)
    {
        return Describe(model, node, cancellationToken).DocId;
    }

    /// <summary>Doc id and display name (<c>Type.Member</c>) of the enclosing declaration; both null when there is none.</summary>
    public static (string? DocId, string? Display) Describe(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken)
    {
        SyntaxNode? declaration = FindDeclaration(node);
        if (declaration is null)
        {
            return (null, null);
        }

        if (declaration is GlobalStatementSyntax)
        {
            return (EnclosingDisplay.TopLevel, EnclosingDisplay.TopLevel);
        }

        ISymbol? symbol = Resolve(model, declaration, cancellationToken);
        if (symbol is null)
        {
            return (null, null);
        }

        return (symbol.GetDocumentationCommentId(), Display(symbol));
    }

    /// <summary>Same words the index path prints for a symbol row and its containing type row.</summary>
    public static string Display(ISymbol symbol)
    {
        return EnclosingDisplay.Format(symbol.ContainingType?.Name, symbol.Name);
    }
}
