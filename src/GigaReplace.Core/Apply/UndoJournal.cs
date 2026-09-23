using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GigaReplace.Core.Apply;

public enum JournalState
{
    InProgress,
    Applied,
    RolledBack,
    Undone,
    PartiallyUndone,
    Failed,
}

public enum JournalEntryKind
{
    Content,
    Rename,
}

public sealed class JournalEntry
{
    public JournalEntryKind Kind { get; set; }

    /// <summary>Original relative path.</summary>
    public string Path { get; set; } = "";

    /// <summary>Where the item is once the whole run is done, after every rename (relative).</summary>
    public string? FinalPath { get; set; }

    public string? Backup { get; set; }

    public string? OriginalHash { get; set; }

    public string? NewHash { get; set; }

    /// <summary>Rename entries: full paths at the moment of the rename.</summary>
    public string? From { get; set; }

    public string? To { get; set; }

    public bool IsDirectory { get; set; }
}

/// <summary>
/// A record of one in-place run: every write (with a backup of the original bytes) and every
/// rename, in order. It's written before anything changes, so a run can always be undone.
/// </summary>
public sealed class UndoJournal
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Id { get; set; } = "";

    public string SourceRoot { get; set; } = "";

    public DateTime CreatedUtc { get; set; }

    public JournalState State { get; set; }

    public string Summary { get; set; } = "";

    public List<JournalEntry> Entries { get; set; } = [];

    [JsonIgnore]
    public string Folder { get; set; } = "";

    public int ContentCount => Entries.Count(e => e.Kind == JournalEntryKind.Content);

    public int RenameCount => Entries.Count(e => e.Kind == JournalEntryKind.Rename);

    /// <summary>Anything that may have left changes behind, including a run interrupted by a crash.</summary>
    public bool CanUndo => State is JournalState.Applied or JournalState.PartiallyUndone or JournalState.Failed or JournalState.InProgress;

    internal string StoreBackup(byte[] bytes)
    {
        var name = $"{Entries.Count:D6}.bin";
        File.WriteAllBytes(System.IO.Path.Combine(Folder, "backup", name), bytes);
        return name;
    }

    internal byte[] ReadBackup(string name) => File.ReadAllBytes(System.IO.Path.Combine(Folder, "backup", name));

    public void Save()
    {
        var target = System.IO.Path.Combine(Folder, "journal.json");
        var temp = target + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, target, overwrite: true);
    }

    internal static UndoJournal? Load(string folder)
    {
        try
        {
            var journal = JsonSerializer.Deserialize<UndoJournal>(File.ReadAllText(System.IO.Path.Combine(folder, "journal.json")), Json);
            if (journal is not null)
            {
                journal.Folder = folder;
            }

            return journal;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string HashOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static string HashOfFile(string path) => HashOf(File.ReadAllBytes(path));
}

/// <summary>Where undo journals live: %LOCALAPPDATA%\GigaReplace\Journals by default.</summary>
public sealed class JournalStore(string? baseFolder = null)
{
    public string BaseFolder { get; } = baseFolder
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GigaReplace", "Journals");

    public UndoJournal Create(string sourceRoot)
    {
        var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24];
        var folder = Path.Combine(BaseFolder, id);
        Directory.CreateDirectory(Path.Combine(folder, "backup"));
        return new UndoJournal
        {
            Id = id,
            SourceRoot = sourceRoot,
            CreatedUtc = DateTime.UtcNow,
            State = JournalState.InProgress,
            Folder = folder,
        };
    }

    public IReadOnlyList<UndoJournal> List()
    {
        if (!Directory.Exists(BaseFolder))
        {
            return [];
        }

        return Directory.EnumerateDirectories(BaseFolder)
            .Select(UndoJournal.Load)
            .OfType<UndoJournal>()
            .OrderByDescending(j => j.CreatedUtc)
            .ToList();
    }
}

public sealed record UndoCheck(IReadOnlyList<string> ChangedSinceApply, IReadOnlyList<string> Missing)
{
    public bool IsClean => ChangedSinceApply.Count == 0 && Missing.Count == 0;
}

public sealed record UndoOutcome(IReadOnlyList<string> Errors, int Restored)
{
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// Reverses an in-place run. It inspects the disk for each step instead of trusting the
/// journal's bookkeeping, so it also works after a crash halfway through.
/// </summary>
public static class UndoService
{
    public static UndoCheck Check(UndoJournal journal)
    {
        var changed = new List<string>();
        var missing = new List<string>();
        foreach (var entry in journal.Entries)
        {
            if (entry.Kind == JournalEntryKind.Content)
            {
                var full = Path.Combine(journal.SourceRoot, entry.FinalPath ?? entry.Path);
                if (!File.Exists(full))
                {
                    missing.Add(entry.FinalPath ?? entry.Path);
                    continue;
                }

                var hash = UndoJournal.HashOfFile(full);
                if (hash != entry.NewHash && hash != entry.OriginalHash)
                {
                    changed.Add(entry.FinalPath ?? entry.Path);
                }
            }
            else if (!Path.Exists(Path.Combine(journal.SourceRoot, entry.FinalPath ?? entry.Path))
                && !Path.Exists(Path.Combine(journal.SourceRoot, entry.Path)))
            {
                // Checked at its final location: a later folder rename may have moved it again.
                missing.Add(entry.Path);
            }
        }

        return new UndoCheck(changed, missing);
    }

    /// <param name="overwriteChangedFiles">Restore files even if they were edited after the run.</param>
    public static UndoOutcome Revert(UndoJournal journal, bool overwriteChangedFiles)
    {
        var errors = new List<string>();
        int restored = 0;
        for (int i = journal.Entries.Count - 1; i >= 0; i--)
        {
            var entry = journal.Entries[i];
            try
            {
                if (entry.Kind == JournalEntryKind.Rename)
                {
                    if (FileMoves.ExistsExactly(entry.To!)
                        && (!FileMoves.ExistsExactly(entry.From!) || (entry.IsDirectory && FileMoves.IsEmptyDirectory(entry.From!))))
                    {
                        FileMoves.Move(entry.To!, entry.From!, entry.IsDirectory);
                        restored++;
                    }
                    else if (!Path.Exists(entry.From!))
                    {
                        errors.Add($"{entry.Path}: can't find it under its new or old name.");
                    }
                }
                else
                {
                    var full = Path.Combine(journal.SourceRoot, entry.Path);
                    if (!File.Exists(full))
                    {
                        errors.Add($"{entry.Path}: the file is missing.");
                        continue;
                    }

                    var hash = UndoJournal.HashOfFile(full);
                    if (hash == entry.OriginalHash)
                    {
                        continue;
                    }

                    if (hash != entry.NewHash && !overwriteChangedFiles)
                    {
                        errors.Add($"{entry.Path}: edited after the run, so it was left as is.");
                        continue;
                    }

                    File.WriteAllBytes(full, journal.ReadBackup(entry.Backup!));
                    restored++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{entry.Path}: {ex.Message}");
            }
        }

        return new UndoOutcome(errors, restored);
    }

    public static UndoOutcome Undo(UndoJournal journal, bool overwriteChangedFiles)
    {
        var outcome = Revert(journal, overwriteChangedFiles);
        journal.State = outcome.Success ? JournalState.Undone : JournalState.PartiallyUndone;
        journal.Save();
        return outcome;
    }
}
