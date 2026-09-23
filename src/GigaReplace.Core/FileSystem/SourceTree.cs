using System.Collections.Immutable;

namespace GigaReplace.Core.FileSystem;

public sealed record TreeFile(string RelativePath, long Length, DateTime LastWriteUtc);

/// <summary>The files and folders a run works on. Relative paths use '\' and exclude the root.</summary>
public sealed class SourceTree
{
    public required string RootPath { get; init; }

    public required IReadOnlyList<TreeFile> Files { get; init; }

    public required IReadOnlyList<string> Directories { get; init; }

    /// <summary>Every child name per folder (included or not), for spotting name clashes.</summary>
    public required IReadOnlyDictionary<string, HashSet<string>> ChildNames { get; init; }

    /// <summary>Children that were left out, per folder.</summary>
    public required IReadOnlyDictionary<string, List<string>> ExcludedChildren { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }

    public int ExcludedCount { get; init; }

    public IReadOnlySet<string> GetChildNames(string directory) =>
        ChildNames.TryGetValue(directory, out var names) ? names : ImmutableHashSet<string>.Empty;
}

public static class SourceTreeEnumerator
{
    public static SourceTree Enumerate(string root, IEnumerable<string> excludePatterns, bool respectGitIgnore, CancellationToken ct = default)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Folder not found: {root}");
        }

        var files = new List<TreeFile>();
        var directories = new List<string>();
        var childNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var excludedChildren = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        int excludedCount = 0;

        var userRules = IgnoreRules.AtRootRelative("", excludePatterns.Select(p => p.Replace('\\', '/')));
        var gitRules = respectGitIgnore ? AncestorGitRules(root) : ImmutableList<IgnoreRules>.Empty;

        void Exclude(string parent, string name)
        {
            excludedCount++;
            if (!excludedChildren.TryGetValue(parent, out var list))
            {
                excludedChildren[parent] = list = [];
            }

            list.Add(name);
        }

        void Walk(string fullDir, string relDir, ImmutableList<IgnoreRules> stack)
        {
            ct.ThrowIfCancellationRequested();
            if (respectGitIgnore)
            {
                var gitIgnore = Path.Combine(fullDir, ".gitignore");
                if (File.Exists(gitIgnore))
                {
                    stack = stack.Add(IgnoreRules.AtRootRelative(relDir, ReadLines(gitIgnore)));
                }
            }

            List<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(fullDir)
                    .EnumerateFileSystemInfos("*", new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = false,
                        AttributesToSkip = 0,
                        ReturnSpecialDirectories = false,
                    })
                    .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add($"Couldn't read folder {DisplayRel(relDir)}: {ex.Message}");
                return;
            }

            var names = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            childNames[relDir] = names;

            foreach (var entry in entries)
            {
                bool isDir = entry is DirectoryInfo;
                string rel = relDir.Length == 0 ? entry.Name : relDir + "\\" + entry.Name;

                if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    // Repository internals are never processed (copy mode can copy them verbatim).
                    Exclude(relDir, entry.Name);
                    continue;
                }

                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    notes.Add($"Skipped link or junction: {rel}");
                    Exclude(relDir, entry.Name);
                    continue;
                }

                string slashPath = rel.Replace('\\', '/');
                if (IsIgnored(slashPath, isDir, userRules, stack))
                {
                    Exclude(relDir, entry.Name);
                    continue;
                }

                if (isDir)
                {
                    directories.Add(rel);
                    Walk(entry.FullName, rel, stack);
                }
                else
                {
                    var info = (FileInfo)entry;
                    files.Add(new TreeFile(rel, info.Length, info.LastWriteTimeUtc));
                }
            }
        }

        Walk(root, "", gitRules);

        return new SourceTree
        {
            RootPath = root,
            Files = files,
            Directories = directories,
            ChildNames = childNames,
            ExcludedChildren = excludedChildren,
            Notes = notes,
            ExcludedCount = excludedCount,
        };
    }

    private static bool IsIgnored(string path, bool isDir, IgnoreRules userRules, ImmutableList<IgnoreRules> gitStack)
    {
        if (userRules.Evaluate(path, isDir) == IgnoreVerdict.Ignored)
        {
            return true;
        }

        // Deeper .gitignore files take precedence; within one file the last match wins.
        for (int i = gitStack.Count - 1; i >= 0; i--)
        {
            var verdict = gitStack[i].Evaluate(path, isDir);
            if (verdict != IgnoreVerdict.None)
            {
                return verdict == IgnoreVerdict.Ignored;
            }
        }

        return false;
    }

    /// <summary>.gitignore files between the repository root and the scan root, plus .git/info/exclude.</summary>
    private static ImmutableList<IgnoreRules> AncestorGitRules(string root)
    {
        var ancestors = new List<string>();
        string? repoRoot = null;
        for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent)
        {
            if (Path.Exists(Path.Combine(dir.FullName, ".git")))
            {
                repoRoot = dir.FullName;
                break;
            }

            if (!string.Equals(dir.FullName, root, StringComparison.OrdinalIgnoreCase))
            {
                ancestors.Add(dir.FullName);
            }
        }

        if (repoRoot is null)
        {
            return ImmutableList<IgnoreRules>.Empty;
        }

        var rules = ImmutableList<IgnoreRules>.Empty;
        var exclude = Path.Combine(repoRoot, ".git", "info", "exclude");
        if (File.Exists(exclude))
        {
            rules = rules.Add(Anchored(repoRoot, root, ReadLines(exclude)));
        }

        if (!string.Equals(repoRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            ancestors.Add(repoRoot);
            ancestors.Reverse(); // repository root first
            foreach (var dir in ancestors)
            {
                var gitIgnore = Path.Combine(dir, ".gitignore");
                if (File.Exists(gitIgnore))
                {
                    rules = rules.Add(Anchored(dir, root, ReadLines(gitIgnore)));
                }
            }
        }

        return rules;
    }

    private static IgnoreRules Anchored(string rulesDir, string root, IEnumerable<string> lines)
    {
        var rootFromBase = Path.GetRelativePath(rulesDir, root);
        return rootFromBase == "."
            ? IgnoreRules.AtRootRelative("", lines)
            : IgnoreRules.AboveRoot(rootFromBase, lines);
    }

    private static string[] ReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string DisplayRel(string rel) => rel.Length == 0 ? "(root)" : rel;
}
