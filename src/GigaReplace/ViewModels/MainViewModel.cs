using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GigaReplace.Core;
using GigaReplace.Core.Apply;
using GigaReplace.Core.Planning;
using GigaReplace.Core.Profiles;
using GigaReplace.Services;

namespace GigaReplace.ViewModels;

/// <summary>Runs the flow: set up → scan → decide (review screen or prompts) → apply → build/undo.</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly JournalStore _journals = new();
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private int _selectedTab;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(ApplyCommand), nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Choose a solution or folder, add what to find and replace, then scan.";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _progressIsIndeterminate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string? _profilePath;

    public MainViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        Setup = new SetupViewModel(dialogs);
        Review = new ReviewViewModel(dialogs);
        Results = new ResultsViewModel(dialogs, _journals);

        Review.StateChanged += (_, _) =>
        {
            ApplyCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(ApplyHint));
        };
        Results.Undone += (_, _) => Review.MarkStale();
        Setup.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SetupViewModel.WriteMode))
            {
                OnPropertyChanged(nameof(ApplyText));
            }
            else if (e.PropertyName is nameof(SetupViewModel.ProfileName))
            {
                OnPropertyChanged(nameof(WindowTitle));
            }
        };

        if (ProfileStore.TryLoadLast() is { } last)
        {
            Setup.Load(last);
        }

        Results.RefreshJournals();
    }

    public SetupViewModel Setup { get; }

    public ReviewViewModel Review { get; }

    public ResultsViewModel Results { get; }

    public string WindowTitle => ProfilePath is null
        ? $"GigaReplace — {Setup.ProfileName}"
        : $"GigaReplace — {Setup.ProfileName} ({Path.GetFileName(ProfilePath)})";

    public string ApplyText => Setup.WriteMode == WriteMode.CopyToFolder ? "Create the renamed copy" : "Apply changes in place";

    public string ApplyHint =>
        Review.Scan is null ? "Scan first."
        : Review.IsStale ? "The files changed since this scan. Scan again to make more changes."
        : Review.PendingCount > 0 ? $"{Review.PendingCount:N0} flagged items still need a decision."
        : Review.ErrorCount > 0 ? "Fix the errors listed under Problems first."
        : Review.ChangeCount == 0 ? "Every match is set to Skip, so nothing would change."
        : Review.WarningCount > 0 ? "Possible problems remain; you'll be asked to confirm."
        : "Ready.";

    private bool CanScan => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (Setup.ValidateForScan() is { } problem)
        {
            _dialogs.Info("Can't scan yet", problem);
            return;
        }

        var profile = Setup.ToProfile();
        ProfileStore.SaveLast(profile);
        _cts = new CancellationTokenSource();
        IsBusy = true;
        StatusText = "Scanning…";
        ProgressIsIndeterminate = true;
        var progress = new Progress<ScanProgress>(p =>
        {
            StatusText = p.Total > 0 ? $"{p.Stage}… {p.Done:N0} of {p.Total:N0} files" : $"{p.Stage}…";
            ProgressIsIndeterminate = p.Total == 0;
            ProgressValue = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
        });

        ScanResult scan;
        try
        {
            scan = await Scanner.ScanAsync(profile, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled.";
            return;
        }
        catch (Exception ex) when (ex is ScanException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _dialogs.Error("Scan failed", ex.Message);
            StatusText = "Scan failed.";
            return;
        }
        finally
        {
            IsBusy = false;
            ProgressIsIndeterminate = false;
            _cts?.Dispose();
            _cts = null;
        }

        Review.Load(scan);
        StatusText = $"Scanned {scan.Tree.Files.Count:N0} files in {scan.Duration.TotalSeconds:0.0} s and found {scan.Items.Count:N0} matches.";
        if (scan.Items.Count == 0)
        {
            _dialogs.Info("Nothing to change", "None of the find texts appear in file contents, file names or folder names.");
            return;
        }

        SelectedTab = 1;
        if (profile.PromptMode != PromptMode.PromptDuringRun)
        {
            return;
        }

        if (!Review.Walk(scan.Items.Where(i => i.IsPending).ToList(), isRun: true))
        {
            StatusText = "Stopped. What you decided is kept; the rest is waiting on the review screen.";
            return;
        }

        var problems = Review.Problems.Select(p => $"{p.Title}: {p.Message}").ToList();
        if (Review.ErrorCount > 0)
        {
            _dialogs.Error("Some problems need fixing", "Fix these on the review screen before applying:", problems);
            return;
        }

        var summary = $"{Review.ChangeCount:N0} changes will be made; {scan.Items.Count - Review.ChangeCount:N0} matches stay as they are.";
        int choice = _dialogs.Choose(
            "Ready to apply",
            problems.Count == 0 ? summary : summary + "\n\nThe checks still see possible problems:",
            [ApplyText, "Review first", "Cancel"],
            problems.Count == 0 ? null : problems);
        if (choice == 0)
        {
            await ApplyCoreAsync(confirmed: true);
        }
    }

    private bool CanApply => !IsBusy && Review.Scan is not null && !Review.IsStale
        && Review.PendingCount == 0 && Review.ErrorCount == 0 && Review.ChangeCount > 0;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private Task ApplyAsync() => ApplyCoreAsync(confirmed: false);

    private async Task ApplyCoreAsync(bool confirmed)
    {
        var scan = Review.Scan;
        if (scan is null)
        {
            return;
        }

        Review.Validate();
        if (Review.PendingCount > 0 || Review.ErrorCount > 0)
        {
            _dialogs.Info("Not ready yet", ApplyHint);
            return;
        }

        ApplyPlan plan;
        try
        {
            plan = ApplyPlan.Build(scan);
        }
        catch (ApplyException ex)
        {
            _dialogs.Error("Can't apply", ex.Message, ex.Details);
            return;
        }

        if (plan.IsEmpty)
        {
            _dialogs.Info("Nothing to change", "Every match is set to Skip.");
            return;
        }

        var output = Setup.ToProfile();
        bool copy = output.WriteMode == WriteMode.CopyToFolder;
        var destination = output.DestinationPath;
        if (copy && string.IsNullOrWhiteSpace(destination))
        {
            destination = Setup.SuggestDestination();
            Setup.DestinationPath = destination;
        }

        if (!confirmed)
        {
            var what = $"{plan.ChangeCount:N0} changes: {plan.ContentWrites.Count:N0} files edited, {plan.FileRenames.Count:N0} files and {plan.FolderRenames.Count:N0} folders renamed";
            var message = copy
                ? $"Copy {scan.Tree.Files.Count:N0} files from\n{scan.RootPath}\nto\n{destination}\nand make {what} in the copy. The source folder isn't touched."
                : $"Change {scan.RootPath} in place with {what}.\n\nAn undo journal is kept, so the Results tab can put everything back.";
            var warnings = Review.Problems.Select(p => $"{p.Title}: {p.Message}").ToList();
            if (warnings.Count > 0)
            {
                message += "\n\nThe checks still see possible problems:";
            }

            if (_dialogs.Choose("Apply the changes?", message, [ApplyText, "Cancel"], warnings.Count > 0 ? warnings : null) != 0)
            {
                return;
            }
        }

        if (!copy && await GitStatus.HasUncommittedChangesAsync(scan.RootPath) == true
            && _dialogs.Choose(
                "Uncommitted git changes",
                "This folder is a git repository with uncommitted changes. The undo journal can put GigaReplace's changes back, but committing first makes the changes easy to review and revert with git too.",
                ["Apply anyway", "Cancel"]) != 0)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        ProgressIsIndeterminate = false;
        StatusText = copy ? "Copying…" : "Applying…";
        var progress = new Progress<ApplyProgress>(p =>
        {
            StatusText = $"{p.Stage}… {p.Done:N0} of {p.Total:N0}";
            ProgressValue = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
        });

        try
        {
            var result = await Task.Run(() => copy
                ? CopyApplier.Apply(plan, destination, output.CopyGitFolder, progress, token)
                : InPlaceApplier.Apply(plan, _journals, progress, token));
            Results.Show(result);
            if (!copy)
            {
                Review.MarkStale();
            }

            StatusText = result.Notes.Count == 0 ? result.Summary : result.Summary + " See the notes on the Results tab.";
            SelectedTab = 2;
        }
        catch (ApplyException ex)
        {
            _dialogs.Error("Couldn't apply the changes", ex.Message, ex.Details);
            StatusText = "Nothing was applied.";
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            var reason = ex is OperationCanceledException ? "Cancelled." : ex.Message;
            _dialogs.Error("Couldn't finish", copy ? $"{reason}\n\nA partial copy may be left in {destination}; delete it before trying again." : reason);
            StatusText = "Stopped before finishing.";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            Results.RefreshJournals();
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void NewProfile()
    {
        if (IsBusy)
        {
            return;
        }

        Setup.Load(new ReplaceProfile());
        ProfilePath = null;
        Review.Clear();
        SelectedTab = 0;
        StatusText = "New profile.";
    }

    [RelayCommand]
    private void OpenProfile()
    {
        if (IsBusy)
        {
            return;
        }

        var path = _dialogs.PickFile("Open a profile", ProfileStore.FileFilter, ProfilePath ?? ProfileStore.AppDataFolder);
        if (path is null)
        {
            return;
        }

        try
        {
            Setup.Load(ProfileStore.Load(path));
            ProfilePath = path;
            Review.Clear();
            SelectedTab = 0;
            StatusText = $"Opened {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            _dialogs.Error("Couldn't open the profile", ex.Message);
        }
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (ProfilePath is null)
        {
            SaveProfileAs();
        }
        else
        {
            SaveTo(ProfilePath);
        }
    }

    [RelayCommand]
    private void SaveProfileAs()
    {
        var name = string.Concat(Setup.ProfileName.Split(Path.GetInvalidFileNameChars())).Trim();
        var path = _dialogs.PickSaveFile(
            "Save the profile",
            ProfileStore.FileFilter,
            (name.Length == 0 ? "Untitled" : name) + ".grprofile.json",
            ProfilePath is null ? ProfileStore.AppDataFolder : Path.GetDirectoryName(ProfilePath));
        if (path is not null)
        {
            SaveTo(path);
        }
    }

    private void SaveTo(string path)
    {
        try
        {
            ProfileStore.Save(Setup.ToProfile(), path);
            ProfilePath = path;
            StatusText = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Error("Couldn't save the profile", ex.Message);
        }
    }

    public void OnClosing() => ProfileStore.SaveLast(Setup.ToProfile());
}
