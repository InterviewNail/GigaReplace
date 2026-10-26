using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GigaReplace.Core.Apply;
using GigaReplace.Core.Planning;

namespace GigaReplace.Core.Memory;

/// <summary>
/// Decisions from earlier runs on one source folder, so a later scan of the same (or a newer)
/// tree only asks about what's new. Saved as JSON in the user's app data, never next to the code.
/// </summary>
public sealed class DecisionMemory
{
    public int Version { get; set; } = 1;

    /// <summary>The folder these decisions were made in.</summary>
    public string Root { get; set; } = "";

    public List<RememberedFile> Files { get; set; } = [];

    /// <summary>Choices made with "… all like this", applied to matching items anywhere.</summary>
    public List<RememberedPattern> Patterns { get; set; } = [];

    /// <summary>Files this app wrote, by their path in this folder and the hash of what was written.</summary>
    public List<ConvertedFile> Converted { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Files.Count == 0 && Patterns.Count == 0 && Converted.Count == 0;
}

/// <summary>The decisions for one file or folder name, with the file's hash when they were made.</summary>
public sealed class RememberedFile
{
    public string Path { get; set; } = "";

    /// <summary>SHA-256 (hex) of the file's bytes at the time; null for folders and rename-only files.</summary>
    public string? Hash { get; set; }

    public DateTime SavedUtc { get; set; }

    public List<RememberedDecision> Decisions { get; set; } = [];
}

/// <summary>One decided match, identified by what it is rather than only where it was.</summary>
public sealed class RememberedDecision
{
    /// <summary>The rule's find/replace/options; a decision only applies while the rule is unchanged.</summary>
    public string RuleKey { get; set; } = "";

    public ChangeKind Kind { get; set; }

    public int Offset { get; set; }

    public string Original { get; set; } = "";

    public string Word { get; set; } = "";

    public MatchContext Context { get; set; }

    public string Line { get; set; } = "";

    /// <summary>Which occurrence of <see cref="Original"/> on an identical <see cref="Line"/> this is (0-based).</summary>
    public int Occurrence { get; set; }

    public Decision Decision { get; set; }

    public string? CustomText { get; set; }
}

public sealed class RememberedPattern
{
    public string Key { get; set; } = "";

    public Decision Decision { get; set; }

    public string? CustomText { get; set; }

    /// <summary>A sample word, for people reading the file.</summary>
    public string Example { get; set; } = "";
}

public sealed class ConvertedFile
{
    public string Path { get; set; } = "";

    public string Hash { get; set; } = "";

    public DateTime WrittenUtc { get; set; }
}

/// <summary>How many decisions a scan took from memory.</summary>
public readonly record struct RecallSummary(int Same, int Changed, int Pattern, int AlreadyConverted)
{
    public int Total => Same + Changed + Pattern + AlreadyConverted;

    public int ToCheck => Changed + Pattern;
}

/// <summary>Reads and writes decision memories, one file per source folder.</summary>
public static class DecisionMemoryStore
{
    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GigaReplace", "Memory");

    private static readonly JsonSerializerOptions Json = ReplaceProfile.JsonOptions;

    /// <summary>The memory file for a folder: its name plus a hash of its full path, so two "src" folders don't collide.</summary>
    public static string PathFor(string root)
    {
        var full = System.IO.Path.GetFullPath(root).TrimEnd('\\').ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..12].ToLowerInvariant();
        var name = string.Concat(System.IO.Path.GetFileName(full).Split(System.IO.Path.GetInvalidFileNameChars())).ToLowerInvariant();
        return System.IO.Path.Combine(Folder, $"{(name.Length == 0 ? "root" : name)}-{hash}.json");
    }

    /// <summary>The folder's memory, or an empty one if there's none (or it can't be read).</summary>
    public static DecisionMemory Load(string root)
    {
        var path = PathFor(root);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<DecisionMemory>(File.ReadAllText(path), Json) is { } memory)
            {
                memory.Files ??= [];
                memory.Patterns ??= [];
                memory.Converted ??= [];
                return memory;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged memory only means starting fresh.
        }

