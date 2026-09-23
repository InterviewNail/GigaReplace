using GigaReplace.Core.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GigaReplace.Core.Analysis.CSharp;

/// <summary>Classifies matches in a C# file with Roslyn's syntax tree (no build or semantic model needed).</summary>
internal sealed class CSharpMatchAnalyzer : IFileAnalyzer
{
    private enum DeclKind
    {
        None,
        Namespace,
        Type,
        Member,
        Local,
        Other,
    }

    private readonly FileAnalysisContext _ctx;
    private readonly SyntaxNode _root;
    private readonly SyntaxNode? _alternate;
    private readonly Dictionary<SyntaxNode, HashSet<string>> _scopeLocals = [];

    public CSharpMatchAnalyzer(FileAnalysisContext ctx)
    {
        _ctx = ctx;
        (_root, _alternate) = CSharpParsing.Parse(ctx.Text, ctx.RelativePath);
    }

    public MatchAnalysis Analyze(int matchIndex)
    {
        var match = _ctx.Matches[matchIndex];
        int position = FirstNonWhitespace(match);
        return Classify(_root, match, position)
            ?? (_alternate is not null ? Classify(_alternate, match, position) : null)
            ?? Inactive(match);
    }

    private int FirstNonWhitespace(RawMatch match)
    {
        for (int i = match.Start; i < match.End; i++)
        {
            if (!char.IsWhiteSpace(_ctx.Text[i]))
            {
                return i;
            }
        }

        return match.Start;
    }

    /// <summary>Null when the position is inside an inactive #if block of this tree.</summary>
    private MatchAnalysis? Classify(SyntaxNode root, RawMatch match, int position)
    {
        var token = root.FindToken(position, findInsideTrivia: true);
        if (token.Span.Contains(position))
        {
            return FromToken(token, match);
        }

        var trivia = root.FindTrivia(position, findInsideTrivia: true);
        return trivia.Kind() switch
        {
            SyntaxKind.DisabledTextTrivia => null,
            SyntaxKind.PreprocessingMessageTrivia => Comment(match, "preprocessor message"),
            _ => Comment(match, "comment"),
        };
    }

    private MatchAnalysis FromToken(SyntaxToken token, RawMatch match)
    {
        var structure = token.Parent?.FirstAncestorOrSelf<StructuredTriviaSyntax>(ascendOutOfTrivia: false);
        if (structure is DirectiveTriviaSyntax directive)
        {
            if (token.IsKind(SyntaxKind.IdentifierToken)
                && directive is IfDirectiveTriviaSyntax or ElifDirectiveTriviaSyntax or DefineDirectiveTriviaSyntax or UndefDirectiveTriviaSyntax)
            {
                return new MatchAnalysis
                {
                    Context = MatchContext.Preprocessor,
                    Detail = "preprocessor symbol",
                    WordStart = token.Span.Start,
                    WordLength = token.Span.Length,
                    SymbolKey = token.ValueText,
                };
            }

            return Comment(match, directive is RegionDirectiveTriviaSyntax or EndRegionDirectiveTriviaSyntax ? "#region label" : "preprocessor directive");
        }

        if (structure is DocumentationCommentTriviaSyntax)
        {
            return Comment(match, "XML doc comment");
        }

        if (structure is not null)
        {
            return MatchAnalysis.ForWord(_ctx.Text, match, MatchContext.Code, "skipped code");
        }

        if (match.Start < token.Span.Start || match.End > token.Span.End)
        {
            return MultiToken(match);
        }

        var kind = token.Kind();
        if (kind == SyntaxKind.IdentifierToken)
        {
            return Identifier(token);
        }

        if (IsStringToken(kind))
        {
            return StringUsageClassifier.Classify(_ctx, token, match);
        }

        var analysis = MatchAnalysis.ForWord(_ctx.Text, match, MatchContext.Code, "code");
        if (SyntaxFacts.IsKeywordKind(kind))
        {
            analysis.Add(RiskCode.InvalidIdentifier, $"This is inside the C# keyword “{token.Text}”. Changing it breaks the code.");
        }

        return analysis;
    }

