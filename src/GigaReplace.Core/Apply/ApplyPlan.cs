using GigaReplace.Core.Planning;
using GigaReplace.Core.Text;

namespace GigaReplace.Core.Apply;

public sealed record ContentWrite(string RelativePath, byte[] NewBytes, byte[] OriginalHash, int ChangeCount);

public sealed record RenameStep(string RelativePath, string NewName, bool IsDirectory);

public readonly record struct ApplyProgress(string Stage, int Done, int Total);

public sealed class ApplyException(string message, IReadOnlyList<string>? details = null, Exception? inner = null) : Exception(message, inner)
{
    public IReadOnlyList<string> Details { get; } = details ?? [];
}

/// <summary>The concrete file operations the approved decisions add up to.</summary>
public sealed class ApplyPlan
{
    public required ScanResult Scan { get; init; }

    public required IReadOnlyList<ContentWrite> ContentWrites { get; init; }

    public required IReadOnlyList<RenameStep> FileRenames { get; init; }

    /// <summary>Deepest folders first, so each rename happens while its parents still have their old names.</summary>
    public required IReadOnlyList<RenameStep> FolderRenames { get; init; }

    public required FinalPathMapper Paths { get; init; }

    public int ChangeCount { get; init; }

    public string SourceRoot => Scan.RootPath;

    public bool IsEmpty => ContentWrites.Count == 0 && FileRenames.Count == 0 && FolderRenames.Count == 0;

    public static ApplyPlan Build(ScanResult scan)
    {
        if (scan.Items.Any(i => i.IsPending))
        {
            throw new ApplyException("Some changes still need a decision.");
        }

        var writes = new List<ContentWrite>();
        foreach (var group in scan.Items.Where(i => i.Kind == ChangeKind.Content && i.WillChange).GroupBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var file = scan.Files[group.Key];
            if (file.Pdf is { } pdf)
            {
                try
                {
                    writes.Add(new ContentWrite(group.Key, pdf.Apply(group), pdf.Hash, group.Count()));
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                    throw new ApplyException($"{group.Key} can't be changed: {ex.Message}");
                }

                continue;
            }

            var content = file.Content ?? throw new ApplyException($"{group.Key} has no text content to change.");
            var newText = TextEdits.ApplyDecisions(content.Text, group);
            if (!content.CanEncode(newText))
            {
                throw new ApplyException($"{group.Key}: the replacement contains characters its {content.EncodingName} encoding can't store.");
            }

            writes.Add(new ContentWrite(group.Key, content.Encode(newText), file.Hash!, group.Count()));
        }

        var mapper = FinalPathMapper.FromDecisions(scan);
        var fileRenames = mapper.NewNames
            .Where(kv => !scan.IsDirectory(kv.Key))
            .Select(kv => new RenameStep(kv.Key, kv.Value, false))
            .OrderBy(r => r.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var folderRenames = mapper.NewNames
            .Where(kv => scan.IsDirectory(kv.Key))
            .Select(kv => new RenameStep(kv.Key, kv.Value, true))
            .OrderByDescending(r => r.RelativePath.Count(c => c == '\\'))
            .ThenBy(r => r.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ApplyPlan
        {
            Scan = scan,
            ContentWrites = writes,
            FileRenames = fileRenames,
            FolderRenames = folderRenames,
            Paths = mapper,
            ChangeCount = scan.Items.Count(i => i.WillChange),
        };
    }

    /// <summary>Problems that mean the tree changed since the scan. Checked before anything is written.</summary>
    internal List<string> FindStaleFiles()
    {
        var issues = new List<string>();
        foreach (var file in Scan.Tree.Files)
        {
            var full = Path.Combine(SourceRoot, file.RelativePath);
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                issues.Add($"{file.RelativePath} no longer exists.");
            }
            else if (info.Length != file.Length || info.LastWriteTimeUtc != file.LastWriteUtc)
            {
                issues.Add($"{file.RelativePath} changed after the scan.");
            }
        }

        foreach (var directory in FolderRenames)
        {
            if (!Directory.Exists(Path.Combine(SourceRoot, directory.RelativePath)))
            {
                issues.Add($"The folder {directory.RelativePath} no longer exists.");
            }
        }

        return issues;
    }
}

public sealed class ApplyResult
{
    public required WriteMode Mode { get; init; }

    /// <summary>The folder that now holds the result (the copy, or the source for in-place runs).</summary>
    public required string OutputRoot { get; init; }

    public int FilesWritten { get; init; }

    public int FilesRenamed { get; init; }

    public int FoldersRenamed { get; init; }

    public int FilesCopied { get; init; }

    public int ChangeCount { get; init; }

    public UndoJournal? Journal { get; init; }

    /// <summary>Full path of the solution (or project) to build, if one is known.</summary>
    public string? BuildTarget { get; init; }

    /// <summary>Things worth knowing, such as folders left behind because another program was using them.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public string Summary => Mode == WriteMode.CopyToFolder
        ? $"Copied {FilesCopied} files to {OutputRoot}: {ChangeCount} changes, {FilesWritten} files edited, {FilesRenamed} files and {FoldersRenamed} folders renamed."
        : $"Changed {OutputRoot} in place: {ChangeCount} changes, {FilesWritten} files edited, {FilesRenamed} files and {FoldersRenamed} folders renamed.";
}
