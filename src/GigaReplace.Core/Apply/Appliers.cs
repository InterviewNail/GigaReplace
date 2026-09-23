using System.Security.Cryptography;

namespace GigaReplace.Core.Apply;

/// <summary>Edits the source tree directly, journaling every step so it can be undone.</summary>
public static class InPlaceApplier
{
    public static ApplyResult Apply(ApplyPlan plan, JournalStore journals, IProgress<ApplyProgress>? progress = null, CancellationToken ct = default)
    {
        var root = plan.SourceRoot;
        var issues = plan.FindStaleFiles();
        foreach (var write in plan.ContentWrites)
        {
            var full = Path.Combine(root, write.RelativePath);
            if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReadOnly) != 0)
            {
                issues.Add($"{write.RelativePath} is read-only.");
            }
            else if (File.Exists(full) && !SHA256.HashData(File.ReadAllBytes(full)).AsSpan().SequenceEqual(write.OriginalHash))
            {
                issues.Add($"{write.RelativePath} changed after the scan.");
            }
        }

        if (issues.Count > 0)
        {
            throw new ApplyException("Nothing was changed. Scan again, because:", issues.Distinct().ToList());
        }

        var journal = journals.Create(root);

        // Record every step (and back up every file) before touching anything.
        foreach (var write in plan.ContentWrites)
        {
            var full = Path.Combine(root, write.RelativePath);
            var backup = journal.StoreBackup(File.ReadAllBytes(full));
            journal.Entries.Add(new JournalEntry
            {
                Kind = JournalEntryKind.Content,
                Path = write.RelativePath,
                FinalPath = plan.Paths.Map(write.RelativePath),
                Backup = backup,
                OriginalHash = Convert.ToHexString(write.OriginalHash),
                NewHash = UndoJournal.HashOf(write.NewBytes),
            });
        }

        foreach (var rename in plan.FileRenames.Concat(plan.FolderRenames))
        {
            var from = Path.Combine(root, rename.RelativePath);
            journal.Entries.Add(new JournalEntry
            {
                Kind = JournalEntryKind.Rename,
                Path = rename.RelativePath,
                FinalPath = plan.Paths.Map(rename.RelativePath),
                From = from,
                To = Path.Combine(Path.GetDirectoryName(from)!, rename.NewName),
                IsDirectory = rename.IsDirectory,
            });
        }

        journal.Summary = $"{plan.ChangeCount} changes: {plan.ContentWrites.Count} files edited, {plan.FileRenames.Count} files and {plan.FolderRenames.Count} folders renamed";
        journal.Save();

        int total = journal.Entries.Count, done = 0;
        JournalEntry? current = null;
        var notes = new List<string>();
        try
        {
            var writes = plan.ContentWrites.ToDictionary(w => w.RelativePath, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in journal.Entries)
            {
                current = entry;
                ct.ThrowIfCancellationRequested();
                if (entry.Kind == JournalEntryKind.Content)
                {
                    File.WriteAllBytes(Path.Combine(root, entry.Path), writes[entry.Path].NewBytes);
                }
                else
                {
                    if (FileMoves.Move(entry.From!, entry.To!, entry.IsDirectory) is { } note)
                    {
                        notes.Add(note);
                    }
                }

                progress?.Report(new ApplyProgress("Applying changes", ++done, total));
            }

            journal.State = JournalState.Applied;
            journal.Save();
        }
        catch (Exception ex)
        {
            var rollback = UndoService.Revert(journal, overwriteChangedFiles: true);
            journal.State = rollback.Success ? JournalState.RolledBack : JournalState.Failed;
            journal.Save();
            var reason = ex is OperationCanceledException ? "Cancelled." : $"Applying failed: {ex.Message}";
            if (current is { Kind: JournalEntryKind.Rename, IsDirectory: true } && ex is IOException or UnauthorizedAccessException)
            {
                reason += " A folder can't be renamed while another program is using it: an IDE with the solution open"
                    + " (Visual Studio, VS Code's C# Dev Kit), a terminal whose current folder is inside it, or a build server"
                    + " (\"dotnet build-server shutdown\" stops those). Close it and try again, or use copy mode.";
            }

            throw rollback.Success
                ? new ApplyException($"{reason} Everything was put back the way it was.", [], ex)
                : new ApplyException($"{reason} Some changes couldn't be put back; the undo journal is in {journal.Folder}.", rollback.Errors, ex);
        }

        return new ApplyResult
        {
            Mode = WriteMode.InPlace,
            OutputRoot = root,
            FilesWritten = plan.ContentWrites.Count,
            FilesRenamed = plan.FileRenames.Count,
            FoldersRenamed = plan.FolderRenames.Count,
            ChangeCount = plan.ChangeCount,
            Journal = journal,
            BuildTarget = BuildTargetOf(plan, root),
            Notes = notes,
        };
    }

    internal static string? BuildTargetOf(ApplyPlan plan, string outputRoot) =>
        plan.Scan.SolutionFile is { } solution ? Path.Combine(outputRoot, plan.Paths.Map(solution)) : null;
}

/// <summary>Writes the result into a new, empty folder. The source is never modified.</summary>
public static class CopyApplier
{
    public static ApplyResult Apply(ApplyPlan plan, string destination, bool copyGitFolder, IProgress<ApplyProgress>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new ApplyException("Choose a destination folder for the copy.");
        }

        var source = Path.GetFullPath(plan.SourceRoot).TrimEnd('\\');
        destination = Path.GetFullPath(destination.Trim().Trim('"')).TrimEnd('\\');
        if (string.Equals(destination, source, StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApplyException("The destination can't be the source folder or a folder inside it.");
        }

        if (File.Exists(destination))
        {
            throw new ApplyException("The destination is a file, not a folder.");
        }

        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new ApplyException("The destination folder must be empty (or not exist yet).");
        }

        var issues = plan.FindStaleFiles();
        if (issues.Count > 0)
        {
            throw new ApplyException("Nothing was copied. Scan again, because:", issues);
        }

        var writes = plan.ContentWrites.ToDictionary(w => w.RelativePath, StringComparer.OrdinalIgnoreCase);
        var tree = plan.Scan.Tree;
        int total = tree.Files.Count, done = 0;

        Directory.CreateDirectory(destination);
        foreach (var directory in tree.Directories)
        {
            Directory.CreateDirectory(Path.Combine(destination, plan.Paths.Map(directory)));
        }

        foreach (var file in tree.Files)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, plan.Paths.Map(file.RelativePath));
            if (writes.TryGetValue(file.RelativePath, out var write))
            {
                File.WriteAllBytes(target, write.NewBytes);
            }
            else
            {
                File.Copy(Path.Combine(source, file.RelativePath), target);
            }

            progress?.Report(new ApplyProgress("Copying files", ++done, total));
        }

        var git = Path.Combine(source, ".git");
        if (copyGitFolder)
        {
            if (Directory.Exists(git))
            {
                progress?.Report(new ApplyProgress("Copying .git", done, total));
                FileMoves.CopyTree(git, Path.Combine(destination, ".git"));
            }
            else if (File.Exists(git))
            {
                File.Copy(git, Path.Combine(destination, ".git"));
            }
        }

        return new ApplyResult
        {
            Mode = WriteMode.CopyToFolder,
            OutputRoot = destination,
            FilesWritten = plan.ContentWrites.Count,
            FilesRenamed = plan.FileRenames.Count,
            FoldersRenamed = plan.FolderRenames.Count,
            FilesCopied = tree.Files.Count,
            ChangeCount = plan.ChangeCount,
            BuildTarget = InPlaceApplier.BuildTargetOf(plan, destination),
        };
    }
}
