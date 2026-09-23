using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GigaReplace.Core.Analysis.CSharp;

internal static class CSharpParsing
{
    public static readonly CSharpParseOptions Options = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.Preview)
        .WithDocumentationMode(DocumentationMode.Parse);

    /// <summary>
    /// Parses the file. When #if blocks hide code, a second parse defines every symbol those
    /// blocks test, so code in both branches of typical #if/#else pairs is analyzed.
    /// </summary>
    public static (SyntaxNode Root, SyntaxNode? Alternate) Parse(string text, string path)
    {
        var root = CSharpSyntaxTree.ParseText(text, Options, path).GetRoot();
        if (!root.ContainsDirectives)
        {
            return (root, null);
        }

        bool hasDisabledText = false;
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trivia in root.DescendantTrivia())
        {
            if (trivia.IsKind(SyntaxKind.DisabledTextTrivia))
            {
                hasDisabledText = true;
            }
            else if (trivia.GetStructure() is ConditionalDirectiveTriviaSyntax conditional)
            {
                foreach (var id in conditional.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                {
                    symbols.Add(id.Identifier.ValueText);
                }
            }
        }

        if (!hasDisabledText || symbols.Count == 0)
        {
            return (root, null);
        }

        var alternate = CSharpSyntaxTree.ParseText(text, Options.WithPreprocessorSymbols(symbols), path).GetRoot();
        return (root, alternate);
    }
}

internal static class Names
{
    public static string Combine(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;

    public static string Dotted(NameSyntax name) =>
        string.Join(".", name.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().Select(s => s.Identifier.ValueText));

    public static string? Simple(TypeSyntax? type) => type switch
    {
        IdentifierNameSyntax i => i.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        QualifiedNameSyntax q => Simple(q.Right),
        AliasQualifiedNameSyntax a => Simple(a.Name),
        NullableTypeSyntax n => Simple(n.ElementType),
        _ => null,
    };

    public static string AttributeName(NameSyntax name)
    {
        var simple = Simple(name) ?? name.ToString();
        return simple.Length > 9 && simple.EndsWith("Attribute", StringComparison.Ordinal) ? simple[..^9] : simple;
    }

    public static IEnumerable<string> AttributeNames(SyntaxList<AttributeListSyntax> lists) =>
        lists.SelectMany(l => l.Attributes).Select(a => AttributeName(a.Name));

    public static IEnumerable<AttributeSyntax> Attributes(SyntaxList<AttributeListSyntax> lists, string name) =>
        lists.SelectMany(l => l.Attributes).Where(a => AttributeName(a.Name) == name);

    /// <summary>The dotted namespace a node sits in (outermost first).</summary>
    public static string NamespaceOf(SyntaxNode node)
    {
        var parts = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Select(ns => Dotted(ns.Name)).Reverse();
        return string.Join(".", parts);
    }

    /// <summary>Namespace.Outer+Inner, matching the keys the indexer records.</summary>
    public static string TypeKey(BaseTypeDeclarationSyntax type)
    {
        var chain = type.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Select(t => t.Identifier.ValueText).Reverse();
        return Combine(NamespaceOf(type), string.Join("+", chain));
    }

    /// <summary>Type names a type expression refers to, unwrapping arrays, nullables and generics.</summary>
    public static void CollectTypeNames(TypeSyntax? type, List<string> into)
    {
        switch (type)
        {
            case NullableTypeSyntax n:
                CollectTypeNames(n.ElementType, into);
                break;
            case ArrayTypeSyntax a:
                CollectTypeNames(a.ElementType, into);
                break;
            case QualifiedNameSyntax q:
                CollectTypeNames(q.Right, into);
                break;
            case AliasQualifiedNameSyntax aq:
                CollectTypeNames(aq.Name, into);
                break;
            case GenericNameSyntax g:
                into.Add(g.Identifier.ValueText);
                foreach (var arg in g.TypeArgumentList.Arguments)
                {
                    CollectTypeNames(arg, into);
                }

                break;
            case IdentifierNameSyntax i:
                into.Add(i.Identifier.ValueText);
                break;
            case TupleTypeSyntax t:
                foreach (var element in t.Elements)
                {
                    CollectTypeNames(element.Type, into);
                }

                break;
            case RefTypeSyntax r:
                CollectTypeNames(r.Type, into);
                break;
            case ScopedTypeSyntax s:
                CollectTypeNames(s.Type, into);
                break;
        }
    }

    public static List<string> TypeNamesOf(TypeSyntax? type)
    {
        var list = new List<string>();
        CollectTypeNames(type, list);
        return list;
    }

    /// <summary>The name a generated [ObservableProperty] property gets from its backing field.</summary>
    public static string? ObservablePropertyName(string field)
    {
        var name = field.StartsWith("m_", StringComparison.Ordinal) ? field[2..] : field.TrimStart('_');
        if (name.Length == 0)
        {
            return null;
        }

        return char.ToUpperInvariant(name[0]) + name[1..];
    }
}
