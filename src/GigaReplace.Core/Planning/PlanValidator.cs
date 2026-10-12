using GigaReplace.Core.Analysis;
using GigaReplace.Core.FileSystem;
using GigaReplace.Core.Text;

namespace GigaReplace.Core.Planning;

public enum ProblemSeverity
{
    Warning,
    Error,
}

/// <summary>A one-click fix: give <see cref="Items"/> the decision <see cref="Decision"/>.</summary>
public sealed record QuickFix(string Label, IReadOnlyList<ChangeItem> Items, Decision Decision);

/// <summary>Something wrong with the plan as a whole, given the current decisions.</summary>
public sealed class PlanProblem
{
    public required ProblemSeverity Severity { get; init; }

    public required string Title { get; init; }

    public required string Message { get; init; }

    public required IReadOnlyList<ChangeItem> Items { get; init; }

    public IReadOnlyList<QuickFix> Fixes { get; init; } = [];

    public bool IsError => Severity == ProblemSeverity.Error;
}

/// <summary>
/// Checks the plan after decisions: names renamed in some places but not others, clashing
/// file names, file references that no longer resolve, and characters a file can't store.
/// </summary>
public static class PlanValidator
{
    public static IReadOnlyList<PlanProblem> Validate(ScanResult scan)
    {
        var problems = new List<PlanProblem>();
        CheckConsistency(scan, problems);
        var mapper = FinalPathMapper.FromDecisions(scan);
        CheckNames(scan, mapper, problems);
        CheckPathReferences(scan, mapper, problems);
        CheckEncodings(scan, problems);
        return problems
            .OrderByDescending(p => p.Severity)
            .ThenBy(p => p.Title, StringComparer.Ordinal)
            .ToList();
    }

    private static void CheckConsistency(ScanResult scan, List<PlanProblem> problems)
    {
        // Symbol → final spelling → items. One "instance" is one occurrence of a whole word.
        var bySymbol = new Dictionary<string, Dictionary<string, List<ChangeItem>>>(StringComparer.Ordinal);
        var instances = scan.Items
            .Where(i => i.SymbolKey is not null && i.Kind == ChangeKind.Content)
            .GroupBy(i => (i.RelativePath, i.WordStart));

        foreach (var instance in instances)
        {
            if (instance.Any(i => i.IsPending))
            {
                continue;
            }

            var first = instance.First();
            string finalWord = TextEdits.ApplyDecisions(first.WordText, instance, first.WordStart);
            if (!bySymbol.TryGetValue(first.SymbolKey!, out var spellings))
            {
                bySymbol[first.SymbolKey!] = spellings = new Dictionary<string, List<ChangeItem>>(StringComparer.Ordinal);
            }

            if (!spellings.TryGetValue(finalWord, out var list))
            {
                spellings[finalWord] = list = [];
            }

            list.AddRange(instance);
        }

        foreach (var (symbol, spellings) in bySymbol)
        {
            if (spellings.Count < 2)
            {
                continue;
            }

            var all = spellings.Values.SelectMany(v => v).ToList();
            var summary = string.Join(", ", spellings
                .OrderByDescending(s => s.Value.Count)
                .Select(s => $"“{s.Key}” in {Count(s.Value.Select(i => (i.RelativePath, i.WordStart)).Distinct().Count(), "place")}"));

            bool isGuid = symbol.StartsWith("guid:", StringComparison.Ordinal);
            string name = isGuid ? symbol[5..] : symbol.Split('@')[0];
            problems.Add(new PlanProblem
            {
                Severity = ProblemSeverity.Warning,
                Title = isGuid ? "GUID only partly regenerated" : "Name only partly renamed",
                Message = isGuid
                    ? $"The GUID {name} would end up with different values: {summary}. Every copy of it has to change together."
                    : $"“{name}” would end up as {summary}. Code using one spelling won't find the other.",
                Items = all,
                Fixes =
                [
                    new QuickFix("Replace everywhere", all, Decision.Replace),
                    new QuickFix("Keep the old name everywhere", all, Decision.Skip),
                ],
            });
        }
    }

    private static void CheckNames(ScanResult scan, FinalPathMapper mapper, List<PlanProblem> problems)
    {
        if (!mapper.HasRenames)
        {
            return;
        }

        foreach (var (path, newName) in mapper.NewNames)
        {
            if (PathNames.Problem(newName) is { } problem)
            {
                var items = scan.RenamesOf(path).ToList();
                problems.Add(new PlanProblem
                {
                    Severity = ProblemSeverity.Error,
                    Title = "Invalid name",
                    Message = $"“{path}” can't be renamed to “{newName}”: {problem}.",
                    Items = items,
                    Fixes = [new QuickFix("Keep the old name", items, Decision.Skip)],
                });
            }
        }

        var byFinalPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string original, string final)
        {
            if (!byFinalPath.TryGetValue(final, out var list))
            {
                byFinalPath[final] = list = [];
            }

            list.Add(original);
        }

        foreach (var file in scan.Tree.Files)
        {
            Add(file.RelativePath, mapper.Map(file.RelativePath));
        }

        foreach (var directory in scan.Tree.Directories)
        {
            Add(directory, mapper.Map(directory));
        }

        foreach (var (parent, names) in scan.Tree.ExcludedChildren)
        {
            foreach (var name in names)
            {
                Add(PathNames.Join(parent, name), PathNames.Join(mapper.Map(parent), name));
            }
        }

