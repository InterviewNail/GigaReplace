namespace GigaReplace.Core.FileSystem;

public static class PathNames
{
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars()
        .Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*'])
        .Distinct()
        .ToArray();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Why a file or folder name can't be used on Windows, or null if it's fine.</summary>
    public static string? Problem(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "the name would be empty";
        }

        if (name is "." or "..")
        {
            return $"“{name}” isn't a usable name";
        }

        int bad = name.IndexOfAny(InvalidNameChars);
        if (bad >= 0)
        {
            return $"“{name[bad]}” isn't allowed in file names";
        }

        if (name.EndsWith('.') || name.EndsWith(' '))
        {
            return "Windows doesn't allow names ending in a dot or space";
        }

        var stem = name.Split('.')[0];
        if (ReservedNames.Contains(stem))
        {
            return $"“{stem}” is a reserved device name on Windows";
        }

        return null;
    }

    public static string ParentOf(string relativePath)
    {
        int slash = relativePath.LastIndexOf('\\');
        return slash < 0 ? "" : relativePath[..slash];
    }

    public static string NameOf(string relativePath)
    {
        int slash = relativePath.LastIndexOf('\\');
        return slash < 0 ? relativePath : relativePath[(slash + 1)..];
    }

    public static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "\\" + name;

    /// <summary>
    /// Resolves a reference (like "..\Lib\Lib.csproj" or "Assets/icon.png") against a folder,
    /// all relative to the root. Returns null when it leaves the root.
    /// </summary>
    public static string? Resolve(string baseDirectory, string reference)
    {
        var parts = new List<string>();
        if (baseDirectory.Length > 0)
        {
            parts.AddRange(baseDirectory.Split('\\'));
        }

        foreach (var segment in reference.Replace('/', '\\').Split('\\'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(segment);
        }

        return string.Join('\\', parts);
    }
}
