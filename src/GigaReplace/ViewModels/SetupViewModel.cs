using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GigaReplace.Core;
using GigaReplace.Core.Text;
using GigaReplace.Services;

namespace GigaReplace.ViewModels;

public partial class RuleViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    private string _find = "";

    [ObservableProperty]
    private string _replace = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPreserveCase))]
    private bool _matchCase;

    [ObservableProperty]
    private bool _preserveCase = true;

    public Guid Id { get; init; } = Guid.NewGuid();

    public bool CanPreserveCase => !MatchCase;

    public static RuleViewModel From(ReplacementRule rule) => new()
    {
        Id = rule.Id,
        Enabled = rule.Enabled,
        Find = rule.Find,
        Replace = rule.Replace,
        MatchCase = rule.MatchCase,
        PreserveCase = rule.PreserveCase,
    };

    public ReplacementRule ToRule() => new()
    {
        Id = Id,
        Enabled = Enabled,
        Find = Find,
        Replace = Replace,
        MatchCase = MatchCase,
        PreserveCase = PreserveCase,
    };
}

/// <summary>The editable profile: source, rules, scope, risk checks and output.</summary>
public partial class SetupViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    private string _profileName = "Untitled";

    [ObservableProperty]
    private string _sourcePath = "";

    [ObservableProperty]
    private bool _replaceInContents = true;

    [ObservableProperty]
    private bool _renameFiles = true;

    [ObservableProperty]
    private bool _renameFolders = true;

    [ObservableProperty]
    private string _excludePatterns = string.Join(Environment.NewLine, ReplaceProfile.DefaultExcludePatterns);

    [ObservableProperty]
    private bool _respectGitIgnore = true;

    [ObservableProperty]
    private int _maxFileSizeMb = 16;

    [ObservableProperty]
    private bool _checkBuildBreakers = true;

    [ObservableProperty]
    private bool _checkPersistedData = true;

    [ObservableProperty]
    private bool _checkAppIdentity = true;

    [ObservableProperty]
    private bool _checkWatchList = true;

    [ObservableProperty]
    private string _watchList = "";

    [ObservableProperty]
    private bool _regenerateIdentityGuids;

    [ObservableProperty]
    private WriteMode _writeMode = WriteMode.CopyToFolder;

    [ObservableProperty]
    private string _destinationPath = "";

    [ObservableProperty]
    private bool _copyGitFolder;

    [ObservableProperty]
    private PromptMode _promptMode = PromptMode.ReviewFirst;

    public SetupViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        Rules.Add(new RuleViewModel());
    }

    public ObservableCollection<RuleViewModel> Rules { get; } = [];

    public void Load(ReplaceProfile profile)
    {
        ProfileName = profile.Name;
        SourcePath = profile.SourcePath;
        Rules.Clear();
        foreach (var rule in profile.Rules)
        {
            Rules.Add(RuleViewModel.From(rule));
        }

        if (Rules.Count == 0)
        {
            Rules.Add(new RuleViewModel());
        }

        ReplaceInContents = profile.ReplaceInContents;
        RenameFiles = profile.RenameFiles;
        RenameFolders = profile.RenameFolders;
        ExcludePatterns = string.Join(Environment.NewLine, profile.ExcludePatterns);
        RespectGitIgnore = profile.RespectGitIgnore;
        MaxFileSizeMb = profile.MaxFileSizeMb;
        CheckBuildBreakers = profile.RiskChecks.BuildBreakers;
        CheckPersistedData = profile.RiskChecks.PersistedData;
        CheckAppIdentity = profile.RiskChecks.AppIdentity;
        CheckWatchList = profile.RiskChecks.WatchList;
        WatchList = string.Join(Environment.NewLine, profile.WatchList);
        RegenerateIdentityGuids = profile.RegenerateIdentityGuids;
        WriteMode = profile.WriteMode;
        DestinationPath = profile.DestinationPath;
        CopyGitFolder = profile.CopyGitFolder;
        PromptMode = profile.PromptMode;
    }

    public ReplaceProfile ToProfile() => new()
    {
        Name = string.IsNullOrWhiteSpace(ProfileName) ? "Untitled" : ProfileName.Trim(),
        SourcePath = SourcePath.Trim().Trim('"'),
        Rules = Rules.Select(r => r.ToRule()).ToList(),
        ReplaceInContents = ReplaceInContents,
        RenameFiles = RenameFiles,
        RenameFolders = RenameFolders,
        ExcludePatterns = Lines(ExcludePatterns),
        RespectGitIgnore = RespectGitIgnore,
        MaxFileSizeMb = Math.Clamp(MaxFileSizeMb, 1, 1024),
        RiskChecks = new RiskCheckSettings
        {
            BuildBreakers = CheckBuildBreakers,
            PersistedData = CheckPersistedData,
            AppIdentity = CheckAppIdentity,
            WatchList = CheckWatchList,
        },
        WatchList = Lines(WatchList),
        RegenerateIdentityGuids = RegenerateIdentityGuids,
        WriteMode = WriteMode,
        DestinationPath = DestinationPath.Trim().Trim('"'),
        CopyGitFolder = CopyGitFolder,
        PromptMode = PromptMode,
    };

    /// <summary>A problem that stops a scan, or null.</summary>
    public string? ValidateForScan()
    {
        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            return "Choose the solution file or folder to process.";
        }

        if (!Rules.Any(r => r.Enabled && r.Find.Length > 0) && !RegenerateIdentityGuids)
        {
            return "Add at least one find → replace pair.";
        }

        var clash = Rules.Where(r => r.Enabled && r.Find.Length > 0).GroupBy(r => (r.Find, r.MatchCase)).FirstOrDefault(g => g.Count() > 1);
        return clash is null ? null : $"“{clash.Key.Find}” is listed more than once; only the first one would ever match.";
    }

    /// <summary>A sensible copy destination next to the source: the folder name with the rules applied.</summary>
    public string SuggestDestination()
    {
        try
        {
            var root = File.Exists(SourcePath) ? Path.GetDirectoryName(Path.GetFullPath(SourcePath))! : Path.GetFullPath(SourcePath.Trim().Trim('"'));
            var parent = Path.GetDirectoryName(root.TrimEnd('\\'));
            if (parent is null)
            {
                return "";
            }

            var name = Path.GetFileName(root.TrimEnd('\\'));
            var rules = Rules.Select(r => r.ToRule()).ToList();
            var matches = RuleMatcher.FindAll(name, rules);
            var renamed = TextEdits.Apply(name, matches.Select(m => new TextEdit(m.Start, m.Length, CasePreserver.Propose(m.Rule, name.Substring(m.Start, m.Length)).Text)));
            var candidate = Path.Combine(parent, renamed == name ? name + " (renamed)" : renamed);
            for (int n = 2; Directory.Exists(candidate) && Directory.EnumerateFileSystemEntries(candidate).Any(); n++)
            {
                candidate = Path.Combine(parent, $"{(renamed == name ? name + " (renamed)" : renamed)} {n}");
            }

            return candidate;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return "";
        }
    }

    [RelayCommand]
    private void AddRule() => Rules.Add(new RuleViewModel());

    [RelayCommand]
    private void RemoveRule(RuleViewModel? rule)
    {
        if (rule is null)
        {
            return;
        }

        Rules.Remove(rule);
        if (Rules.Count == 0)
        {
            Rules.Add(new RuleViewModel());
        }
    }

    [RelayCommand]
    private void MoveRuleUp(RuleViewModel? rule) => MoveRule(rule, -1);

    [RelayCommand]
    private void MoveRuleDown(RuleViewModel? rule) => MoveRule(rule, +1);

    private void MoveRule(RuleViewModel? rule, int delta)
    {
        int index = rule is null ? -1 : Rules.IndexOf(rule);
        int target = index + delta;
        if (index >= 0 && target >= 0 && target < Rules.Count)
        {
            Rules.Move(index, target);
        }
    }

    [RelayCommand]
    private void BrowseSourceFolder()
    {
        var folder = _dialogs.PickFolder("Choose the folder to process", ExistingFolder(SourcePath));
        if (folder is not null)
        {
            SourcePath = folder;
            OfferDestination();
        }
    }

    [RelayCommand]
    private void BrowseSolution()
    {
        var file = _dialogs.PickFile("Choose a solution", "Solutions (*.sln;*.slnx)|*.sln;*.slnx|Projects (*.csproj;*.vbproj;*.fsproj)|*.csproj;*.vbproj;*.fsproj", SourcePath);
        if (file is not null)
        {
            SourcePath = file;
            OfferDestination();
        }
    }

    [RelayCommand]
    private void BrowseDestination()
    {
        var folder = _dialogs.PickFolder("Choose an empty folder for the renamed copy", ExistingFolder(DestinationPath) ?? ExistingFolder(SourcePath));
        if (folder is not null)
        {
            DestinationPath = folder;
        }
    }

    [RelayCommand]
    private void SuggestDestinationPath() => DestinationPath = SuggestDestination();

    [RelayCommand]
    private void ResetExclusions() => ExcludePatterns = string.Join(Environment.NewLine, ReplaceProfile.DefaultExcludePatterns);

    private void OfferDestination()
    {
        if (string.IsNullOrWhiteSpace(DestinationPath))
        {
            DestinationPath = SuggestDestination();
        }
    }

    private static string? ExistingFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        path = path.Trim().Trim('"');
        if (Directory.Exists(path))
        {
            return path;
        }

        var parent = Path.GetDirectoryName(path);
        return parent is not null && Directory.Exists(parent) ? parent : null;
    }

    private static List<string> Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
