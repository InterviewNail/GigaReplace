using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GigaReplace.Core;
using GigaReplace.Core.Apply;
using GigaReplace.Services;

namespace GigaReplace.ViewModels;

public sealed class JournalItemViewModel(UndoJournal journal)
{
    public UndoJournal Journal { get; } = journal;

    public string Folder => Journal.SourceRoot;

    public string When => Journal.CreatedUtc.ToLocalTime().ToString("g");

    public string Summary => Journal.Summary;

    public bool CanUndo => Journal.CanUndo;

    public string State => Journal.State switch
    {
        JournalState.Applied => "Applied",
        JournalState.Undone => "Undone",
        JournalState.RolledBack => "Rolled back, nothing changed",
        JournalState.PartiallyUndone => "Partly undone",
        JournalState.Failed => "Failed part-way",
        JournalState.InProgress => "Interrupted",
        _ => "",
    };
}

/// <summary>What happened: the applied result, the optional build check, and undo of in-place runs.</summary>
public partial class ResultsViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly JournalStore _journals;
    private readonly ConcurrentQueue<string> _pendingLog = new();
    private readonly DispatcherTimer _logTimer;
    private CancellationTokenSource? _buildCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(ResultSummary), nameof(CanUndoResult), nameof(ResultIsCopy), nameof(ResultNotes))]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand), nameof(OpenOutputFolderCommand), nameof(UndoResultCommand))]
    private ApplyResult? _result;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand), nameof(CancelBuildCommand))]
    private bool _isBuilding;

    [ObservableProperty]
    private string _buildSummary = "";

    [ObservableProperty]
    private bool? _buildSucceeded;

    public ResultsViewModel(IDialogService dialogs, JournalStore journals)
    {
        _dialogs = dialogs;
        _journals = journals;
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _logTimer.Tick += (_, _) => FlushLog();
    }

    public event EventHandler<string>? LogAppended;

    public event EventHandler? LogCleared;

    public event EventHandler? Undone;

    public ObservableCollection<BuildDiagnostic> BuildErrors { get; } = [];

    public ObservableCollection<JournalItemViewModel> Journals { get; } = [];

    public bool HasResult => Result is not null;

    public bool ResultIsCopy => Result?.Mode == WriteMode.CopyToFolder;

    public string ResultSummary => Result?.Summary ?? "";

    public IReadOnlyList<string> ResultNotes => Result?.Notes ?? [];

    public bool CanUndoResult => Result?.Journal is { CanUndo: true };

    public void Show(ApplyResult result)
    {
        Result = result;
        BuildSummary = "";
        BuildSucceeded = null;
        BuildErrors.Clear();
        LogCleared?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshJournals()
    {
        Journals.Clear();
        foreach (var journal in _journals.List().Take(25))
        {
            Journals.Add(new JournalItemViewModel(journal));
        }

        OnPropertyChanged(nameof(CanUndoResult));
        UndoResultCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasResult))]
    private void OpenOutputFolder()
    {
        if (Result is not null && Directory.Exists(Result.OutputRoot))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Result.OutputRoot}\"") { UseShellExecute = true });
        }
    }

    private bool CanBuild => Result is not null && !IsBuilding;

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task BuildAsync()
    {
        var target = DotnetBuildRunner.FindBuildTarget(Result!.OutputRoot, Result.BuildTarget);
        if (target is null)
        {
            _dialogs.Info("Nothing to build", $"There's no solution or project file at the top of {Result.OutputRoot}.");
            return;
        }

        LogCleared?.Invoke(this, EventArgs.Empty);
        BuildErrors.Clear();
        BuildSucceeded = null;
        BuildSummary = $"Building {Path.GetFileName(target)}…";
        IsBuilding = true;
        _buildCts = new CancellationTokenSource();
        _logTimer.Start();
        try
        {
            var outcome = await DotnetBuildRunner.BuildAsync(target, line => _pendingLog.Enqueue(line), _buildCts.Token);
            BuildSucceeded = outcome.Succeeded;
            BuildSummary = outcome.Succeeded
                ? $"Build succeeded in {outcome.Duration.TotalSeconds:0} s ({Plural(outcome.WarningCount, "warning")})."
                : $"Build failed: {Plural(outcome.ErrorCount, "error")}, {Plural(outcome.WarningCount, "warning")}. Errors that also happen in the original solution aren't caused by the rename.";
            foreach (var diagnostic in outcome.Diagnostics.Where(d => d.IsError).Take(500))
            {
                BuildErrors.Add(diagnostic);
            }
        }
        catch (OperationCanceledException)
        {
            BuildSummary = "Build cancelled.";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            BuildSummary = "Couldn't run dotnet: " + ex.Message;
            BuildSucceeded = false;
        }
        finally
        {
            IsBuilding = false;
            _logTimer.Stop();
            FlushLog();
            _buildCts.Dispose();
            _buildCts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsBuilding))]
    private void CancelBuild() => _buildCts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanUndoResult))]
    private Task UndoResultAsync() =>
        Result?.Journal is { } journal ? UndoAsync(new JournalItemViewModel(journal)) : Task.CompletedTask;

    [RelayCommand]
    private async Task UndoAsync(JournalItemViewModel? item)
    {
        if (item is null || !item.Journal.CanUndo)
        {
            return;
        }

        var journal = item.Journal;
        var check = await Task.Run(() => UndoService.Check(journal));
        var message = $"Put {journal.SourceRoot} back the way it was before the run of {item.When}?\n\n{journal.Summary}.";
        bool overwrite = false;
        if (check.ChangedSinceApply.Count > 0)
        {
            int choice = _dialogs.Choose(
                "Undo the run?",
                message + "\n\nThese files were edited after the run. Undo can leave them as they are (keeping your edits), or put the originals back anyway:",
                ["Undo, keep my later edits", "Undo, restore the originals", "Cancel"],
                check.ChangedSinceApply);
            if (choice is < 0 or 2)
            {
                return;
            }

            overwrite = choice == 1;
        }
        else if (_dialogs.Choose("Undo the run?", message, ["Undo", "Cancel"], check.Missing.Count > 0 ? check.Missing.Select(m => "Can't find: " + m) : null) != 0)
        {
            return;
        }

        var outcome = await Task.Run(() => UndoService.Undo(journal, overwrite));
        if (Result?.Journal is { } current && current.Id == journal.Id && !ReferenceEquals(current, journal))
        {
            // The list holds its own copy loaded from disk; keep the result's copy in step.
            current.State = journal.State;
        }

        RefreshJournals();
        if (outcome.Success)
        {
            _dialogs.Info("Undone", $"Restored {Plural(outcome.Restored, "item")}. The folder is back to how it was before the run.");
        }
        else
        {
            _dialogs.Error("Undo finished with problems", $"Restored {Plural(outcome.Restored, "item")}, but these couldn't be:", outcome.Errors);
        }

        Undone?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenJournalFolder() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_journals.BaseFolder}\"") { UseShellExecute = true });

    private void FlushLog()
    {
        if (_pendingLog.IsEmpty)
        {
            return;
        }

        var sb = new StringBuilder();
        while (_pendingLog.TryDequeue(out var line))
        {
            sb.AppendLine(line);
        }

        LogAppended?.Invoke(this, sb.ToString());
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";
}