        foreach (var (final, originals) in byFinalPath)
        {
            if (originals.Count < 2)
            {
                continue;
            }

            var items = originals.SelectMany(scan.RenamesOf).Where(i => i.WillChange).ToList();
            if (items.Count == 0)
            {
                continue;
            }

            problems.Add(new PlanProblem
            {
                Severity = ProblemSeverity.Error,
                Title = "Name clash",
                Message = $"{string.Join(" and ", originals.Select(o => $"“{o}”"))} would all end up as “{final}”.",
                Items = items,
                Fixes = [new QuickFix("Keep the old names", items, Decision.Skip)],
            });
        }
    }

    private static void CheckPathReferences(ScanResult scan, FinalPathMapper mapper, List<PlanProblem> problems)
    {
        if (scan.Index.PathReferences.Count == 0)
        {
            return;
        }

        var existing = new HashSet<string>(scan.Tree.Files.Select(f => f.RelativePath).Concat(scan.Tree.Directories), StringComparer.OrdinalIgnoreCase);
        var projectDirectories = new HashSet<string>(scan.Index.Projects.Keys.Select(PathNames.ParentOf), StringComparer.OrdinalIgnoreCase);

        foreach (var (file, references) in scan.Index.PathReferences)
        {
            if (!scan.Files.TryGetValue(file, out var scanned) || scanned.Content is null)
            {
                continue;
            }

            string text = scanned.Content.Text;
            var fileItems = scan.ContentItemsOf(file);
            foreach (var reference in references)
            {
                string baseDirectory = reference.Base == ReferenceBase.ProjectDirectory
                    ? ProjectDirectoryOf(file, projectDirectories)
                    : PathNames.ParentOf(file);
                string originalValue = text.Substring(reference.Start, reference.Length);
                var originalTarget = PathNames.Resolve(baseDirectory, originalValue);
                if (originalTarget is null || !existing.Contains(originalTarget))
                {
                    continue;
                }

                var inside = fileItems.Where(i => i.Offset >= reference.Start && i.Offset + i.Length <= reference.Start + reference.Length).ToList();
                string finalValue = TextEdits.ApplyDecisions(originalValue, inside, reference.Start);
                var finalTarget = PathNames.Resolve(mapper.Map(baseDirectory), finalValue);
                string expected = mapper.Map(originalTarget);
                if (finalTarget is not null && string.Equals(finalTarget, expected, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var renames = AlongPath(scan, originalTarget).ToList();
                var related = inside.Concat(renames).ToList();
                if (related.Count == 0 || inside.Any(i => i.IsPending) || renames.Any(i => i.IsPending))
                {
                    continue;
                }

                var fixes = new List<QuickFix>();
                if (inside.Count > 0 && inside.Any(i => !i.WillChange))
                {
                    fixes.Add(new QuickFix("Update the reference", inside, Decision.Replace));
                }

                var changingRenames = renames.Where(i => i.WillChange).ToList();
                if (changingRenames.Count > 0)
                {
                    fixes.Add(new QuickFix("Keep the old file name", changingRenames, Decision.Skip));
                }

                problems.Add(new PlanProblem
                {
                    Severity = ProblemSeverity.Warning,
                    Title = "Broken file reference",
                    Message = $"{file} refers to “{finalValue}”, but after the changes that file will be at “{expected}”.",
                    Items = related,
                    Fixes = fixes,
                });
            }
        }
    }

    private static IEnumerable<ChangeItem> AlongPath(ScanResult scan, string relativePath)
    {
        var parts = relativePath.Split('\\');
        string current = "";
        foreach (var part in parts)
        {
            current = PathNames.Join(current, part);
            foreach (var item in scan.RenamesOf(current))
            {
                yield return item;
            }
        }
    }

    private static string ProjectDirectoryOf(string file, HashSet<string> projectDirectories)
    {
        for (string dir = PathNames.ParentOf(file); ; dir = PathNames.ParentOf(dir))
        {
            if (projectDirectories.Contains(dir))
            {
                return dir;
            }

            if (dir.Length == 0)
            {
                return PathNames.ParentOf(file);
            }
        }
    }

    private static void CheckEncodings(ScanResult scan, List<PlanProblem> problems)
    {
        foreach (var group in scan.Items.Where(i => i.Kind == ChangeKind.Content && i.WillChange).GroupBy(i => i.RelativePath))
        {
            if (scan.Files[group.Key].Pdf is { } pdf)
            {
                CheckPdf(group.Key, pdf, group, problems);
                continue;
            }

            var content = scan.Files[group.Key].Content;
            if (content is null || !content.IsLegacy8Bit)
            {
                continue;
            }

            var bad = group.Where(i => !content.CanEncode(i.FinalText)).ToList();
            if (bad.Count > 0)
            {
                problems.Add(new PlanProblem
                {
                    Severity = ProblemSeverity.Error,
                    Title = "Characters the file can't store",
                    Message = $"{group.Key} uses an 8-bit ANSI encoding, which can't store some characters of the replacement text.",
                    Items = bad,
                    Fixes = [new QuickFix("Keep the old text", bad, Decision.Skip)],
                });
            }
        }
    }

    /// <summary>Custom text in a PDF must be drawable with the font already there.</summary>
    private static void CheckPdf(string path, Pdf.PdfContent pdf, IEnumerable<ChangeItem> items, List<PlanProblem> problems)
    {
        var bad = items.Where(i => pdf.CannotWrite(i.Offset, i.Length, i.FinalText) is not null).ToList();
        if (bad.Count > 0)
        {
            problems.Add(new PlanProblem
            {
                Severity = ProblemSeverity.Error,
                Title = "Text the PDF can't draw",
                Message = $"{path}: {pdf.CannotWrite(bad[0].Offset, bad[0].Length, bad[0].FinalText)}",
                Items = bad,
                Fixes = [new QuickFix("Keep the old text", bad, Decision.Skip)],
            });
        }
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
