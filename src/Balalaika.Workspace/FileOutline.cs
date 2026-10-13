using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Balalaika.Index;

namespace Balalaika.Workspace;

/// <summary>One declaration in an outline: indented by nesting, with the line span the reader needs to read exactly that declaration.</summary>
public sealed record OutlineEntry(int Indent, string Text, int StartLine, int EndLine)
{
    /// <summary><c>  text  12-30</c>, or <c>  text  12</c> for a single-line declaration; <paramref name="extraIndent"/> shifts the whole entry.</summary>
    public string Format(int extraIndent = 0)
    {
        string span = this.EndLine > this.StartLine ? $"{this.StartLine}-{this.EndLine}" : this.StartLine.ToString();
        return $"{new string(' ', 2 + (this.Indent + extraIndent) * 2)}{this.Text}  {span}";
    }
}

/// <summary>
/// Maps a file or a type without reading it: one line per declaration in source order, indented by nesting, with
/// kind, the compact signature <c>list_members</c> uses, every attribute by short name (plus its first argument when
/// that is a string), and the line span excluding leading trivia so doc comments are not counted. Attributes are
/// printed without an allow-list, so routes, test markers and job triggers all show the same way without the server
/// knowing any framework. An entry at indent <c>n</c> is shown when <c>depth &gt;= n + 1</c>: 1 lists types, 2 adds
/// their members and nested types, 3 adds the nested types' members.
/// </summary>
public static class FileOutline
{
    /// <summary>Outline of a whole compilation unit.</summary>
    public static List<OutlineEntry> Build(SyntaxNode root, SemanticModel model, int depth, bool includePrivate, CancellationToken cancellationToken)
    {
        return BuildForTypes(TopLevelTypes(root), model, depth, includePrivate, cancellationToken);
    }

    /// <summary>Outline of the given type declarations (for example the parts of one partial type in one file) and what they contain.</summary>
    public static List<OutlineEntry> BuildForTypes(IEnumerable<SyntaxNode> typeNodes, SemanticModel model, int depth, bool includePrivate, CancellationToken cancellationToken)
    {
        List<OutlineEntry> entries = [];
        foreach (SyntaxNode node in typeNodes)
        {
            if (node is MemberDeclarationSyntax member && IsType(member))
            {
                Visit(member, 0, model, depth, includePrivate, entries, cancellationToken);
            }
        }

        return entries;
    }

    /// <summary>
    /// Outline from stored symbol rows of one file, for a solution that is not loaded. Nesting follows
    /// <see cref="SymbolRecord.ContainingDocId"/>; the text is the stored signature, which already carries
    /// accessibility, modifiers and kind. Attributes are not indexed and therefore absent.
    /// </summary>
    public static List<OutlineEntry> FromIndex(IReadOnlyList<SymbolRecord> symbols, int depth, bool includePrivate)
    {
        Dictionary<string, SymbolRecord> byDocId = new(StringComparer.Ordinal);
        foreach (SymbolRecord s in symbols)
        {
            byDocId.TryAdd(s.DocId, s);
        }

        List<OutlineEntry> entries = [];
        foreach (SymbolRecord s in symbols.OrderBy(s => s.Line).ThenBy(s => s.Column))
        {
            int indent = 0;
            bool hidden = !includePrivate && DeclarationSurface.IsPrivate(s.Accessibility);
            for (string? containing = s.ContainingDocId; containing is not null && byDocId.TryGetValue(containing, out SymbolRecord? parent); containing = parent.ContainingDocId)
            {
                indent++;
                hidden |= !includePrivate && DeclarationSurface.IsPrivate(parent.Accessibility);
            }

            if (hidden || depth < indent + 1)
            {
                continue;
            }

            entries.Add(new OutlineEntry(indent, s.Signature, s.Line, Math.Max(s.Line, s.EndLine)));
        }

        return entries;
    }

    private static IEnumerable<MemberDeclarationSyntax> TopLevelTypes(SyntaxNode root)
    {
        IEnumerable<MemberDeclarationSyntax> members = root switch
        {
            CompilationUnitSyntax unit => unit.Members,
            BaseNamespaceDeclarationSyntax ns => ns.Members,
            _ => [],
        };

        foreach (MemberDeclarationSyntax member in members)
        {
            if (member is BaseNamespaceDeclarationSyntax nested)
            {
                foreach (MemberDeclarationSyntax inner in TopLevelTypes(nested))
                {
                    yield return inner;
                }
            }
            else if (IsType(member))
            {
                yield return member;
            }
        }
    }

    private static bool IsType(MemberDeclarationSyntax member)
    {
        return member is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax;
    }