        return new DecisionMemory { Root = System.IO.Path.GetFullPath(root) };
    }

    public static void Save(DecisionMemory memory)
    {
        var path = PathFor(memory.Root);
        if (memory.IsEmpty)
        {
            Delete(memory.Root);
            return;
        }

        Directory.CreateDirectory(Folder);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(memory, Json));
        File.Move(temp, path, overwrite: true);
    }

    public static bool Exists(string root) => File.Exists(PathFor(root));

    public static void Delete(string root)
    {
        var path = PathFor(root);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

/// <summary>Applies remembered decisions to a scan, and records a scan's decisions for next time.</summary>
public static class DecisionRecall
{
    /// <summary>Identifies a rule by what it does, so a re-created identical rule keeps its decisions and an edited one doesn't.</summary>
    public static string RuleKey(ReplacementRule rule) =>
        $"{rule.Find}\u001F{rule.Replace}\u001F{(rule.MatchCase ? 1 : 0)}{(rule.PreserveCase ? 1 : 0)}";

    /// <summary>What "… all like this" means for an item: same rule, text, kind of place and reasons.</summary>
    public static string PatternKey(ChangeItem item) =>
        $"{RuleKey(item.Rule)}|{item.Context}|{string.Join('+', item.Risks.Select(r => (int)r.Code).Order())}|{item.OriginalText}";

    public static RecallSummary Apply(ScanResult scan, DecisionMemory memory)
    {
        if (memory.IsEmpty)
        {
            return default;
        }

        var files = memory.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var converted = memory.Converted.ToDictionary(f => f.Path, f => f.Hash, StringComparer.OrdinalIgnoreCase);
        var patterns = memory.Patterns.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var occurrences = Occurrences(scan.Items);
        int same = 0, changed = 0, pattern = 0, alreadyConverted = 0;

        void Use(ChangeItem item, Decision decision, string? customText, RememberState state)
        {
            item.SetDecision(decision, customText);
            item.Remembered = state;
            switch (state)
            {
                case RememberState.Same: same++; break;
                case RememberState.Changed: changed++; break;
                case RememberState.Pattern: pattern++; break;
                default: alreadyConverted++; break;
            }
        }

        var eligible = scan.Items.Where(i => i.Rule.Origin == RuleOrigin.User && i.CanChange);
        foreach (var group in eligible.GroupBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            string? hash = HashOf(scan, group.Key);
            var items = group.ToList();
            if (hash is not null && converted.TryGetValue(group.Key, out var writtenHash) && writtenHash == hash)
            {
                items.ForEach(i => Use(i, Decision.Skip, null, RememberState.AlreadyConverted));
                continue;
            }

            var open = new List<ChangeItem>();
            if (files.TryGetValue(group.Key, out var file))
            {
                var unused = file.Decisions.Where(d => d.Decision != Decision.Pending).ToList();

                // 1. The same match: same place in an unchanged file (names never move), or the same line.
                foreach (var item in items)
                {
                    var hit = SameMatch(item, file, unused, hash, occurrences[item]);
                    if (hit is null)
                    {
                        open.Add(item);
                        continue;
                    }

                    unused.Remove(hit);
                    Use(item, hit.Decision, hit.CustomText, RememberState.Same);
                }

                // 2. A decision whose line changed goes to the one match on the most similar line, if any is similar enough.
                foreach (var d in unused.Where(d => d.Kind == ChangeKind.Content))
                {
                    var best = open
                        .Where(i => d.RuleKey == RuleKey(i.Rule) && d.Kind == i.Kind && d.Context == i.Context
                            && string.Equals(d.Original, i.OriginalText, StringComparison.Ordinal) && string.Equals(d.Word, i.WordText, StringComparison.Ordinal))
                        .Select(i => (Item: i, Score: Similarity(d.Line, i.LineText)))
                        .Where(x => x.Score >= 0.5)
                        .OrderByDescending(x => x.Score)
                        .FirstOrDefault();
                    if (best.Item is { } item)
                    {
                        open.Remove(item);
                        Use(item, d.Decision, d.CustomText, RememberState.Changed);
                    }
                }
            }
            else
            {
                open = items;
            }

            // 3. "… all like this" choices.
            foreach (var item in open)
            {
                if (patterns.TryGetValue(PatternKey(item), out var p) && p.Decision != Decision.Pending)
                {
                    Use(item, p.Decision, p.CustomText, RememberState.Pattern);
                }
            }
        }

        return new RecallSummary(same, changed, pattern, alreadyConverted);
    }

    private static RememberedDecision? SameMatch(ChangeItem item, RememberedFile file, List<RememberedDecision> unused, string? hash, int occurrence)
    {
        var key = RuleKey(item.Rule);
        var candidates = unused.Where(d => d.RuleKey == key && d.Kind == item.Kind && string.Equals(d.Original, item.OriginalText, StringComparison.Ordinal));
        if (item.Kind != ChangeKind.Content || (hash is not null && file.Hash == hash))
        {
            return candidates.FirstOrDefault(d => d.Offset == item.Offset);
        }

        return candidates.FirstOrDefault(d => d.Line == item.LineText && d.Occurrence == occurrence);
    }

    /// <summary>How alike two lines are, from 0 to 1 (1 − edit distance / longer length).</summary>
    internal static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
        {
            return 1;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return 1 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }
    /// <summary>
    /// Saves the scan's decisions into <paramref name="memory"/>. Only decisions that differ from what
    /// the scan proposed are kept (approved-automatically changes need no memory). Files in the scan
    /// replace their earlier entries; decisions for rules no longer in use are dropped.
    /// </summary>
    public static void Record(ScanResult scan, DecisionMemory memory, IEnumerable<ChangeItem> likeThis, IEnumerable<string> forgottenPatterns)
    {
        memory.Root = scan.RootPath;
        var ruleKeys = scan.Rules.Where(r => r.Origin == RuleOrigin.User).Select(RuleKey).ToHashSet(StringComparer.Ordinal);
        var occurrences = Occurrences(scan.Items);
        var files = memory.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var scannedPaths = scan.Tree.Files.Select(f => f.RelativePath).Concat(scan.Tree.Directories);
        var byPath = scan.Items.Where(IsRecordable).ToLookup(i => i.RelativePath, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;

        foreach (var path in scannedPaths)
        {
            var decided = byPath[path].ToList();
            if (decided.Count == 0)
            {
                files.Remove(path);
                continue;
            }

            files[path] = new RememberedFile
            {
                Path = path,
                Hash = decided.Any(i => i.Kind == ChangeKind.Content) ? HashOf(scan, path) : null,
                SavedUtc = now,
                Decisions = decided.Select(i => new RememberedDecision
                {
                    RuleKey = RuleKey(i.Rule),
                    Kind = i.Kind,
                    Offset = i.Offset,
                    Original = i.OriginalText,
                    Word = i.WordText,
                    Context = i.Context,
                    Line = i.LineText,
                    Occurrence = occurrences[i],
                    Decision = i.Decision,
                    CustomText = i.Decision == Decision.Custom ? i.CustomText : null,
                }).ToList(),
            };
        }

        foreach (var file in files.Values)
        {
            file.Decisions.RemoveAll(d => !ruleKeys.Contains(d.RuleKey));
        }

        memory.Files = files.Values.Where(f => f.Decisions.Count > 0).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();

        var patterns = memory.Patterns.ToDictionary(p => p.Key, StringComparer.Ordinal);
        foreach (var key in forgottenPatterns)
        {
            patterns.Remove(key);
        }

        foreach (var item in likeThis.Where(i => !i.IsPending && i.Rule.Origin == RuleOrigin.User && i.CanChange))
        {
            var key = PatternKey(item);
            patterns[key] = new RememberedPattern
            {
                Key = key,
                Decision = item.Decision,
                CustomText = item.Decision == Decision.Custom ? item.CustomText : null,
                Example = item.WordText,
            };
        }

        memory.Patterns = patterns.Values.Where(p => ruleKeys.Any(r => p.Key.StartsWith(r + "|", StringComparison.Ordinal))).ToList();
    }

    /// <summary>Remembers what an apply wrote, so scanning the result later recognizes it as already converted.</summary>
    public static void RecordOutputs(ApplyPlan plan, DecisionMemory outputMemory)
    {
        var converted = outputMemory.Converted.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        foreach (var write in plan.ContentWrites)
        {
            var path = plan.Paths.Map(write.RelativePath);
            converted[path] = new ConvertedFile { Path = path, Hash = Convert.ToHexString(SHA256.HashData(write.NewBytes)), WrittenUtc = now };
        }

        outputMemory.Converted = converted.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsRecordable(ChangeItem item) =>
        item.Rule.Origin == RuleOrigin.User
        && item.CanChange
        && !item.IsPending
        && item.Remembered != RememberState.AlreadyConverted
        && (item.Decision != item.DefaultDecision || item.Decision == Decision.Custom);

    private static string? HashOf(ScanResult scan, string path) =>
        scan.Files.TryGetValue(path, out var file) && file.Hash is { } hash ? Convert.ToHexString(hash) : null;

    /// <summary>For each item, which occurrence of its text on an identical line (in the same file, rule and kind) it is.</summary>
    private static Dictionary<ChangeItem, int> Occurrences(IEnumerable<ChangeItem> items)
    {
        var result = new Dictionary<ChangeItem, int>(ReferenceEqualityComparer.Instance);
        foreach (var group in items.GroupBy(i => (Path: i.RelativePath.ToUpperInvariant(), Rule: RuleKey(i.Rule), i.Kind, i.OriginalText, i.LineText)))
        {
            int n = 0;
            foreach (var item in group.OrderBy(i => i.Offset))
            {
                result[item] = n++;
            }
        }

        return result;
    }
}
