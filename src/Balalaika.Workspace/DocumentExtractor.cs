using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Balalaika.Index;

namespace Balalaika.Workspace;

/// <summary>The normalised tokens of one declaration and its attributes as written; see <see cref="DocumentExtractor.DeclarationTexts"/>.</summary>
public sealed record DeclarationText(string Text, IReadOnlyList<string> Attributes);

/// <summary>
/// Extracts declarations, symbol usages and type relations from one document. The output depends on the document
/// text and on what it binds against in the compilation at extraction time; it is stored keyed by the text hash only
/// (see <see cref="IndexStore"/>). The <c>reindex</c> tool exists for the rare case where that assumption breaks.
/// </summary>
public static class DocumentExtractor
{
    public static async Task<DocumentExtraction> ExtractAsync(Document document, CancellationToken cancellationToken)
    {
        SemanticModel? model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (model is null || root is null)
        {
            return DocumentExtraction.Empty;
        }

        return Extract(model, root, cancellationToken);
    }

    public static DocumentExtraction Extract(SemanticModel model, SyntaxNode root, CancellationToken cancellationToken)
    {
        List<SymbolRecord> symbols = [];
        List<ReferenceRecord> references = [];
        List<RelationRecord> relations = [];
        HashSet<string> seenDeclarations = new(StringComparer.Ordinal);
        HashSet<(string, int, int)> seenReferences = [];
        Dictionary<SyntaxNode, string?> enclosingDocIds = [];

        foreach (SyntaxNode node in root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryDescribeDeclaration(model, node, cancellationToken, out ISymbol? declared, out SyntaxToken identifier, out SyntaxNode declaration))
            {
                AddDeclaration(declared, identifier, declaration, symbols, relations, seenDeclarations);
                continue;
            }

            switch (node)
            {
                case SimpleNameSyntax name:
                    if (name is IdentifierNameSyntax { IsVar: true })
                    {
                        break;
                    }

                    AddReference(model, name, name.Identifier, references, seenReferences, enclosingDocIds, cancellationToken);
                    break;

                case ObjectCreationExpressionSyntax creation:
                    AddReference(model, creation, creation.NewKeyword, references, seenReferences, enclosingDocIds, cancellationToken);
                    break;

                case ImplicitObjectCreationExpressionSyntax implicitCreation:
                    AddReference(model, implicitCreation, implicitCreation.NewKeyword, references, seenReferences, enclosingDocIds, cancellationToken);
                    break;

                case ConstructorInitializerSyntax initializer:
                    AddReference(model, initializer, initializer.ThisOrBaseKeyword, references, seenReferences, enclosingDocIds, cancellationToken);
                    break;

                case PrimaryConstructorBaseTypeSyntax primaryBase:
                    AddReference(model, primaryBase, primaryBase.ArgumentList.OpenParenToken, references, seenReferences, enclosingDocIds, cancellationToken);
                    break;
            }
        }

