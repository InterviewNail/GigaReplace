using System.ComponentModel;

namespace GigaReplace.Core;

/// <summary>One proposed change: a match inside a file, or part of a file or folder name.</summary>
public sealed class ChangeItem : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs[] DecisionDependents =
    [
        new(nameof(Decision)),
        new(nameof(CustomText)),
        new(nameof(FinalText)),
        new(nameof(WillChange)),
        new(nameof(IsPending)),
        new(nameof(WordFinalText)),
        new(nameof(WordPreviewText)),
        new(nameof(PreviewText)),
    ];

    private Decision _decision;
    private string? _customText;

    public int Id { get; internal set; }

    public required ChangeKind Kind { get; init; }

    public required ReplacementRule Rule { get; init; }

    /// <summary>The file whose text changes, or the file/folder being renamed (original path).</summary>
    public required string RelativePath { get; init; }

    /// <summary>Offset in the decoded file text, or in the file/folder name for renames.</summary>
    public required int Offset { get; init; }

    public required int Length { get; init; }

    public required string OriginalText { get; init; }

    public required string ProposedText { get; init; }

    /// <summary>1-based; 0 for renames.</summary>
    public int Line { get; init; }

    public int Column { get; init; }

    public MatchContext Context { get; init; }

    public string ContextDetail { get; init; } = "";

    /// <summary>Groups occurrences of one code name, which must all end up the same.</summary>
    public string? SymbolKey { get; init; }

    /// <summary>Offset of <see cref="WordText"/> in the same text as <see cref="Offset"/>.</summary>
    public int WordStart { get; init; }

    /// <summary>The whole word or name the match sits in.</summary>
    public string WordText { get; init; } = "";

    public string LineText { get; init; } = "";

    public int MatchStartInLine { get; init; }

    public IReadOnlyList<RiskFinding> Risks { get; init; } = [];

    /// <summary>Matches with the same rule, context, reasons and text count as "like this".</summary>
    public string SimilarityKey { get; init; } = "";

    public Decision DefaultDecision { get; init; }

    public Decision Decision
    {
        get => _decision;
        set
        {
            if (_decision == value)
            {
                return;
            }

            _decision = value;
            NotifyDecisionChanged();
        }
    }

    public string? CustomText
    {
        get => _customText;
        set
        {
            if (_customText == value)
            {
                return;
            }

            _customText = value;
            NotifyDecisionChanged();
        }
    }

    public string FinalText => Decision switch
    {
        Decision.Replace => ProposedText,
        Decision.Custom => CustomText ?? ProposedText,
        _ => OriginalText,
    };

    public bool WillChange =>
        (Decision is Decision.Replace or Decision.Custom) && !string.Equals(FinalText, OriginalText, StringComparison.Ordinal);

    public bool IsPending => Decision == Decision.Pending;

    public bool IsFlagged => Risks.Count > 0;

    public bool IsRename => Kind != ChangeKind.Content;

    public string WordProposedText => Splice(ProposedText);

    public string WordFinalText => Splice(FinalText);

    /// <summary>What the word becomes: the proposal while undecided, otherwise the decided text.</summary>
    public string WordPreviewText => IsPending ? WordProposedText : WordFinalText;

    /// <summary>What the match becomes: the proposal while undecided, otherwise the decided text.</summary>
    public string PreviewText => IsPending ? ProposedText : FinalText;

    public string RiskSummary => string.Join(", ", Risks.Select(r => r.Title).Distinct());

    public string ContextDisplay => ContextNames.Display(Context);

    public string KindDisplay => Kind switch
    {
        ChangeKind.Content => "Text",
        ChangeKind.FileRename => "File name",
        _ => "Folder name",
    };

    public string Location => Kind == ChangeKind.Content ? $"{RelativePath}:{Line}" : RelativePath;

    public string LineDisplay => Kind == ChangeKind.Content ? Line.ToString() : "";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Sets the decision and custom text together, raising change notifications once.</summary>
    public void SetDecision(Decision decision, string? customText = null)
    {
        var newCustom = decision == Decision.Custom ? customText : null;
        if (_decision == decision && _customText == newCustom)
        {
            return;
        }

        _decision = decision;
        _customText = newCustom;
        NotifyDecisionChanged();
    }

    private string Splice(string replacement)
    {
        int at = Offset - WordStart;
        if (WordText.Length == 0 || at < 0 || at + Length > WordText.Length)
        {
            return replacement;
        }

        return string.Concat(WordText.AsSpan(0, at), replacement, WordText.AsSpan(at + Length));
    }

    private void NotifyDecisionChanged()
    {
        var handler = PropertyChanged;
        if (handler is null)
        {
            return;
        }

        foreach (var args in DecisionDependents)
        {
            handler(this, args);
        }
    }

    public override string ToString() => $"#{Id} {Location} {OriginalText} → {FinalText} ({Decision})";
}
