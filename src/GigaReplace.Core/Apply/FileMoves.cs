namespace GigaReplace.Core.Apply;

internal static class FileMoves
{
    private const int MaxAttempts = 5;

    /// <summary>
    /// Renames a file or folder; case-only renames go through a temporary name. A folder that
    /// another program is using (an IDE's build process or a terminal with it as the current
    /// folder) can't be renamed, but what's inside it can be moved, so its contents move into
    /// a new folder instead. Returns a note when something had to be left behind.
    /// </summary>
    public static string? Move(string from, string to, bool isDirectory)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            var temp = from + ".gr-" + Guid.NewGuid().ToString("N")[..8];
            var first = MoveOnce(from, temp, isDirectory);
            var second = Move(temp, to, isDirectory);
            return Join(first, second);
        }

        if (isDirectory && IsEmptyDirectory(to))
        {
            // Left behind by an earlier move of a folder that was in use: fill it again.
            return MoveContents(from, to);
        }

        if (Path.Exists(to))
        {
            throw new IOException($"“{to}” already exists.");
        }

        return MoveOnce(from, to, isDirectory);
    }

    private static string? MoveOnce(string from, string to, bool isDirectory)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (isDirectory)
                {
                    Directory.Move(from, to);
                }
                else
                {
                    File.Move(from, to);
                }

                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && Path.Exists(from) && !Path.Exists(to))
            {
                if (attempt < MaxAttempts)
                {
                    // Antivirus scanners and indexers briefly hold files that were just written.
                    Thread.Sleep(100 * attempt);
                    continue;
                }

                if (!isDirectory)
                {
                    throw;
                }

                Directory.CreateDirectory(to);
                return MoveContents(from, to);
            }
        }
    }

    private static string? MoveContents(string from, string to)
    {
        string? notes = null;
        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos().ToList())
        {
            notes = Join(notes, Move(entry.FullName, Path.Combine(to, entry.Name), entry is DirectoryInfo));
        }

        try
        {
            Directory.Delete(from);
            return notes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Join(notes, $"“{from}” is in use by another program, so it was left behind empty. Delete it once that program is closed.");
        }
    }

    public static bool IsEmptyDirectory(string path) =>
        Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any();

    /// <summary>True when an entry with exactly this name (including case) exists.</summary>
    public static bool ExistsExactly(string path)
    {
        var parent = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (parent is null || !Directory.Exists(parent))
        {
            return false;
        }

        return new DirectoryInfo(parent).EnumerateFileSystemInfos(name).Any(e => string.Equals(e.Name, name, StringComparison.Ordinal));
    }

    public static void CopyTree(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)));
        }

        foreach (var directory in new DirectoryInfo(sourceDirectory).EnumerateDirectories())
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                CopyTree(directory.FullName, Path.Combine(destinationDirectory, directory.Name));
            }
        }
    }

    private static string? Join(string? a, string? b) => a is null ? b : b is null ? a : a + " " + b;
}
