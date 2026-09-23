namespace GigaReplace.Core.Analysis.Markup;

internal sealed class XamlAnalyzer(FileAnalysisContext ctx) : IFileAnalyzer
{
    private static readonly HashSet<string> DisplayAttributes = new(StringComparer.Ordinal)
    {
        "Title", "Content", "Text", "Header", "ToolTip", "Description", "Label", "Caption", "PlaceholderText",
        "Watermark", "AutomationProperties.Name", "AutomationProperties.HelpText", "d:Text", "d:Content", "d:Title",
        "ContentStringFormat", "StringFormat", "Tag", "InputGestureText",
    };

    private static readonly HashSet<string> UriAttributes = new(StringComparer.Ordinal)
    {
        "Source", "ImageSource", "Icon", "StartupUri", "NavigateUri", "UriSource", "BaseUri", "DisplayMemberPath",
    };

    private readonly XmlDocumentMap _map = XmlDocumentMap.Build(ctx.Text);

    public MatchAnalysis Analyze(int matchIndex)
    {
        var match = ctx.Matches[matchIndex];
        var analysis = MatchAnalysis.ForWord(ctx.Text, match, MatchContext.MarkupText, "XAML");
        if (_map.Find(match.Start) is not { } span)
        {
            return analysis;
        }

        string word = analysis.Word(ctx.Text);
        switch (span.Kind)
        {
            case XmlSpanKind.Comment:
                analysis.Context = MatchContext.Comment;
                analysis.Detail = "XAML comment";
                break;
            case XmlSpanKind.ElementName:
                analysis.Context = MatchContext.MarkupName;
                analysis.Detail = "XAML element (a type or property)";
                analysis.SymbolKey = word;
                break;
            case XmlSpanKind.AttributeName:
                analysis.Context = MatchContext.MarkupName;
                analysis.Detail = "XAML attribute (a property or event)";
                analysis.SymbolKey = word;
                break;
            case XmlSpanKind.AttributeValue:
                AttributeValue(span, match.Start - span.Start, match.Length, word, analysis);
                break;
            default:
                analysis.Context = MatchContext.MarkupText;
                analysis.Detail = "display text";
                ValueHeuristics.Inspect(ctx.Text[span.Start..span.End], match.Start - span.Start, match.Length, analysis, allowPaths: false);
                break;
        }

        return analysis;
    }

    private void AttributeValue(XmlSpan span, int index, int length, string word, MatchAnalysis analysis)
    {
        analysis.Context = MatchContext.MarkupValue;
        string attribute = span.Attribute ?? "";
        string value = ctx.Text[span.Start..span.End];

        if (attribute == "x:Class")
        {
            analysis.Detail = "x:Class (must match the code-behind class)";
            analysis.SymbolKey = word;
            return;
        }

        if (attribute is "x:Name" or "Name")
        {
            analysis.Detail = "x:Name (becomes a field in the code-behind)";
            analysis.SymbolKey = word;
            return;
        }

        if (attribute.StartsWith("xmlns", StringComparison.Ordinal))
        {
            int assembly = value.IndexOf("assembly=", StringComparison.OrdinalIgnoreCase);
            if (assembly >= 0 && index >= assembly)
            {
                analysis.Detail = "assembly in an xmlns mapping";
                analysis.Add(RiskCode.AssemblyReference, "This XAML namespace mapping names an assembly. It must match that assembly's final name.");
            }
            else
            {
                analysis.Detail = "CLR namespace in an xmlns mapping";
                analysis.SymbolKey = word;
            }

            return;
        }

        if (value.Contains(";component", StringComparison.OrdinalIgnoreCase))
        {
            analysis.Detail = "pack URI";
            ValueHeuristics.Inspect(value, index, length, analysis, allowPaths: false);
            return;
        }

        if (value.TrimStart().StartsWith('{'))
        {
            analysis.Detail = "binding or markup extension (only checked at run time)";
            analysis.SymbolKey = word;
            return;
        }

        if (attribute == "x:Key")
        {
            analysis.Detail = "resource key";
            analysis.SymbolKey = word;
            return;
        }

        if (DisplayAttributes.Contains(attribute))
        {
            analysis.Detail = "display text";
            ValueHeuristics.Inspect(value, index, length, analysis, allowPaths: false);
            return;
        }

        if (UriAttributes.Contains(attribute))
        {
            analysis.Detail = $"{attribute} (file or URI)";
            ValueHeuristics.Inspect(value, index, length, analysis, allowPaths: false);
            return;
        }

        analysis.Detail = $"value of {attribute}";
        if (IsIdentifierLike(value))
        {
            // Event handlers, enum values, property paths.
            analysis.SymbolKey = word;
        }
        else
        {
            ValueHeuristics.Inspect(value, index, length, analysis, allowPaths: false);
        }
    }

    private static bool IsIdentifierLike(string value)
    {
        value = value.Trim();
        return value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_')
            && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '.');
    }
}
