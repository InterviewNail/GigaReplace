using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GigaReplace.Core;
using GigaReplace.Core.Planning;
using GigaReplace.Core.Profiles;
using GigaReplace.Services;

namespace GigaReplace.ViewModels;

public enum ItemFilter
{
    All,
    NeedsDecision,
    Flagged,
    WillChange,
    Kept,
    Renames,
    IdentityGuids,
}

public enum DecisionScope
{
    ThisOnly,
    SameName,
    Similar,
}

public sealed record FilterOption<T>(T Value, string Label);

public sealed record FixViewModel(string Label, IRelayCommand Command);

public sealed class ProblemViewModel
{
    public ProblemViewModel(PlanProblem problem, ReviewViewModel owner)
    {
        Problem = problem;
        Fixes = problem.Fixes.Select(f => new FixViewModel(f.Label, new RelayCommand(() => owner.ApplyFix(f)))).ToList();
        ShowCommand = new RelayCommand(() => owner.ShowOnly(problem.Items, problem.Title));
    }

    public PlanProblem Problem { get; }

    public string Title => Problem.Title;

    public string Message => Problem.Message;

    public bool IsError => Problem.IsError;

    public IReadOnlyList<FixViewModel> Fixes { get; }

    public IRelayCommand ShowCommand { get; }

    public string ShowText => Problem.Items.Count == 1 ? "Show the item" : $"Show the {Problem.Items.Count} items";
}

