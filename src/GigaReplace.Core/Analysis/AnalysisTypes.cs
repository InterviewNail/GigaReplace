using GigaReplace.Core.Text;

namespace GigaReplace.Core.Analysis;

/// <summary>What an analyzer found out about one match.</summary>
internal sealed class MatchAnalysis
{
    public MatchContext Context { get; set; } = MatchContext.Text;

    public string Detail { get; set; } = "";

    public string? SymbolKey { get; set; }

    public int WordStart { get; set; }

    public int WordLength { get; set; }

    public List<RiskFinding> Risks { get; } = [];

    public bool Has(RiskCode code) => Risks.Any(r => r.Code == code);

    public void Add(RiskCode code, string message)
    {
        if (!Has(code))
        {
            Risks.Add(new RiskFinding(code, message));
        }
    }

    public string Word(string text) => text.Substring(WordStart, WordLength);

    public static MatchAnalysis ForWord(string text, RawMatch match, MatchContext context, string detail, bool symbol = false)
    {
        var (start, length) = WordShape.ExpandToWord(text, match.Start, match.Length);
        var analysis = new MatchAnalysis { Context = context, Detail = detail, WordStart = start, WordLength = length };
        if (symbol)
        {
            analysis.SymbolKey = text.Substring(start, length);
        }

        return analysis;
    }
}

internal interface IFileAnalyzer
{
    MatchAnalysis Analyze(int matchIndex);
}

/// <summary>One file's text and matches, shared by its analyzer.</summary>
internal sealed class FileAnalysisContext
{
    public required string RelativePath { get; init; }

    public required string Text { get; init; }

    public required SolutionIndex Index { get; init; }

    public required IReadOnlyList<RawMatch> Matches { get; init; }

    /// <summary>The proposed replacement of each match (parallel to <see cref="Matches"/>).</summary>
    public required IReadOnlyList<string> Proposals { get; init; }

    public FileKind Kind => FileKinds.Of(RelativePath);

    /// <summary>A span of the text with every match inside it replaced by its proposal.</summary>
    public string ProposedSpan(int start, int length)
    {
        int end = start + length;
        var edits = new List<TextEdit>();
        for (int i = FirstMatchAtOrAfter(start); i < Matches.Count && Matches[i].Start < end; i++)
        {
            if (Matches[i].End <= end)
            {
                edits.Add(new TextEdit(Matches[i].Start - start, Matches[i].Length, Proposals[i]));
            }
        }

        return TextEdits.Apply(Text.Substring(start, length), edits);
    }

    private int FirstMatchAtOrAfter(int position)
    {
        int lo = 0, hi = Matches.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Matches[mid].Start < position)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}

/// <summary>A GUID that identifies the product (UpgradeCode, ProjectGuid, …) and can be regenerated.</summary>
public sealed record IdentityGuidCandidate(string Guid, string Label, string FirstFile);

internal enum ReferenceBase
{
    FileDirectory,
    ProjectDirectory,
}

/// <summary>A file path written inside a project, solution or XAML file.</summary>
internal sealed record PathReference(string FileRelativePath, int Start, int Length, ReferenceBase Base);

internal sealed class ProjectFacts
{
    public required string RelativePath { get; init; }

    public string? AssemblyName { get; set; }

    public string? RootNamespace { get; set; }

    public string? PackageId { get; set; }

    public string FileBaseName => Path.GetFileNameWithoutExtension(RelativePath);
}

/// <summary>Facts gathered from one file during indexing, merged into the <see cref="SolutionIndex"/>.</summary>
internal sealed class FileFacts
{
    public List<string> Declared { get; } = [];

    public List<string> WeakDeclared { get; } = [];

    public List<string> TypeNames { get; } = [];

    public List<(string Namespace, string Type)> Types { get; } = [];

    public List<(string TypeKey, string Member)> Members { get; } = [];

    public List<(string TypeKey, string Local)> Locals { get; } = [];

    public List<(string Type, string Reason)> Serialized { get; } = [];

    public List<string> XmlContract { get; } = [];

    public List<string> SettingsTypes { get; } = [];

    public List<string> StringEnums { get; } = [];

    public List<(string Type, string MemberType)> MemberTypes { get; } = [];

    public bool UsesStringEnumConverter { get; set; }

    public ProjectFacts? Project { get; set; }

    public List<IdentityGuidCandidate> Guids { get; } = [];

    public List<PathReference> PathReferences { get; } = [];
}
