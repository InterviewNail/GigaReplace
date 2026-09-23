using System.Collections.Concurrent;
using System.Diagnostics;
using GigaReplace.Core.Analysis;
using GigaReplace.Core.FileSystem;
using GigaReplace.Core.Text;

namespace GigaReplace.Core.Planning;

public sealed class ScanException(string message) : Exception(message);

/// <summary>
/// Finds every match of the rules in file contents, file names and folder names, and judges
/// each one. Nothing is written. Runs in two passes: first index the whole tree (declarations,
/// serialized types, projects, identity GUIDs), then analyze each match against that index.
/// </summary>
public static class Scanner
{
    private const int MaxWordLength = 256;
    private const int MaxLineLength = 400;

    public static Task<ScanResult> ScanAsync(ReplaceProfile profile, IProgress<ScanProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => Scan(profile, progress, ct), ct);

    public static ScanResult Scan(ReplaceProfile profile, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        profile = profile.Clone();
        var (root, solutionFile) = ResolveSource(profile.SourcePath);
        var userRules = profile.Rules.Where(r => r.IsUsable).ToList();
        if (userRules.Count == 0 && !profile.RegenerateIdentityGuids)
        {
            throw new ScanException("Add at least one find → replace pair.");
        }

        var warnings = new ConcurrentBag<string>();
        progress?.Report(new ScanProgress("Listing files", 0, 0));
        var tree = SourceTreeEnumerator.Enumerate(root, profile.ExcludePatterns, profile.RespectGitIgnore, ct);
        foreach (var note in tree.Notes)
        {
            warnings.Add(note);
        }

        // Pass 1: read and index every file.
        long maxBytes = (long)Math.Max(1, profile.MaxFileSizeMb) * 1024 * 1024;
        var scanned = new ScannedFile[tree.Files.Count];
        var facts = new FileFacts?[tree.Files.Count];
        int done = 0;
        Parallel.For(0, tree.Files.Count, new ParallelOptions { CancellationToken = ct }, i =>
        {
            var entry = tree.Files[i];
            var read = TextFileReader.Read(Path.Combine(root, entry.RelativePath), maxBytes);
            scanned[i] = new ScannedFile
            {
                RelativePath = entry.RelativePath,
                Length = entry.Length,
                LastWriteUtc = entry.LastWriteUtc,
                Status = read.Status,
                Content = read.Content,
                Hash = read.Hash,
                Error = read.Error,
            };

            if (read.Status == FileReadStatus.Unreadable)
            {
                warnings.Add($"Couldn't read {entry.RelativePath}: {read.Error}");
            }
            else if (read.Status == FileReadStatus.TooLarge)
            {
                warnings.Add($"Skipped the contents of {entry.RelativePath} (larger than {profile.MaxFileSizeMb} MB).");
            }

            if (read.Content is { } content)
            {
                try
                {
                    facts[i] = FactsCollector.Collect(entry.RelativePath, content.Text);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    warnings.Add($"Couldn't index {entry.RelativePath}: {ex.Message}");
                }
            }

            Report(progress, "Reading and indexing", Interlocked.Increment(ref done), tree.Files.Count);
        });

        var index = SolutionIndex.Build(Enumerable.Range(0, facts.Length)
            .Where(i => facts[i] is not null)
            .Select(i => (tree.Files[i].RelativePath, facts[i]!)));

        var rules = new List<ReplacementRule>(userRules);
        if (profile.RegenerateIdentityGuids)
        {
            rules.AddRange(index.IdentityGuids.Select(g => new ReplacementRule
            {
                Find = g.Guid,
                Replace = Guid.NewGuid().ToString("D"),
                MatchCase = false,
                PreserveCase = true,
                Origin = RuleOrigin.IdentityGuid,
                Description = g.Label,
            }));
        }

        var contentRules = profile.ReplaceInContents ? rules : rules.Where(r => r.Origin == RuleOrigin.IdentityGuid).ToList();
        var watch = profile.RiskChecks.WatchList ? WatchList.Compile(profile.WatchList) : WatchList.Empty;

        // Pass 2: find and judge matches.
        var items = new ConcurrentBag<ChangeItem>();
        var textFiles = scanned.Where(s => s.Content is not null).ToList();
        done = 0;
        if (contentRules.Count > 0)
        {
            Parallel.ForEach(textFiles, new ParallelOptions { CancellationToken = ct }, file =>
            {
                foreach (var item in ContentItems(file, contentRules, index, profile, watch, warnings))
                {
                    items.Add(item);
                }

                Report(progress, "Analyzing matches", Interlocked.Increment(ref done), textFiles.Count);
            });
        }

        progress?.Report(new ScanProgress("Checking names", 0, 0));
        if (profile.RenameFiles)
        {
            foreach (var file in tree.Files)
            {
                foreach (var item in RenameItems(file.RelativePath, false, userRules, tree, index, profile, watch))
                {
                    items.Add(item);
                }
            }
        }

        if (profile.RenameFolders)
        {
            foreach (var directory in tree.Directories)
            {
                foreach (var item in RenameItems(directory, true, userRules, tree, index, profile, watch))
                {
                    items.Add(item);
                }
            }
        }

        var ordered = items
            .OrderBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Kind == ChangeKind.Content ? 0 : 1)
            .ThenBy(i => i.Offset)
            .ToList();
        for (int k = 0; k < ordered.Count; k++)
        {
            ordered[k].Id = k + 1;
        }

