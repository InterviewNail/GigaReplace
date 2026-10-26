using System.Text.Json;
using System.Text.Json.Serialization;

namespace GigaReplace.Core;

/// <summary>Everything that configures one run. Saved and loaded as JSON.</summary>
public sealed class ReplaceProfile
{
    public static IReadOnlyList<string> DefaultExcludePatterns { get; } =
    [
        ".vs/",
        ".idea/",
        "bin/",
        "obj/",
        "node_modules/",
        "packages/",
        "TestResults/",
        "*.user",
        "*.suo",
    ];

    public int Version { get; set; } = 1;

    public string Name { get; set; } = "Untitled";

    /// <summary>A folder, or a .sln/.slnx file whose folder is processed.</summary>
    public string SourcePath { get; set; } = "";

    public List<ReplacementRule> Rules { get; set; } = [];

    public bool ReplaceInContents { get; set; } = true;

    public bool RenameFiles { get; set; } = true;

    public bool RenameFolders { get; set; } = true;

    /// <summary>Look for matches inside PDF files (text and document properties).</summary>
    public bool SearchPdfs { get; set; } = true;

    /// <summary>Also change text on PDF pages where that can be done safely; otherwise it's only listed.</summary>
    public bool EditPdfText { get; set; } = true;

    /// <summary>Extra exclusions in .gitignore syntax, relative to the source folder.</summary>
    public List<string> ExcludePatterns { get; set; } = [.. DefaultExcludePatterns];

    public bool RespectGitIgnore { get; set; } = true;

    /// <summary>Larger files are never edited, though they can still be renamed and copied.</summary>
    public int MaxFileSizeMb { get; set; } = 16;

    public RiskCheckSettings RiskChecks { get; set; } = new();

    /// <summary>Terms (or /regex/ lines) that flag any match on a line containing them.</summary>
    public List<string> WatchList { get; set; } = [];

    /// <summary>Give the result new identity GUIDs (UpgradeCode, ProjectGuid, …) so both apps can coexist.</summary>
    public bool RegenerateIdentityGuids { get; set; }

    public WriteMode WriteMode { get; set; } = WriteMode.CopyToFolder;

    public string DestinationPath { get; set; } = "";

    /// <summary>Copy mode only: also copy the .git folder, untouched.</summary>
    public bool CopyGitFolder { get; set; }

    public PromptMode PromptMode { get; set; } = PromptMode.ReviewFirst;

    /// <summary>Reuse decisions from earlier runs on the same folder, and save this run's for next time.</summary>
    public bool RememberDecisions { get; set; } = true;

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public ReplaceProfile Clone()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        return JsonSerializer.Deserialize<ReplaceProfile>(json, JsonOptions)!;
    }
}

/// <summary>Which kinds of risky change are flagged for a decision. Each can be switched off.</summary>
public sealed class RiskCheckSettings
{
    public bool BuildBreakers { get; set; } = true;

    public bool PersistedData { get; set; } = true;

    public bool AppIdentity { get; set; } = true;

    public bool WatchList { get; set; } = true;

    public bool IsEnabled(RiskCategory category) => category switch
    {
        RiskCategory.BuildBreaker => BuildBreakers,
        RiskCategory.PersistedData => PersistedData,
        RiskCategory.AppIdentity => AppIdentity,
        RiskCategory.WatchList => WatchList,
        _ => true,
    };
}
