using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GigaReplace.ViewModels;

public enum DecisionAction
{
    Replace,
    Skip,
    Custom,
    ReplaceAllLikeThis,
    SkipAllLikeThis,
    Back,
    Stop,
}

public sealed record DecisionOutcome(DecisionAction Action, string? CustomText, bool ApplyToSymbol);

/// <summary>The "possible problem" dialog: one flagged change, asking what to do.</summary>
public partial class DecisionViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _applyToSymbol;

    [ObservableProperty]
    private bool _showCustom;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseCustomCommand))]
    private string _customText;

    public DecisionViewModel(ItemDetailsViewModel details, string progressText, bool canGoBack, bool isRun)
    {
        Details = details;
        ProgressText = progressText;
        CanGoBack = canGoBack;
        IsRun = isRun;
        ApplyToSymbol = details.HasSymbolGroup;
        _customText = details.Item.FinalText;
    }

    public ItemDetailsViewModel Details { get; }

    public string ProgressText { get; }

    public bool CanGoBack { get; }

    public bool IsRun { get; }

    public string StopText => IsRun ? "Stop the run" : "Close";

    public string StopHint => IsRun
        ? "Stops asking. Everything decided so far is kept; the rest waits on the review screen."
        : "Closes this walk-through. Undecided items stay on the review screen.";

    public DecisionOutcome? Outcome { get; private set; }

    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Replace() => Close(DecisionAction.Replace);

    [RelayCommand]
    private void Skip() => Close(DecisionAction.Skip);

    [RelayCommand]
    private void ToggleCustom() => ShowCustom = !ShowCustom;

    private bool HasCustomText => CustomText is not null;

    [RelayCommand(CanExecute = nameof(HasCustomText))]
    private void UseCustom() => Close(DecisionAction.Custom);

    [RelayCommand]
    private void ReplaceAllLikeThis() => Close(DecisionAction.ReplaceAllLikeThis);

    [RelayCommand]
    private void SkipAllLikeThis() => Close(DecisionAction.SkipAllLikeThis);

    [RelayCommand]
    private void Back() => Close(DecisionAction.Back);

    [RelayCommand]
    private void Stop() => Close(DecisionAction.Stop);

    private void Close(DecisionAction action)
    {
        Outcome = new DecisionOutcome(action, action == DecisionAction.Custom ? CustomText : null, ApplyToSymbol);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