        return new ScanResult
        {
            RootPath = root,
            Profile = profile,
            SolutionFile = solutionFile,
            Tree = tree,
            Files = scanned.ToDictionary(s => s.RelativePath, StringComparer.OrdinalIgnoreCase),
            Items = ordered,
            Index = index,
            Rules = rules,
            Warnings = warnings.OrderBy(w => w, StringComparer.OrdinalIgnoreCase).ToList(),
            Duration = clock.Elapsed,
        };
    }

    /// <summary>The folder to process and, if known, the solution file inside it.</summary>
    public static (string Root, string? SolutionFile) ResolveSource(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ScanException("Choose a solution file or folder first.");
        }

        var full = Path.GetFullPath(sourcePath.Trim().Trim('"'));
        if (File.Exists(full))
        {
            var ext = Path.GetExtension(full).ToLowerInvariant();
            if (ext is not (".sln" or ".slnx" or ".slnf" or ".csproj" or ".vbproj" or ".fsproj"))
            {
                throw new ScanException("Choose a folder, or a .sln / .slnx / project file.");
            }

            return (Path.GetDirectoryName(full)!, Path.GetFileName(full));
        }

        if (!Directory.Exists(full))
        {
            throw new ScanException($"“{full}” doesn't exist.");
        }

        var solution = Directory.EnumerateFiles(full, "*.slnx").Concat(Directory.EnumerateFiles(full, "*.sln"))
            .Select(Path.GetFileName)
            .FirstOrDefault();
        return (full, solution);
    }

    private static IEnumerable<ChangeItem> ContentItems(
        ScannedFile file,
        IReadOnlyList<ReplacementRule> rules,
        SolutionIndex index,
        ReplaceProfile profile,
        WatchList watch,
        ConcurrentBag<string> warnings)
    {
        var text = file.Content!.Text;
        var matches = RuleMatcher.FindAll(text, rules);
        if (matches.Count == 0)
        {
            return [];
        }

        var proposals = matches.Select(m => CasePreserver.Propose(m.Rule, text.Substring(m.Start, m.Length))).ToList();
        var ctx = new FileAnalysisContext
        {
            RelativePath = file.RelativePath,
            Text = text,
            Index = index,
            Matches = matches,
            Proposals = proposals.Select(p => p.Text).ToList(),
        };

        IFileAnalyzer? analyzer = null;
        if (matches.Any(m => m.Rule.Origin == RuleOrigin.User))
        {
            try
            {
                analyzer = AnalyzerFactory.Create(ctx);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Couldn't analyze {file.RelativePath}; its matches are treated as plain text ({ex.Message}).");
            }
        }

        var lines = new LineIndex(text);
        var result = new List<ChangeItem>(matches.Count);
        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var original = text.Substring(match.Start, match.Length);
            var proposal = proposals[i];
            if (string.Equals(proposal.Text, original, StringComparison.Ordinal))
            {
                continue;
            }

            MatchAnalysis analysis;
            var risks = new List<RiskFinding>();
            int line = lines.LineOf(match.Start);
            string lineText = lines.LineText(line);

            if (match.Rule.Origin == RuleOrigin.IdentityGuid)
            {
                analysis = new MatchAnalysis
                {
                    Context = MatchContext.IdentityGuid,
                    Detail = match.Rule.Description ?? "identity GUID",
                    WordStart = match.Start,
                    WordLength = match.Length,
                    SymbolKey = "guid:" + match.Rule.Find,
                };
            }
            else
            {
                analysis = SafeAnalyze(analyzer, i, text, match);
                if (!WordShape.IsAligned(text, match.Start, match.Length))
                {
                    risks.Add(MidWord(original, text.Substring(analysis.WordStart, analysis.WordLength)));
                }

                if (proposal.Ambiguous)
                {
                    risks.Add(AmbiguousCasing(original, proposal.Text));
                }

                risks.AddRange(analysis.Risks);
                if (watch.FirstHit(lineText) is { } term)
                {
                    risks.Add(new RiskFinding(RiskCode.WatchListTerm, $"This line contains “{term}”, which is on your watch-list."));
                }

                risks = Enabled(risks, profile);
            }

            // Keep the word sensible for display: it must contain the match and stay short.
            int wordStart = analysis.WordStart, wordLength = analysis.WordLength;
            if (wordStart > match.Start || wordStart + wordLength < match.End || wordLength > MaxWordLength)
            {
                (wordStart, wordLength) = (match.Start, match.Length);
            }

            int lineStart = lines.LineStart(line);
            var (shownLine, matchInLine) = Window(lineText, match.Start - lineStart, match.Length);

            result.Add(new ChangeItem
            {
                Kind = ChangeKind.Content,
                Rule = match.Rule,
                RelativePath = file.RelativePath,
                Offset = match.Start,
                Length = match.Length,
                OriginalText = original,
                ProposedText = proposal.Text,
                Line = line + 1,
                Column = match.Start - lineStart + 1,
                Context = analysis.Context,
                ContextDetail = analysis.Detail,
                SymbolKey = analysis.SymbolKey,
                WordStart = wordStart,
                WordText = text.Substring(wordStart, wordLength),
                LineText = shownLine,
                MatchStartInLine = matchInLine,
                Risks = risks,
                SimilarityKey = SimilarityKey(match.Rule, analysis.Context, risks, original),
                DefaultDecision = risks.Count > 0 ? Decision.Pending : Decision.Replace,
            });
        }

        foreach (var item in result)
        {
            item.Decision = item.DefaultDecision;
        }

        return result;
    }

    private static MatchAnalysis SafeAnalyze(IFileAnalyzer? analyzer, int index, string text, RawMatch match)
    {
        if (analyzer is not null)
        {
            try
            {
                return analyzer.Analyze(index);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fall through to plain-text treatment.
            }
        }

        return MatchAnalysis.ForWord(text, match, MatchContext.Text, "text");
    }

    private static IEnumerable<ChangeItem> RenameItems(
        string relativePath,
        bool isDirectory,
        IReadOnlyList<ReplacementRule> rules,
        SourceTree tree,
        SolutionIndex index,
        ReplaceProfile profile,
        WatchList watch)
    {
        string name = PathNames.NameOf(relativePath);
        var matches = RuleMatcher.FindAll(name, rules);
        if (matches.Count == 0)
        {
            yield break;
        }

        var proposals = matches.Select(m => CasePreserver.Propose(m.Rule, name.Substring(m.Start, m.Length))).ToList();
        string newName = TextEdits.Apply(name, matches.Select((m, i) => new TextEdit(m.Start, m.Length, proposals[i].Text)));
        if (string.Equals(newName, name, StringComparison.Ordinal))
        {
            yield break;
        }

        string parent = PathNames.ParentOf(relativePath);
        string? nameProblem = PathNames.Problem(newName);
        bool targetExists = !newName.Equals(name, StringComparison.OrdinalIgnoreCase) && tree.GetChildNames(parent).Contains(newName);
        var projectRisk = !isDirectory && FileKinds.IsProjectFile(relativePath) ? DefaultNamesRisk(relativePath, newName, index) : null;
        string? watchTerm = watch.FirstHit(relativePath);
        int nameOffsetInPath = relativePath.Length - name.Length;

        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var original = name.Substring(match.Start, match.Length);
            if (string.Equals(proposals[i].Text, original, StringComparison.Ordinal))
            {
                continue;
            }

            var risks = new List<RiskFinding>();
            if (!WordShape.IsAligned(name, match.Start, match.Length))
            {
                var (ws, wl) = WordShape.ExpandToWord(name, match.Start, match.Length);
                risks.Add(MidWord(original, name.Substring(ws, wl)));
            }

            if (proposals[i].Ambiguous)
            {
                risks.Add(AmbiguousCasing(original, proposals[i].Text));
            }

            if (nameProblem is not null)
            {
                risks.Add(new RiskFinding(RiskCode.InvalidFileName, $"“{newName}” can't be used: {nameProblem}."));
            }

            if (targetExists)
            {
                risks.Add(new RiskFinding(RiskCode.RenameTargetExists, $"This folder already contains something named “{newName}”."));
            }

            if (projectRisk is not null)
            {
                risks.Add(projectRisk);
            }

            if (watchTerm is not null)
            {
                risks.Add(new RiskFinding(RiskCode.WatchListTerm, $"The path contains “{watchTerm}”, which is on your watch-list."));
            }

            risks = Enabled(risks, profile);
            var context = isDirectory ? MatchContext.FolderName : MatchContext.FileName;
            var item = new ChangeItem
            {
                Kind = isDirectory ? ChangeKind.FolderRename : ChangeKind.FileRename,
                Rule = match.Rule,
                RelativePath = relativePath,
                Offset = match.Start,
                Length = match.Length,
                OriginalText = original,
                ProposedText = proposals[i].Text,
                Context = context,
                ContextDetail = isDirectory ? "folder name" : "file name",
                WordStart = 0,
                WordText = name,
                LineText = relativePath,
                MatchStartInLine = nameOffsetInPath + match.Start,
                Risks = risks,
                SimilarityKey = SimilarityKey(match.Rule, context, risks, original),
                DefaultDecision = risks.Count > 0 ? Decision.Pending : Decision.Replace,
            };
            item.Decision = item.DefaultDecision;
            yield return item;
        }
    }

    private static RiskFinding? DefaultNamesRisk(string projectPath, string newFileName, SolutionIndex index)
    {
        if (!index.Projects.TryGetValue(projectPath, out var project))
        {
            return null;
        }

        var newBase = Path.GetFileNameWithoutExtension(newFileName);
        var effects = new List<string>();
        if (project.AssemblyName is null)
        {
            effects.Add($"the assembly will be built as “{newBase}.dll” (or .exe)");
        }

        if (project.RootNamespace is null)
        {
            effects.Add($"the default namespace and embedded-resource names will start with “{newBase}”");
        }

        if (project.PackageId is null && project.AssemblyName is null)
        {
            effects.Add("its NuGet package ID changes too, if it's packed");
        }

        return effects.Count == 0
            ? null
            : new RiskFinding(RiskCode.DefaultProjectNames, $"The project doesn't set these names itself, so they follow the file name: {string.Join("; ", effects)}.");
    }

    private static RiskFinding MidWord(string matched, string word) =>
        new(RiskCode.MidWord, $"“{matched}” is joined to other letters in “{word}”, so it may be part of a different word.");

    private static RiskFinding AmbiguousCasing(string matched, string proposed) =>
        new(RiskCode.AmbiguousCasing, $"“{matched}” has unusual casing, so the replacement's casing is a guess: “{proposed}”.");

    private static List<RiskFinding> Enabled(List<RiskFinding> risks, ReplaceProfile profile) =>
        risks.Where(r => profile.RiskChecks.IsEnabled(r.Category)).DistinctBy(r => r.Code).ToList();

    private static string SimilarityKey(ReplacementRule rule, MatchContext context, IReadOnlyList<RiskFinding> risks, string original) =>
        $"{rule.Id:N}|{context}|{string.Join('+', risks.Select(r => (int)r.Code).Order())}|{original}";

    private static (string Line, int MatchStart) Window(string line, int matchStart, int matchLength)
    {
        if (line.Length <= MaxLineLength)
        {
            return (line, matchStart);
        }

        int from = Math.Max(0, matchStart - 150);
        int to = Math.Min(line.Length, matchStart + matchLength + 150);
        var prefix = from > 0 ? "…" : "";
        var suffix = to < line.Length ? "…" : "";
        return (prefix + line[from..to] + suffix, matchStart - from + prefix.Length);
    }

    private static void Report(IProgress<ScanProgress>? progress, string stage, int done, int total)
    {
        if (progress is not null && (done % 25 == 0 || done == total))
        {
            progress.Report(new ScanProgress(stage, done, total));
        }
    }
}
