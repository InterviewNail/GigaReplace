using GigaReplace.Core.Analysis;
using GigaReplace.Core.FileSystem;
using GigaReplace.Core.Text;

namespace GigaReplace.Core.Planning;

public sealed class ScannedFile
{
    public required string RelativePath { get; init; }

    public required long Length { get; init; }

    public required DateTime LastWriteUtc { get; init; }

    public required FileReadStatus Status { get; init; }

    public TextFileContent? Content { get; init; }

    /// <summary>SHA-256 of the file's bytes when it was scanned (text files only).</summary>
    public byte[]? Hash { get; init; }

    public string? Error { get; init; }
}

public readonly record struct ScanProgress(string Stage, int Done, int Total);

/// <summary>A line of code around a match, for previews. <see cref="MatchStart"/> is -1 on other lines.</summary>
public sealed record ContextLine(int Number, string Text, int MatchStart, int MatchLength);

/// <summary>Everything a scan found. Decisions live on the <see cref="Items"/> themselves.</summary>
public sealed class ScanResult
{
    private Dictionary<string, List<ChangeItem>>? _bySymbol;
    private Dictionary<string, List<ChangeItem>>? _bySimilarity;
    private Dictionary<string, List<ChangeItem>>? _contentByFile;
    private Dictionary<string, List<ChangeItem>>? _renamesByPath;
    private Dictionary<string, List<ChangeItem>>? _commentMentions;
    private HashSet<string>? _directories;

    public required string RootPath { get; init; }

    /// <summary>A snapshot of the profile the scan ran with.</summary>
    public required ReplaceProfile Profile { get; init; }

    /// <summary>The solution file (relative), if one was chosen or found at the root.</summary>
    public string? SolutionFile { get; init; }

    public required SourceTree Tree { get; init; }

    public required IReadOnlyDictionary<string, ScannedFile> Files { get; init; }

    public required IReadOnlyList<ChangeItem> Items { get; init; }

    public required SolutionIndex Index { get; init; }

    /// <summary>The rules that ran, including generated identity-GUID rules.</summary>
    public required IReadOnlyList<ReplacementRule> Rules { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public TimeSpan Duration { get; init; }

    public int TextFileCount => Files.Values.Count(f => f.Status == FileReadStatus.Text);

    private HashSet<string> DirectorySet => LazyInitializer.EnsureInitialized(
        ref _directories, () => new HashSet<string>(Tree.Directories, StringComparer.OrdinalIgnoreCase));

    private Dictionary<string, List<ChangeItem>> BySymbol => LazyInitializer.EnsureInitialized(
        ref _bySymbol, () => Group(Items.Where(i => i.SymbolKey is not null), i => i.SymbolKey!, StringComparer.Ordinal));

    private Dictionary<string, List<ChangeItem>> BySimilarity => LazyInitializer.EnsureInitialized(
        ref _bySimilarity, () => Group(Items, i => i.SimilarityKey, StringComparer.Ordinal));

    private Dictionary<string, List<ChangeItem>> ContentByFile => LazyInitializer.EnsureInitialized(
        ref _contentByFile, () => Group(Items.Where(i => i.Kind == ChangeKind.Content), i => i.RelativePath, StringComparer.OrdinalIgnoreCase));

    private Dictionary<string, List<ChangeItem>> RenamesByPath => LazyInitializer.EnsureInitialized(
        ref _renamesByPath, () => Group(Items.Where(i => i.IsRename), i => i.RelativePath, StringComparer.OrdinalIgnoreCase));

    public bool IsDirectory(string relativePath) => DirectorySet.Contains(relativePath);

    private Dictionary<string, List<ChangeItem>> CommentMentions => LazyInitializer.EnsureInitialized(
        ref _commentMentions, () => Group(Items.Where(i => i.Kind == ChangeKind.Content && i.Context == MatchContext.Comment), i => i.WordText, StringComparer.Ordinal));

    /// <summary>
    /// Every occurrence of the same code name (a single item when the match isn't a code name).
    /// Comments that mention a compound name, such as “OrderHubService”, count too, so doc
    /// comments and crefs follow the code; a bare word like “OrderHub” in prose doesn't.
    /// </summary>
    public IReadOnlyList<ChangeItem> SameSymbol(ChangeItem item)
    {
        if (item.SymbolKey is null || !BySymbol.TryGetValue(item.SymbolKey, out var list))
        {
            return [item];
        }

        bool compound = !item.SymbolKey.Contains('@') && !string.Equals(item.WordText, item.OriginalText, StringComparison.Ordinal);
        return compound && CommentMentions.TryGetValue(item.WordText, out var mentions) ? [.. list, .. mentions] : list;
    }

    /// <summary>Matches of the same rule and text, in the same kind of place, flagged for the same reasons.</summary>
    public IReadOnlyList<ChangeItem> Similar(ChangeItem item) =>
        BySimilarity.TryGetValue(item.SimilarityKey, out var list) ? list : [item];

    public IReadOnlyList<ChangeItem> ContentItemsOf(string relativePath) =>
        ContentByFile.TryGetValue(relativePath, out var list) ? list : [];

    public IReadOnlyList<ChangeItem> RenamesOf(string relativePath) =>
        RenamesByPath.TryGetValue(relativePath, out var list) ? list : [];

    public IEnumerable<IGrouping<string, ChangeItem>> RenameGroups => RenamesByPath.Select(kv => new Grouping(kv.Key, kv.Value));

    /// <summary>The lines around a text match, with the match marked.</summary>
    public IReadOnlyList<ContextLine> ContextLines(ChangeItem item, int before = 3, int after = 3)
    {
        if (item.Kind != ChangeKind.Content || !Files.TryGetValue(item.RelativePath, out var file) || file.Content is null)
        {
            return [];
        }

        var lines = new LineIndex(file.Content.Text);
        int target = lines.LineOf(item.Offset);
        int first = Math.Max(0, target - before);
        int last = Math.Min(lines.LineCount - 1, target + after);
        var result = new List<ContextLine>();
        for (int line = first; line <= last; line++)
        {
            string text = lines.LineText(line);
            int matchStart = -1, matchLength = 0;
            if (line == target)
            {
                matchStart = item.Offset - lines.LineStart(line);
                matchLength = Math.Min(item.Length, Math.Max(0, text.Length - matchStart));
            }

            result.Add(new ContextLine(line + 1, text, matchStart, matchLength));
        }

        return result;
    }

    private static Dictionary<string, List<ChangeItem>> Group(IEnumerable<ChangeItem> items, Func<ChangeItem, string> key, StringComparer comparer)
    {
        var map = new Dictionary<string, List<ChangeItem>>(comparer);
        foreach (var item in items)
        {
            var k = key(item);
            if (!map.TryGetValue(k, out var list))
            {
                map[k] = list = [];
            }

            list.Add(item);
        }

        return map;
    }

    private sealed class Grouping(string key, List<ChangeItem> items) : IGrouping<string, ChangeItem>
    {
        public string Key => key;

        public IEnumerator<ChangeItem> GetEnumerator() => items.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