    private static bool IsStringToken(SyntaxKind kind) => kind is SyntaxKind.StringLiteralToken
        or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.Utf8StringLiteralToken
        or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken
        or SyntaxKind.Utf8SingleLineRawStringLiteralToken or SyntaxKind.Utf8MultiLineRawStringLiteralToken
        or SyntaxKind.CharacterLiteralToken;

    private MatchAnalysis Identifier(SyntaxToken token)
    {
        string word = token.ValueText;
        bool verbatim = token.Text.StartsWith('@');
        int wordStart = token.Span.Start + (verbatim ? 1 : 0);
        int wordLength = token.Span.End - wordStart;
        string newWord = _ctx.ProposedSpan(wordStart, wordLength);
        var analysis = new MatchAnalysis { WordStart = wordStart, WordLength = wordLength };
        var (kind, node) = DeclarationOf(token);
        string? scope = null;

        switch (kind)
        {
            case DeclKind.Namespace:
                analysis.Context = MatchContext.Declaration;
                analysis.Detail = "namespace name";
                break;
            case DeclKind.Type:
                analysis.Context = MatchContext.Declaration;
                analysis.Detail = TypeKeyword(node!) + " declaration";
                TypeChecks(node!, word, newWord, analysis);
                break;
            case DeclKind.Member:
                analysis.Context = MatchContext.Declaration;
                analysis.Detail = MemberKeyword(node!) + " declaration";
                MemberChecks(node!, word, newWord, analysis);
                break;
            case DeclKind.Local:
                analysis.Context = MatchContext.Declaration;
                analysis.Detail = LocalKeyword(node!) + " declaration";
                scope = LocalChecks(node!, word, newWord, analysis);
                break;
            case DeclKind.Other:
                analysis.Context = MatchContext.Declaration;
                analysis.Detail = "name declaration";
                break;
            default:
                analysis.Context = MatchContext.Identifier;
                analysis.Detail = ReferenceDetail(token);
                scope = ScopeOfReference(token, word);
                if (scope is null && !_ctx.Index.IsDeclared(word))
                {
                    analysis.Add(
                        RiskCode.ExternalSymbol,
                        $"“{word}” isn't declared anywhere in this folder tree, so it probably comes from .NET or a NuGet package. Renaming it here won't rename the real thing, and the code won't compile.");
                }

                break;
        }

        analysis.SymbolKey = scope is null ? word : word + "@" + scope;

        if (!string.Equals(newWord, word, StringComparison.Ordinal))
        {
            if (!SyntaxFacts.IsValidIdentifier(newWord))
            {
                analysis.Add(RiskCode.InvalidIdentifier, $"“{newWord}” isn't a valid C# name.");
            }
            else if (!verbatim && SyntaxFacts.GetKeywordKind(newWord) != SyntaxKind.None)
            {
                analysis.Add(RiskCode.InvalidIdentifier, $"“{newWord}” is a C# keyword.");
            }
        }

        return analysis;
    }