/// <summary>The review screen: every proposed change, its reasons, the user's decisions and the plan's problems.</summary>
public partial class ReviewViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _validateTimer;
    private readonly DispatcherTimer _searchTimer;
    private List<ChangeItem> _items = [];
    private HashSet<ChangeItem>? _only;
    private ListCollectionView? _view;
    private bool _suppressRefresh;

    [ObservableProperty]
    private ICollectionView? _itemsView;

    [ObservableProperty]
    private ChangeItem? _selectedItem;

    [ObservableProperty]
    private ItemDetailsViewModel? _details;

    [ObservableProperty]
    private ItemFilter _filter;

    [ObservableProperty]
    private RiskCategory? _category;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private DecisionScope _scope;

    [ObservableProperty]
    private string _customText = "";

    [ObservableProperty]
    private bool _isStale;

    [ObservableProperty]
    private string? _onlyFilterText;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private int _flaggedCount;

    [ObservableProperty]
    private int _changeCount;

    [ObservableProperty]
    private int _fileCount;

    [ObservableProperty]
    private int _shownCount;

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private int _warningCount;

    public ReviewViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        _validateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _validateTimer.Tick += (_, _) =>
        {
            _validateTimer.Stop();
            Validate();
        };
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RefreshView();
        };
    }

    /// <summary>Raised when anything that affects applying changes (counts, problems, staleness).</summary>
    public event EventHandler? StateChanged;

    public ScanResult? Scan { get; private set; }

    public ObservableCollection<ProblemViewModel> Problems { get; } = [];

    public ObservableCollection<string> Notes { get; } = [];

    public IReadOnlyList<FilterOption<ItemFilter>> FilterOptions { get; } =
    [
        new(ItemFilter.All, "Everything"),
        new(ItemFilter.NeedsDecision, "Needs a decision"),
        new(ItemFilter.Flagged, "Flagged for any reason"),
        new(ItemFilter.WillChange, "Will change"),
        new(ItemFilter.Kept, "Skipped"),
        new(ItemFilter.Renames, "File and folder names"),
        new(ItemFilter.IdentityGuids, "Identity GUIDs"),
    ];

    public IReadOnlyList<FilterOption<RiskCategory?>> CategoryOptions { get; } =
    [
        new(null, "Any reason"),
        new(RiskCategory.BuildBreaker, RiskCatalog.Title(RiskCategory.BuildBreaker)),
        new(RiskCategory.PersistedData, RiskCatalog.Title(RiskCategory.PersistedData)),
        new(RiskCategory.AppIdentity, RiskCatalog.Title(RiskCategory.AppIdentity)),
        new(RiskCategory.WatchList, RiskCatalog.Title(RiskCategory.WatchList)),
    ];

    public bool HasScan => Scan is not null;

    public bool HasProblems => Problems.Count > 0;

    public string TabHeader => PendingCount > 0 ? $"2  Review  ({PendingCount:N0} to decide)" : "2  Review";

    public string SummaryText => Scan is null
        ? "Nothing scanned yet."
        : $"{TotalCount:N0} matches in {FileCount:N0} files and names · {ChangeCount:N0} will change";

    public string DecisionText => PendingCount == 0 ? "Every flagged item has a decision." : $"{PendingCount:N0} flagged items need a decision.";

    public string ProblemsHeader => ErrorCount > 0
        ? $"Problems: {ErrorCount} to fix, {WarningCount} to check"
        : WarningCount > 0 ? $"Problems: {WarningCount} to check" : "No problems found";

    public string ShownText => ShownCount == TotalCount ? $"{TotalCount:N0} shown" : $"{ShownCount:N0} of {TotalCount:N0} shown";

    public void Load(ScanResult scan)
    {
        Scan = scan;
        IsStale = false;
        _items = scan.Items.ToList();
        _only = null;
        OnlyFilterText = null;
        _view = new ListCollectionView(_items) { Filter = Matches };
        _suppressRefresh = true;
        Filter = ItemFilter.All;
        Category = null;
        SearchText = "";
        _suppressRefresh = false;
        _searchTimer.Stop();
        ItemsView = _view;
        ShownCount = _view.Count;

        Notes.Clear();
        foreach (var note in scan.Warnings.Take(200))
        {
            Notes.Add(note);
        }

        OnPropertyChanged(nameof(HasScan));
        Recount();
        Validate();
        SelectedItem = _items.FirstOrDefault(i => i.IsPending) ?? _items.FirstOrDefault();
    }

    public void Clear()
    {
        Scan = null;
        _items = [];
        _view = null;
        ItemsView = null;
        SelectedItem = null;
        Problems.Clear();
        Notes.Clear();
        IsStale = false;
        OnPropertyChanged(nameof(HasScan));
        OnPropertyChanged(nameof(HasProblems));
        Recount();
    }

    /// <summary>After an in-place run the scan no longer matches the disk.</summary>
    public void MarkStale()
    {
        IsStale = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedItemChanged(ChangeItem? value)
    {
        Details = value is null || Scan is null ? null : new ItemDetailsViewModel(Scan, value);
        Scope = Details is { HasSymbolGroup: true } ? DecisionScope.SameName : DecisionScope.ThisOnly;
        CustomText = value?.FinalText ?? "";
    }

    partial void OnFilterChanged(ItemFilter value) => RefreshView();

    partial void OnCategoryChanged(RiskCategory? value) => RefreshView();

    partial void OnSearchTextChanged(string value)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    partial void OnShownCountChanged(int value) => OnPropertyChanged(nameof(ShownText));

    partial void OnIsStaleChanged(bool value) => StateChanged?.Invoke(this, EventArgs.Empty);

    private bool Matches(object obj)
    {
        var item = (ChangeItem)obj;
        if (_only is not null && !_only.Contains(item))
        {
            return false;
        }

        bool keep = Filter switch
        {
            ItemFilter.NeedsDecision => item.IsPending,
            ItemFilter.Flagged => item.IsFlagged,
            ItemFilter.WillChange => item.WillChange,
            ItemFilter.Kept => item.Decision == Decision.Skip,
            ItemFilter.Renames => item.IsRename,
            ItemFilter.IdentityGuids => item.Context == MatchContext.IdentityGuid,
            _ => true,
        };
        if (!keep || (Category is { } category && !item.Risks.Any(r => r.Category == category)))
        {
            return false;
        }

        var search = SearchText.Trim();
        return search.Length == 0
            || item.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.WordText.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.WordFinalText.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.ContextDetail.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.RiskSummary.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.LineText.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshView()
    {
        if (_view is null || _suppressRefresh)
        {
            return;
        }

        var selected = SelectedItem;
        int index = selected is null ? -1 : _view.IndexOf(selected);
        _view.Refresh();
        ShownCount = _view.Count;
        if (selected is not null && _view.IndexOf(selected) >= 0)
        {
            SelectedItem = selected;
        }
        else if (_view.Count > 0)
        {
            SelectedItem = (ChangeItem)_view.GetItemAt(Math.Clamp(index, 0, _view.Count - 1));
        }
        else
        {
            SelectedItem = null;
        }
    }

    /// <summary>Gives <paramref name="item"/> (and, depending on scope, its group) a decision.</summary>
    public void Decide(ChangeItem item, Decision decision, string? customText, DecisionScope scope)
    {
        if (Scan is null)
        {
            return;
        }

        IEnumerable<ChangeItem> targets = scope switch
        {
            DecisionScope.SameName => Scan.SameSymbol(item),
            DecisionScope.Similar => Scan.Similar(item),
            _ => [item],
        };

        if (decision == Decision.Custom && customText == item.ProposedText)
        {
            decision = Decision.Replace;
        }

        foreach (var target in targets.Append(item).Distinct())
        {
            target.SetDecision(decision, customText);
        }

        AfterDecisions();
    }

    public void ApplyFix(QuickFix fix)
    {
        foreach (var item in fix.Items)
        {
            item.SetDecision(fix.Decision);
        }

        AfterDecisions();
        Validate();
    }

    public void ShowOnly(IReadOnlyList<ChangeItem> items, string title)
    {
        _only = items.ToHashSet();
        OnlyFilterText = items.Count == 1 ? $"Showing the item of “{title}”" : $"Showing the {items.Count} items of “{title}”";
        if (Filter != ItemFilter.All)
        {
            Filter = ItemFilter.All;
        }
        else
        {
            RefreshView();
        }
    }

    [RelayCommand]
    private void ClearOnly()
    {
        _only = null;
        OnlyFilterText = null;
        RefreshView();
    }

    private void AfterDecisions()
    {
        Recount();
        _validateTimer.Stop();
        _validateTimer.Start();
        if (Filter is ItemFilter.NeedsDecision or ItemFilter.WillChange or ItemFilter.Kept)
        {
            RefreshView();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Validate()
    {
        _validateTimer.Stop();
        Problems.Clear();
        if (Scan is not null)
        {
            foreach (var problem in PlanValidator.Validate(Scan))
            {
                Problems.Add(new ProblemViewModel(problem, this));
            }
        }

        ErrorCount = Problems.Count(p => p.IsError);
        WarningCount = Problems.Count - ErrorCount;
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(ProblemsHeader));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Recount()
    {
        TotalCount = _items.Count;
        PendingCount = _items.Count(i => i.IsPending);
        FlaggedCount = _items.Count(i => i.IsFlagged);
        ChangeCount = _items.Count(i => i.WillChange);
        FileCount = _items.Select(i => i.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        OnPropertyChanged(nameof(TabHeader));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(DecisionText));
        OnPropertyChanged(nameof(ShownText));
    }

    [RelayCommand]
    private void ReplaceSelected()
    {
        if (SelectedItem is { } item)
        {
            Decide(item, Decision.Replace, null, Scope);
        }
    }

    [RelayCommand]
    private void SkipSelected()
    {
        if (SelectedItem is { } item)
        {
            Decide(item, Decision.Skip, null, Scope);
        }
    }

    [RelayCommand]
    private void UseCustomText()
    {
        if (SelectedItem is { } item)
        {
            Decide(item, Decision.Custom, CustomText, Scope);
        }
    }

    [RelayCommand]
    private void ResetSelected()
    {
        if (SelectedItem is not { } item || Scan is null)
        {
            return;
        }

        IEnumerable<ChangeItem> targets = Scope switch
        {
            DecisionScope.SameName => Scan.SameSymbol(item),
            DecisionScope.Similar => Scan.Similar(item),
            _ => [item],
        };
        foreach (var target in targets.Append(item).Distinct())
        {
            target.SetDecision(target.DefaultDecision);
        }

        AfterDecisions();
    }

    [RelayCommand]
    private void SetShown(string decision)
    {
        if (_view is null || _view.Count == 0)
        {
            return;
        }

        var shown = _view.Cast<ChangeItem>().ToList();
        bool skip = decision == "Skip";
        var verb = skip ? "Skip" : "Replace";
        if (_dialogs.Choose($"{verb} all shown items?", $"Set all {shown.Count:N0} items in the list to “{verb}”?", [skip ? "Skip them all" : "Replace them all", "Cancel"]) != 0)
        {
            return;
        }

        foreach (var item in shown)
        {
            item.SetDecision(skip ? Decision.Skip : Decision.Replace);
        }

        AfterDecisions();
    }

    [RelayCommand]
    private void StepThrough()
    {
        var queue = (_view?.Cast<ChangeItem>() ?? _items).Where(i => i.IsPending).ToList();
        if (queue.Count == 0)
        {
            queue = _items.Where(i => i.IsPending).ToList();
        }

        if (queue.Count == 0)
        {
            _dialogs.Info("Nothing to decide", "Every flagged item already has a decision.");
            return;
        }

        Walk(queue, isRun: false);
    }

    [RelayCommand]
    private void DecideInDialog(ChangeItem? item)
    {
        if (item is not null)
        {
            Walk([item], isRun: false, includeDecided: true);
        }
    }

    [RelayCommand]
    private void ExportCsv()
    {
        if (Scan is null)
        {
            return;
        }

        var path = _dialogs.PickSaveFile("Export the review as CSV", "CSV (*.csv)|*.csv", "GigaReplace review.csv", Scan.RootPath);
        if (path is null)
        {
            return;
        }

        try
        {
            CsvReport.Write(Scan, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Error("Couldn't export", ex.Message);
        }
    }

    /// <summary>
    /// Asks about each item in turn with the decision dialog. Returns false if the user stopped.
    /// Items decided along the way (by "same name" or "like this" choices) are skipped.
    /// </summary>
    public bool Walk(IReadOnlyList<ChangeItem> queue, bool isRun, bool includeDecided = false)
    {
        if (Scan is null)
        {
            return true;
        }

        var history = new List<int>();
        ChangeItem? revisit = null;
        int i = 0;
        try
        {
            while (i < queue.Count)
            {
                var item = queue[i];
                if (!includeDecided && !item.IsPending && !ReferenceEquals(item, revisit))
                {
                    i++;
                    continue;
                }

                revisit = null;
                int left = queue.Skip(i).Count(q => q.IsPending);
                string progress = isRun
                    ? $"Possible problem · {left:N0} left to decide (of {TotalCount:N0} matches)"
                    : queue.Count == 1 ? "Decide this change" : $"Possible problem · {left:N0} left to decide";
                SelectedItem = item;
                var model = new DecisionViewModel(new ItemDetailsViewModel(Scan, item), progress, history.Count > 0, isRun);
                var outcome = _dialogs.ShowDecision(model);
                var scope = outcome.ApplyToSymbol ? DecisionScope.SameName : DecisionScope.ThisOnly;

                switch (outcome.Action)
                {
                    case DecisionAction.Stop:
                        return false;
                    case DecisionAction.Back:
                        if (history.Count > 0)
                        {
                            i = history[^1];
                            history.RemoveAt(history.Count - 1);
                            revisit = queue[i];
                        }

                        continue;
                    case DecisionAction.Replace:
                        Decide(item, Decision.Replace, null, scope);
                        break;
                    case DecisionAction.Skip:
                        Decide(item, Decision.Skip, null, scope);
                        break;
                    case DecisionAction.Custom:
                        Decide(item, Decision.Custom, outcome.CustomText, scope);
                        break;
                    case DecisionAction.ReplaceAllLikeThis:
                        Decide(item, Decision.Replace, null, DecisionScope.Similar);
                        break;
                    case DecisionAction.SkipAllLikeThis:
                        Decide(item, Decision.Skip, null, DecisionScope.Similar);
                        break;
                }

                history.Add(i);
                i++;
            }

            return true;
        }
        finally
        {
            Validate();
        }
    }
}
