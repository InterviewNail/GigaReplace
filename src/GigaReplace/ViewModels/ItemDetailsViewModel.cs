using GigaReplace.Core;
using GigaReplace.Core.Planning;

namespace GigaReplace.ViewModels;

/// <summary>A preview line, split so the match can be highlighted.</summary>
public sealed record PreviewLine(string Number, string Before, string Match, string After, bool IsMatchLine);

/// <summary>Everything the detail pane and the decision dialog show about one change.</summary>
public sealed class ItemDetailsViewModel
{
    public ItemDetailsViewModel(ScanResult scan, ChangeItem item)
    {
        Item = item;
        Header = item.Place is not null ? $"{item.RelativePath} · {item.Place}"
            : item.Kind == ChangeKind.Content ? $"{item.RelativePath} · line {item.Line}" : item.RelativePath;
        Where = string.IsNullOrEmpty(item.ContextDetail) ? item.ContextDisplay : $"{item.ContextDisplay} · {item.ContextDetail}";
        Risks = item.Risks;

        if (item.Kind == ChangeKind.Content)
        {
            // PDF text has no line numbers of its own, so none are shown.
            string Number(int n) => item.Place is null ? n.ToString() : "";
            Lines = scan.ContextLines(item, before: 2, after: 2).Select(l => l.MatchStart < 0
                ? new PreviewLine(Number(l.Number), l.Text, "", "", false)
                : Split(Number(l.Number), l.Text, l.MatchStart, l.MatchLength)).ToList();
        }
        else
        {
            Lines = [Split("", item.LineText, item.MatchStartInLine, item.Length)];
        }

        var matchLine = Lines.FirstOrDefault(l => l.IsMatchLine);
        LineBefore = matchLine?.Before ?? "";
        LineAfter = matchLine?.After ?? "";

        var symbolGroup = scan.SameSymbol(item);
        SymbolCount = item.SymbolKey is null ? 1 : symbolGroup.Count;
        SymbolName = item.SymbolKey is null ? "" : item.WordText;
        SimilarCount = scan.Similar(item).Count;
        FileCount = symbolGroup.Select(i => i.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    public ChangeItem Item { get; }

    public string Header { get; }

    public string Where { get; }

    public IReadOnlyList<RiskFinding> Risks { get; }

    public bool HasRisks => Risks.Count > 0;

    public IReadOnlyList<PreviewLine> Lines { get; }

    /// <summary>The match line's text around the match, for the "after" preview.</summary>
    public string LineBefore { get; }

    public string LineAfter { get; }

    public int SymbolCount { get; }

    public string SymbolName { get; }

    public int SimilarCount { get; }

    public int FileCount { get; }

    public bool HasSymbolGroup => SymbolCount > 1;

    public bool HasSimilar => SimilarCount > 1;

    public string SymbolScopeText => char.ToUpperInvariant(SymbolScopeInline[0]) + SymbolScopeInline[1..];

    /// <summary>For use mid-sentence: "all 3 occurrences of “X” (2 files)".</summary>
    public string SymbolScopeInline => FileCount > 1
        ? $"all {SymbolCount} occurrences of “{SymbolName}” ({FileCount} files)"
        : $"all {SymbolCount} occurrences of “{SymbolName}”";

    public string SimilarScopeText => $"All {SimilarCount} matches like this one (same text, same kind of place, same reasons)";

    private static PreviewLine Split(string number, string text, int start, int length)
    {
        start = Math.Clamp(start, 0, text.Length);
        length = Math.Clamp(length, 0, text.Length - start);
        return new PreviewLine(number, Tabs(text[..start]), text.Substring(start, length), Tabs(text[(start + length)..]), true);
    }

    private static string Tabs(string text) => text.Replace("\t", "    ");
}