    private static (DeclKind Kind, SyntaxNode? Node) DeclarationOf(SyntaxToken token)
    {
        switch (token.Parent)
        {
            case BaseTypeDeclarationSyntax t when t.Identifier == token:
                return (DeclKind.Type, t);
            case DelegateDeclarationSyntax d when d.Identifier == token:
                return (DeclKind.Type, d);
            case MethodDeclarationSyntax m when m.Identifier == token:
                return (DeclKind.Member, m);
            case PropertyDeclarationSyntax p when p.Identifier == token:
                return (DeclKind.Member, p);
            case EventDeclarationSyntax e when e.Identifier == token:
                return (DeclKind.Member, e);
            case EnumMemberDeclarationSyntax em when em.Identifier == token:
                return (DeclKind.Member, em);
            case VariableDeclaratorSyntax v when v.Identifier == token:
                return v.Parent?.Parent is BaseFieldDeclarationSyntax ? (DeclKind.Member, v) : (DeclKind.Local, v);
            case ParameterSyntax p when p.Identifier == token:
                return p.Parent?.Parent is RecordDeclarationSyntax ? (DeclKind.Member, p) : (DeclKind.Local, p);
            case TypeParameterSyntax tp:
                return tp.Parent?.Parent is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax ? (DeclKind.Other, tp) : (DeclKind.Local, tp);
            case LocalFunctionStatementSyntax lf when lf.Identifier == token:
                return (DeclKind.Local, lf);
            case SingleVariableDesignationSyntax sv:
                return (DeclKind.Local, sv);
            case ForEachStatementSyntax fe when fe.Identifier == token:
                return (DeclKind.Local, fe);
            case CatchDeclarationSyntax cd when cd.Identifier == token:
                return (DeclKind.Local, cd);
            case LabeledStatementSyntax ls when ls.Identifier == token:
                return (DeclKind.Local, ls);
            case FromClauseSyntax or LetClauseSyntax or JoinClauseSyntax or JoinIntoClauseSyntax or QueryContinuationSyntax:
                return (DeclKind.Local, token.Parent);
            case TupleElementSyntax te:
                return (DeclKind.Other, te);
            case ExternAliasDirectiveSyntax ea:
                return (DeclKind.Other, ea);
            case IdentifierNameSyntax id:
                if (id.Parent is NameEqualsSyntax { Parent: UsingDirectiveSyntax or AnonymousObjectMemberDeclaratorSyntax } nameEquals)
                {
                    return (DeclKind.Other, nameEquals.Parent);
                }

                if (id.Parent is NameColonSyntax { Parent: ArgumentSyntax { Parent: TupleExpressionSyntax } } nameColon)
                {
                    return (DeclKind.Other, nameColon);
                }

                var ns = id.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>();
                if (ns is not null && ns.Name.Span.Contains(id.Span))
                {
                    return (DeclKind.Namespace, ns);
                }

                return (DeclKind.None, null);
            default:
                return (DeclKind.None, null);
        }
    }

    private void TypeChecks(SyntaxNode declaration, string word, string newWord, MatchAnalysis analysis)
    {
        var index = _ctx.Index;
        if (!string.Equals(newWord, word, StringComparison.Ordinal))
        {
            var containing = declaration.Parent?.FirstAncestorOrSelf<BaseTypeDeclarationSyntax>();
            if (containing is null)
            {
                string ns = Names.NamespaceOf(declaration);
                if (index.TypeExists(ns, newWord))
                {
                    var where = ns.Length == 0 ? "the global namespace" : $"namespace {ns}";
                    analysis.Add(RiskCode.NameCollision, $"A type named “{newWord}” already exists in {where}.");
                }
            }
            else if (index.MemberExists(Names.TypeKey(containing), newWord))
            {
                analysis.Add(RiskCode.NameCollision, $"“{containing.Identifier.ValueText}” already has a member named “{newWord}”.");
            }
        }

        if (index.XmlContractTypes.Contains(word))
        {
            analysis.Add(
                RiskCode.SerializedTypeName,
                $"“{word}” is written into saved data by name ({index.SerializedReason(word)}). Existing files or messages that use the old name won't load.");
        }
    }