    private static void Visit(MemberDeclarationSyntax typeNode, int nesting, SemanticModel model, int depth, bool includePrivate, List<OutlineEntry> entries, CancellationToken cancellationToken)
    {
        if (depth < nesting + 1)
        {
            return;
        }

        ISymbol? type = model.GetDeclaredSymbol(typeNode, cancellationToken);
        if (type is null || (!includePrivate && type.DeclaredAccessibility == Accessibility.Private))
        {
            return;
        }

        entries.Add(new OutlineEntry(nesting, TypeText(typeNode, type, model, cancellationToken), StartLine(typeNode), EndLine(typeNode)));
        if (depth < nesting + 2)
        {
            return;
        }

        if (typeNode is TypeDeclarationSyntax { ParameterList: { } parameters } && type is INamedTypeSymbol { IsRecord: true } record)
        {
            // Positional record parameters declare properties with no member syntax of their own.
            foreach (ParameterSyntax parameter in parameters.Parameters)
            {
                IPropertySymbol? property = record.GetMembers(parameter.Identifier.ValueText).OfType<IPropertySymbol>().FirstOrDefault();
                if (property is not null)
                {
                    AddMember(property, parameter, parameter.AttributeLists, nesting + 1, includePrivate, entries, model, cancellationToken);
                }
            }
        }

        IEnumerable<MemberDeclarationSyntax> members = typeNode switch
        {
            TypeDeclarationSyntax t => t.Members,
            EnumDeclarationSyntax e => e.Members,
            _ => [],
        };

        foreach (MemberDeclarationSyntax member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (member)
            {
                case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                    Visit(member, nesting + 1, model, depth, includePrivate, entries, cancellationToken);
                    break;

                case BaseFieldDeclarationSyntax field:
                    foreach (VariableDeclaratorSyntax declarator in field.Declaration.Variables)
                    {
                        AddMember(model.GetDeclaredSymbol(declarator, cancellationToken), field, field.AttributeLists, nesting + 1, includePrivate, entries, model, cancellationToken);
                    }

                    break;

                default:
                    AddMember(model.GetDeclaredSymbol(member, cancellationToken), member, member.AttributeLists, nesting + 1, includePrivate, entries, model, cancellationToken);
                    break;
            }
        }
    }

    private static void AddMember(ISymbol? symbol, SyntaxNode span, SyntaxList<AttributeListSyntax> attributeLists, int indent, bool includePrivate, List<OutlineEntry> entries, SemanticModel model, CancellationToken cancellationToken)
    {
        if (symbol is null || symbol.IsImplicitlyDeclared || (!includePrivate && symbol.DeclaredAccessibility == Accessibility.Private))
        {
            return;
        }

        StringBuilder text = new();
        AppendPrefix(text, symbol);
        text.Append(symbol.ToDisplayString(DisplayFormats.Compact));
        AppendAttributes(text, attributeLists, model, cancellationToken);
        entries.Add(new OutlineEntry(indent, text.ToString(), StartLine(span), EndLine(span)));
    }

    private static string TypeText(MemberDeclarationSyntax typeNode, ISymbol type, SemanticModel model, CancellationToken cancellationToken)
    {
        StringBuilder text = new();
        AppendPrefix(text, type);
        text.Append(type.ToDisplayString(DisplayFormats.Compact));
        if (typeNode is BaseTypeDeclarationSyntax { BaseList: { } baseList })
        {
            text.Append(' ').Append(Collapse(baseList.ToString()));
        }

        AppendAttributes(text, typeNode.AttributeLists, model, cancellationToken);
        return text.ToString();
    }

    /// <summary>Accessibility unless public, <c>static</c>, then the kind word, as <c>list_members</c> prints it.</summary>
    private static void AppendPrefix(StringBuilder text, ISymbol symbol)
    {
        if (symbol.DeclaredAccessibility != Accessibility.Public && symbol.DeclaredAccessibility != Accessibility.NotApplicable)
        {
            text.Append(symbol.DeclaredAccessibility.ToString().ToLowerInvariant()).Append(' ');
        }

        if (symbol.IsStatic && symbol is not INamedTypeSymbol { TypeKind: TypeKind.Enum })
        {
            text.Append("static ");
        }

        text.Append(DisplayFormats.KindOf(symbol)).Append(' ');
    }

    /// <summary>
    /// Every attribute as <c>[Name]</c> or <c>[Name("first string argument")]</c>: the short name without the
    /// <c>Attribute</c> suffix, and the first positional argument only when it is a string literal or a constant that
    /// resolves to one. Other arguments are dropped.
    /// </summary>
    private static void AppendAttributes(StringBuilder text, SyntaxList<AttributeListSyntax> attributeLists, SemanticModel model, CancellationToken cancellationToken)
    {
        bool first = true;
        foreach (AttributeListSyntax list in attributeLists)
        {
            foreach (AttributeSyntax attribute in list.Attributes)
            {
                text.Append(first ? "  [" : " [").Append(ShortName(attribute.Name));
                first = false;
                AttributeArgumentSyntax? argument = attribute.ArgumentList?.Arguments.FirstOrDefault();
                if (argument is { NameEquals: null } && StringValue(argument.Expression, model, cancellationToken) is { } value)
                {
                    text.Append('(').Append(SymbolDisplay.FormatLiteral(value, quote: true)).Append(')');
                }

                text.Append(']');
            }
        }
    }

    private static string ShortName(NameSyntax name)
    {
        string identifier = name switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => name.ToString(),
        };

        const string suffix = "Attribute";
        return identifier.Length > suffix.Length && identifier.EndsWith(suffix, StringComparison.Ordinal) ? identifier[..^suffix.Length] : identifier;
    }

    private static string? StringValue(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken)
    {
        if (expression is LiteralExpressionSyntax { Token.Value: string literal })
        {
            return literal;
        }

        Optional<object?> constant = model.GetConstantValue(expression, cancellationToken);
        return constant is { HasValue: true, Value: string value } ? value : null;
    }

    private static int StartLine(SyntaxNode node)
    {
        return node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    }

    private static int EndLine(SyntaxNode node)
    {
        return node.GetLocation().GetLineSpan().EndLinePosition.Line + 1;
    }

    private static string Collapse(string s)
    {
        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
