using System.Text.Json.Serialization;

namespace GigaReplace.Core;

/// <summary>One find → replace pair. Rules apply in list order; text claimed by an earlier rule isn't matched again.</summary>
public sealed class ReplacementRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public bool Enabled { get; set; } = true;

    public string Find { get; set; } = "";

    public string Replace { get; set; } = "";

    /// <summary>Strict: only text with exactly the same casing matches.</summary>
    public bool MatchCase { get; set; }

    /// <summary>When matching ignores case, mirror each match's casing onto the replacement.</summary>
    public bool PreserveCase { get; set; } = true;

    /// <summary>Rules generated during a scan (identity GUIDs) are never saved in profiles.</summary>
    [JsonIgnore]
    public RuleOrigin Origin { get; init; } = RuleOrigin.User;

    /// <summary>Why a generated rule exists, such as "Installer UpgradeCode".</summary>
    [JsonIgnore]
    public string? Description { get; init; }

    [JsonIgnore]
    public bool IsUsable => Enabled && Find.Length > 0;

    public ReplacementRule Clone() => new()
    {
        Id = Id,
        Enabled = Enabled,
        Find = Find,
        Replace = Replace,
        MatchCase = MatchCase,
        PreserveCase = PreserveCase,
        Origin = Origin,
        Description = Description,
    };

    public override string ToString() => $"{Find} → {Replace}";
}