    private void MemberChecks(SyntaxNode declaration, string word, string newWord, MatchAnalysis analysis)
    {
        var type = declaration.FirstAncestorOrSelf<BaseTypeDeclarationSyntax>();
        if (type is null)
        {
            return;
        }

        string typeName = type.Identifier.ValueText;
        string typeKey = Names.TypeKey(type);
        if (!string.Equals(newWord, word, StringComparison.Ordinal))
        {
            if (_ctx.Index.MemberExists(typeKey, newWord))
            {
                analysis.Add(
                    RiskCode.NameCollision,
                    $"“{typeName}” already has a member named “{newWord}”. The rename would clash with it (or add an overload, if both are methods).");
            }

            if (_ctx.Index.LocalExists(typeKey, newWord))
            {
                analysis.Add(
                    RiskCode.Shadowing,
                    $"Code inside “{typeName}” already has a local variable or parameter named “{newWord}”. There it would hide the renamed member, so some code could quietly refer to something else.");
            }
        }

        PersistedMemberChecks(declaration, type, word, newWord, analysis);
    }

    private static readonly HashSet<string> IgnoredBySerializers = new(StringComparer.Ordinal)
    {
        "JsonIgnore", "XmlIgnore", "IgnoreDataMember", "NotMapped", "NonSerialized",
    };

    private static readonly HashSet<string> OptInSerialization = new(StringComparer.Ordinal)
    {
        "JsonInclude", "DataMember", "JsonProperty", "Column", "Key", "BsonElement", "ProtoMember",
    };

    private static readonly HashSet<string> NamingAttributes = new(StringComparer.Ordinal)
    {
        "JsonPropertyName", "JsonProperty", "DataMember", "XmlElement", "XmlAttribute", "XmlArray", "Column",
        "BsonElement", "EnumMember", "JsonStringEnumMemberName",
    };

    private void PersistedMemberChecks(SyntaxNode declaration, BaseTypeDeclarationSyntax type, string word, string newWord, MatchAnalysis analysis)
    {
        var index = _ctx.Index;
        string typeName = type.Identifier.ValueText;

        if (declaration is EnumMemberDeclarationSyntax enumMember)
        {
            bool savedAsText = index.SerializedTypes.ContainsKey(typeName)
                && (index.UsesStringEnumConverter || index.StringEnums.Contains(typeName));
            if (savedAsText && !HasExplicitName(enumMember.AttributeLists))
            {
                analysis.Add(
                    RiskCode.SerializedMember,
                    $"“{typeName}” values appear to be saved as text (JsonStringEnumConverter). Stored data containing “{word}” won't read back after the rename.");
            }

            return;
        }

        SyntaxList<AttributeListSyntax> attributes;
        SyntaxTokenList modifiers;
        switch (declaration)
        {
            case PropertyDeclarationSyntax property:
                attributes = property.AttributeLists;
                modifiers = property.Modifiers;
                break;
            case VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax field }:
                attributes = field.AttributeLists;
                modifiers = field.Modifiers;
                break;
            case ParameterSyntax parameter:
                attributes = parameter.AttributeLists;
                modifiers = default;
                break;
            default:
                return;
        }

        if (modifiers.Any(SyntaxKind.StaticKeyword) || modifiers.Any(SyntaxKind.ConstKeyword))
        {
            return;
        }

        var attributeNames = Names.AttributeNames(attributes).ToHashSet(StringComparer.Ordinal);
        if (attributeNames.Overlaps(IgnoredBySerializers) || HasExplicitName(attributes))
        {
            return;
        }

        if (index.SettingsTypes.Contains(typeName))
        {
            analysis.Add(
                RiskCode.UserSettings,
                $"“{word}” is a user setting of “{typeName}”. It's saved in user.config under this name, so existing users' saved values won't load.");
            return;
        }