        return new DocumentExtraction(symbols, references, relations);
    }

    /// <summary>
    /// The usage kind of a node that refers to a symbol: the node a <c>find_references</c> location points at (a
    /// simple name, or the <c>new</c>, <c>this</c>, <c>base</c> or <c>(</c> token's parent) prints the same word as
    /// the index row the extractor stored for it.
    /// </summary>
    public static string UsageKindOf(SyntaxNode node)
    {
        return node switch
        {
            ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax => ReferenceKinds.Creation,
            ConstructorInitializerSyntax or PrimaryConstructorBaseTypeSyntax => ReferenceKinds.Invocation,
            _ => ClassifyUsage(node),
        };
    }

    /// <summary>
    /// The declaration text per declared doc id, as tokens joined by one space, so whitespace and comments do not
    /// count, plus its attributes as written. A type contributes only its header (attributes, modifiers, name, base
    /// list, constraints): its members are declarations of their own. Used by <c>review_changes</c> to tell a body
    /// edit from an untouched declaration and to say which attribute a header gained or lost.
    /// </summary>
    public static IReadOnlyDictionary<string, DeclarationText> DeclarationTexts(SemanticModel model, SyntaxNode root, CancellationToken cancellationToken)
    {
        Dictionary<string, DeclarationText> texts = new(StringComparer.Ordinal);
        foreach (SyntaxNode node in root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryDescribeDeclaration(model, node, cancellationToken, out ISymbol? symbol, out _, out SyntaxNode declaration))
            {
                continue;
            }

            string? docId = symbol?.GetDocumentationCommentId();
            if (docId is not null)
            {
                texts.TryAdd(docId, new DeclarationText(NormalizedText(declaration), AttributesOf(declaration)));
            }
        }

        return texts;
    }

    /// <summary>Each attribute of a declaration as written, whitespace collapsed: <c>Authorize("Admin")</c>.</summary>
    private static IReadOnlyList<string> AttributesOf(SyntaxNode declaration)
    {
        SyntaxList<AttributeListSyntax> lists = declaration switch
        {
            MemberDeclarationSyntax member => member.AttributeLists,
            ParameterSyntax parameter => parameter.AttributeLists,
            _ => default,
        };
        return lists.SelectMany(l => l.Attributes).Select(a => string.Join(" ", a.DescendantTokens().Select(t => t.Text))).ToList();
    }

    private static string NormalizedText(SyntaxNode declaration)
    {
        IEnumerable<SyntaxToken> tokens = declaration switch
        {
            TypeDeclarationSyntax t => t.DescendantTokens().TakeWhile(token => token != t.OpenBraceToken),
            EnumDeclarationSyntax e => e.DescendantTokens().TakeWhile(token => token != e.OpenBraceToken),
            _ => declaration.DescendantTokens(),
        };
        return string.Join(" ", tokens.Select(t => t.Text));
    }

    /// <summary>
    /// Whether <paramref name="node"/> declares a symbol the index records, and if so which symbol, its identifier
    /// token and the syntax node that spans the whole declaration (the field declaration for a declarator).
    /// </summary>
    private static bool TryDescribeDeclaration(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken, out ISymbol? symbol, out SyntaxToken identifier, out SyntaxNode declaration)
    {
        switch (node)
        {
            case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax
                or MethodDeclarationSyntax or ConstructorDeclarationSyntax or DestructorDeclarationSyntax
                or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax
                or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax or EnumMemberDeclarationSyntax:
                symbol = model.GetDeclaredSymbol(node, cancellationToken);
                identifier = IdentifierOf(node);
                declaration = node;
                return true;

            case VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax or EventFieldDeclarationSyntax } declarator:
                symbol = model.GetDeclaredSymbol(declarator, cancellationToken);
                identifier = declarator.Identifier;
                declaration = declarator.Parent!.Parent!;
                return true;

            case ParameterSyntax { Parent.Parent: RecordDeclarationSyntax record } parameter:
                // Positional record parameters synthesize properties with no syntax of their own.
                symbol = model.GetDeclaredSymbol(record, cancellationToken) is INamedTypeSymbol recordType
                    ? recordType.GetMembers(parameter.Identifier.ValueText).OfType<IPropertySymbol>().FirstOrDefault()
                    : null;
                identifier = parameter.Identifier;
                declaration = parameter;
                return true;

            default:
                symbol = null;
                identifier = default;
                declaration = node;
                return false;
        }
    }

    private static SyntaxToken IdentifierOf(SyntaxNode node)
    {
        return node switch
        {
            BaseTypeDeclarationSyntax t => t.Identifier,
            DelegateDeclarationSyntax d => d.Identifier,
            MethodDeclarationSyntax m => m.Identifier,
            ConstructorDeclarationSyntax c => c.Identifier,
            DestructorDeclarationSyntax d => d.Identifier,
            PropertyDeclarationSyntax p => p.Identifier,
            IndexerDeclarationSyntax i => i.ThisKeyword,
            EventDeclarationSyntax e => e.Identifier,
            OperatorDeclarationSyntax o => o.OperatorToken,
            ConversionOperatorDeclarationSyntax c => c.Type.GetFirstToken(),
            EnumMemberDeclarationSyntax e => e.Identifier,
            _ => node.GetFirstToken(),
        };
    }

    private static void AddDeclaration(
        ISymbol? symbol,
        SyntaxToken identifier,
        SyntaxNode declaration,
        List<SymbolRecord> symbols,
        List<RelationRecord> relations,
        HashSet<string> seen)
    {
        if (symbol is null)
        {
            return;
        }

        string? docId = symbol.GetDocumentationCommentId();
        if (docId is null || !seen.Add(docId))
        {
            return;
        }

        LinePosition pos = identifier.GetLocation().GetLineSpan().StartLinePosition;
        symbols.Add(new SymbolRecord(
            docId,
            DisplayFormats.KindOf(symbol),
            symbol.Name,
            symbol.ToDisplayString(DisplayFormats.FullyQualified),
            symbol.ToDisplayString(DisplayFormats.Signature),
            symbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
            symbol.ContainingType?.GetDocumentationCommentId(),
            pos.Line + 1,
            pos.Character + 1,
            declaration.GetLocation().GetLineSpan().EndLinePosition.Line + 1));

        AddRelations(symbol, docId, relations);
    }

    private static void AddRelations(ISymbol symbol, string fromDocId, List<RelationRecord> relations)
    {
        switch (symbol)
        {
            case INamedTypeSymbol type:
                if (type.BaseType is { } baseType && baseType.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum or SpecialType.System_Delegate or SpecialType.System_MulticastDelegate))
                {
                    AddRelation(relations, fromDocId, baseType, RelationKinds.Inherits);
                }

                foreach (INamedTypeSymbol iface in type.Interfaces)
                {
                    AddRelation(relations, fromDocId, iface, RelationKinds.Implements);
                }

                break;

            case IMethodSymbol method:
                if (method.OverriddenMethod is { } overridden)
                {
                    AddRelation(relations, fromDocId, overridden, RelationKinds.Overrides);
                }

                foreach (IMethodSymbol explicitImpl in method.ExplicitInterfaceImplementations)
                {
                    AddRelation(relations, fromDocId, explicitImpl, RelationKinds.Implements);
                }

                AddImplicitImplementations(method, fromDocId, relations);
                break;

            case IPropertySymbol property:
                if (property.OverriddenProperty is { } overriddenProperty)
                {
                    AddRelation(relations, fromDocId, overriddenProperty, RelationKinds.Overrides);
                }

                foreach (IPropertySymbol explicitImpl in property.ExplicitInterfaceImplementations)
                {
                    AddRelation(relations, fromDocId, explicitImpl, RelationKinds.Implements);
                }

                AddImplicitImplementations(property, fromDocId, relations);
                break;

            case IEventSymbol evt:
                if (evt.OverriddenEvent is { } overriddenEvent)
                {
                    AddRelation(relations, fromDocId, overriddenEvent, RelationKinds.Overrides);
                }

                foreach (IEventSymbol explicitImpl in evt.ExplicitInterfaceImplementations)
                {
                    AddRelation(relations, fromDocId, explicitImpl, RelationKinds.Implements);
                }

                AddImplicitImplementations(evt, fromDocId, relations);
                break;
        }
    }

    private static void AddImplicitImplementations(ISymbol member, string fromDocId, List<RelationRecord> relations)
    {
        INamedTypeSymbol? containing = member.ContainingType;
        if (containing is null || member.DeclaredAccessibility != Accessibility.Public || member.IsStatic)
        {
            return;
        }

        foreach (INamedTypeSymbol iface in containing.AllInterfaces)
        {
            foreach (ISymbol ifaceMember in iface.GetMembers(member.Name))
            {
                if (ifaceMember.Kind != member.Kind)
                {
                    continue;
                }

                ISymbol? impl = containing.FindImplementationForInterfaceMember(ifaceMember);
                if (impl is not null && SymbolEqualityComparer.Default.Equals(impl, member))
                {
                    AddRelation(relations, fromDocId, ifaceMember, RelationKinds.Implements);
                }
            }
        }
    }

    private static void AddRelation(List<RelationRecord> relations, string fromDocId, ISymbol target, string kind)
    {
        target = target.OriginalDefinition;
        string? toDocId = target.GetDocumentationCommentId();
        if (toDocId is null)
        {
            return;
        }

        relations.Add(new RelationRecord(fromDocId, toDocId, SymbolIdentity.AssemblyOf(target) ?? "", kind));
    }

    private static void AddReference(
        SemanticModel model,
        SyntaxNode node,
        SyntaxToken anchor,
        List<ReferenceRecord> references,
        HashSet<(string, int, int)> seen,
        Dictionary<SyntaxNode, string?> enclosingDocIds,
        CancellationToken cancellationToken)
    {
        SymbolInfo info = model.GetSymbolInfo(node, cancellationToken);
        ISymbol? symbol = info.Symbol ?? (info.CandidateSymbols.Length > 0 ? info.CandidateSymbols[0] : null);
        symbol = Normalize(symbol);
        if (symbol is null)
        {
            return;
        }

        string? docId = symbol.GetDocumentationCommentId();
        if (docId is null)
        {
            return;
        }

        LinePosition pos = anchor.GetLocation().GetLineSpan().StartLinePosition;
        int line = pos.Line + 1;
        int column = pos.Character + 1;
        if (!seen.Add((docId, line, column)))
        {
            return;
        }

        references.Add(new ReferenceRecord(docId, SymbolIdentity.AssemblyOf(symbol) ?? "", UsageKindOf(node), line, column, EnclosingDocIdOf(model, node, enclosingDocIds, cancellationToken)));
    }

    /// <summary>Doc id of the enclosing declaration, resolved once per declaration node; references cluster in few members.</summary>
    private static string? EnclosingDocIdOf(SemanticModel model, SyntaxNode node, Dictionary<SyntaxNode, string?> cache, CancellationToken cancellationToken)
    {
        SyntaxNode? declaration = EnclosingMember.FindDeclaration(node);
        if (declaration is null)
        {
            return null;
        }

        if (declaration is GlobalStatementSyntax)
        {
            return EnclosingDisplay.TopLevel;
        }

        if (!cache.TryGetValue(declaration, out string? docId))
        {
            docId = EnclosingMember.Resolve(model, declaration, cancellationToken)?.GetDocumentationCommentId();
            cache[declaration] = docId;
        }

        return docId;
    }

    private static ISymbol? Normalize(ISymbol? symbol)
    {
        if (symbol is IAliasSymbol alias)
        {
            symbol = alias.Target;
        }

        if (symbol is IMethodSymbol { ReducedFrom: { } reduced })
        {
            symbol = reduced;
        }

        if (symbol is null)
        {
            return null;
        }

        symbol = symbol.OriginalDefinition;

        switch (symbol.Kind)
        {
            case SymbolKind.Local:
            case SymbolKind.Parameter:
            case SymbolKind.Label:
            case SymbolKind.RangeVariable:
            case SymbolKind.TypeParameter:
            case SymbolKind.Namespace:
            case SymbolKind.Preprocessing:
            case SymbolKind.Discard:
            case SymbolKind.ErrorType:
            case SymbolKind.DynamicType:
            case SymbolKind.ArrayType:
            case SymbolKind.PointerType:
            case SymbolKind.FunctionPointerType:
                return null;
        }

        if (symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction })
        {
            return null;
        }

        return symbol;
    }

    private static string ClassifyUsage(SyntaxNode node)
    {
        SyntaxNode? parent = node.Parent;
        if (parent is MemberAccessExpressionSyntax { Name: var n } access && n == node)
        {
            parent = access.Parent;
            node = access;
        }
        else if (parent is QualifiedNameSyntax { Right: var r } qualified && r == node)
        {
            parent = qualified.Parent;
            node = qualified;
        }

        if (parent is InvocationExpressionSyntax { Expression: var expr } && expr == node)
        {
            return ReferenceKinds.Invocation;
        }

        if (parent is ObjectCreationExpressionSyntax)
        {
            return ReferenceKinds.Creation;
        }

        if (parent is AttributeSyntax)
        {
            return ReferenceKinds.Attribute;
        }

        if (parent is SimpleBaseTypeSyntax or PrimaryConstructorBaseTypeSyntax)
        {
            return ReferenceKinds.Base;
        }

        return ReferenceKinds.Usage;
    }
}