        if (index.SerializedTypes.TryGetValue(typeName, out var reason))
        {
            bool isPublic = modifiers.Any(SyntaxKind.PublicKeyword) || declaration is ParameterSyntax || type is InterfaceDeclarationSyntax;
            if (!isPublic && !attributeNames.Overlaps(OptInSerialization))
            {
                return;
            }

            analysis.Add(
                RiskCode.SerializedMember,
                $"“{typeName}” looks serialized ({reason}), and “{word}” becomes a key in that data. Saved files, database columns or API payloads that use “{word}” won't match “{newWord}”.");
        }
    }

    private static bool HasExplicitName(SyntaxList<AttributeListSyntax> lists)
    {
        foreach (var attribute in lists.SelectMany(l => l.Attributes))
        {
            if (!NamingAttributes.Contains(Names.AttributeName(attribute.Name)) || attribute.ArgumentList is null)
            {
                continue;
            }

            foreach (var argument in attribute.ArgumentList.Arguments)
            {
                var named = argument.NameEquals?.Name.Identifier.ValueText;
                if (named is "Name" or "PropertyName" or "ElementName" or "AttributeName" or "Value")
                {
                    return true;
                }

                if (named is null && argument.NameColon is null && argument.Expression is LiteralExpressionSyntax literal
                    && literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private string? LocalChecks(SyntaxNode declaration, string word, string newWord, MatchAnalysis analysis)
    {
        var scopeNode = ScopeNodeForDeclaration(declaration);
        if (scopeNode is null)
        {
            return null;
        }

        if (!string.Equals(newWord, word, StringComparison.Ordinal))
        {
            if (LocalsIn(scopeNode).Contains(newWord))
            {
                analysis.Add(RiskCode.NameCollision, $"This member already has a local variable or parameter named “{newWord}”.");
            }

            var type = declaration.FirstAncestorOrSelf<BaseTypeDeclarationSyntax>();
            if (type is not null && _ctx.Index.MemberExists(Names.TypeKey(type), newWord))
            {
                analysis.Add(
                    RiskCode.Shadowing,
                    $"“{type.Identifier.ValueText}” has a member named “{newWord}”. The renamed variable would hide it here, so code that meant the member would use the variable instead.");
            }
        }

        return ScopeId(scopeNode);
    }

    private static SyntaxNode? ScopeNodeForDeclaration(SyntaxNode declaration)
    {
        if (declaration is ParameterSyntax { Parent.Parent: TypeDeclarationSyntax owner })
        {
            return owner;
        }

        return MemberScopeNode(declaration);
    }

    private static SyntaxNode? MemberScopeNode(SyntaxNode node)
    {
        foreach (var n in node.AncestorsAndSelf())
        {
            switch (n)
            {
                case BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or BaseFieldDeclarationSyntax
                    or EnumMemberDeclarationSyntax or DelegateDeclarationSyntax:
                    return n;
                case GlobalStatementSyntax:
                    // Top-level statements share one scope.
                    return n.Parent;
                case BaseTypeDeclarationSyntax:
                    return null;
            }
        }

        return null;
    }

    private string? ScopeOfReference(SyntaxToken token, string word)
    {
        if (token.Parent is not IdentifierNameSyntax id)
        {
            return null;
        }

        switch (id.Parent)
        {
            case MemberAccessExpressionSyntax access when access.Name == id:
            case MemberBindingExpressionSyntax:
            case QualifiedNameSyntax qualified when qualified.Right == id:
            case NameColonSyntax or NameEqualsSyntax:
                return null;
        }

        var member = MemberScopeNode(id);
        if (member is not null && LocalsIn(member).Contains(word))
        {
            return ScopeId(member);
        }

        var type = id.FirstAncestorOrSelf<TypeDeclarationSyntax>();
        if (type is not null and not RecordDeclarationSyntax
            && type.ParameterList is { } parameters
            && parameters.Parameters.Any(p => p.Identifier.ValueText == word))
        {
            return ScopeId(type);
        }

        return null;
    }

    private HashSet<string> LocalsIn(SyntaxNode scope)
    {
        if (_scopeLocals.TryGetValue(scope, out var cached))
        {
            return cached;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        if (scope is TypeDeclarationSyntax type)
        {
            if (type.ParameterList is { } parameters)
            {
                foreach (var p in parameters.Parameters)
                {
                    names.Add(p.Identifier.ValueText);
                }
            }
        }
        else
        {
            foreach (var n in scope.DescendantNodesAndSelf())
            {
                var identifier = n switch
                {
                    VariableDeclaratorSyntax v when v.Parent?.Parent is not BaseFieldDeclarationSyntax => v.Identifier,
                    ParameterSyntax p => p.Identifier,
                    SingleVariableDesignationSyntax s => s.Identifier,
                    ForEachStatementSyntax f => f.Identifier,
                    CatchDeclarationSyntax c => c.Identifier,
                    LocalFunctionStatementSyntax lf => lf.Identifier,
                    TypeParameterSyntax tp => tp.Identifier,
                    LabeledStatementSyntax ls => ls.Identifier,
                    FromClauseSyntax fc => fc.Identifier,
                    LetClauseSyntax lc => lc.Identifier,
                    JoinClauseSyntax jc => jc.Identifier,
                    JoinIntoClauseSyntax ji => ji.Identifier,
                    QueryContinuationSyntax qc => qc.Identifier,
                    _ => default,
                };

                if (identifier.ValueText is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }
        }

        _scopeLocals[scope] = names;
        return names;
    }

    private string ScopeId(SyntaxNode node) => $"{_ctx.RelativePath}#{node.SpanStart}";

    private MatchAnalysis MultiToken(RawMatch match)
    {
        var (start, length) = WordShape.ExpandToWord(_ctx.Text, match.Start, match.Length, c => WordShape.IsWordChar(c) || c == '.');
        return new MatchAnalysis
        {
            Context = MatchContext.Code,
            Detail = "code spanning several tokens",
            WordStart = start,
            WordLength = length,
            SymbolKey = _ctx.Text.Substring(start, length),
        };
    }

    private MatchAnalysis Inactive(RawMatch match) =>
        MatchAnalysis.ForWord(_ctx.Text, match, MatchContext.InactiveCode, "code in an inactive #if block", symbol: true);

    private MatchAnalysis Comment(RawMatch match, string detail) =>
        MatchAnalysis.ForWord(_ctx.Text, match, MatchContext.Comment, detail);

    private static string ReferenceDetail(SyntaxToken token)
    {
        var node = token.Parent;
        if (node is ConstructorDeclarationSyntax or DestructorDeclarationSyntax)
        {
            return "constructor name";
        }

        if (node?.FirstAncestorOrSelf<UsingDirectiveSyntax>() is not null)
        {
            return "using directive";
        }

        if (node?.Parent is AttributeSyntax)
        {
            return "attribute name";
        }

        if (node?.FirstAncestorOrSelf<InvocationExpressionSyntax>() is { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } })
        {
            return "nameof(…)";
        }

        return "code reference";
    }

    private static string TypeKeyword(SyntaxNode node) => node switch
    {
        ClassDeclarationSyntax => "class",
        StructDeclarationSyntax => "struct",
        InterfaceDeclarationSyntax => "interface",
        RecordDeclarationSyntax r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record",
        EnumDeclarationSyntax => "enum",
        DelegateDeclarationSyntax => "delegate",
        _ => "type",
    };

    private static string MemberKeyword(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax => "method",
        PropertyDeclarationSyntax => "property",
        EventDeclarationSyntax => "event",
        VariableDeclaratorSyntax { Parent.Parent: EventFieldDeclarationSyntax } => "event",
        VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax field } when field.Modifiers.Any(SyntaxKind.ConstKeyword) => "constant",
        VariableDeclaratorSyntax => "field",
        EnumMemberDeclarationSyntax => "enum member",
        ParameterSyntax => "record property",
        _ => "member",
    };

    private static string LocalKeyword(SyntaxNode node) => node switch
    {
        ParameterSyntax => "parameter",
        TypeParameterSyntax => "type parameter",
        LocalFunctionStatementSyntax => "local function",
        LabeledStatementSyntax => "label",
        FromClauseSyntax or LetClauseSyntax or JoinClauseSyntax or JoinIntoClauseSyntax or QueryContinuationSyntax => "query variable",
        _ => "local variable",
    };
}
