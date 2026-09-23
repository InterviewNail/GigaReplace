using System.Text;
using System.Text.RegularExpressions;

namespace GigaReplace.Core.FileSystem;

public enum IgnoreVerdict
{
    None,
    Ignored,
    Included,
}

/// <summary>
/// One set of ignore patterns in .gitignore syntax, anchored at a directory. Matching is
/// case-insensitive, as git does on Windows. Paths use '/' separators.
/// </summary>
public sealed class IgnoreRules
{
    private readonly List<Pattern> _patterns;

    // Exactly one of these is set: the rules' directory relative to the scan root, or the scan
    // root relative to the rules' directory (for .gitignore files above the scan root).
    private readonly string? _baseFromRoot;
    private readonly string? _rootFromBase;

    private IgnoreRules(List<Pattern> patterns, string? baseFromRoot, string? rootFromBase)
    {
        _patterns = patterns;
        _baseFromRoot = baseFromRoot;
        _rootFromBase = rootFromBase;
    }

    public int Count => _patterns.Count;

    /// <summary>Rules whose directory is at or below the scan root ("" is the root itself).</summary>
    public static IgnoreRules AtRootRelative(string baseFromRoot, IEnumerable<string> lines) =>
        new(Parse(lines), Normalize(baseFromRoot), null);

    /// <summary>Rules from a directory above the scan root (a parent repository's .gitignore).</summary>
    public static IgnoreRules AboveRoot(string rootFromBase, IEnumerable<string> lines) =>
        new(Parse(lines), null, Normalize(rootFromBase));

    public IgnoreVerdict Evaluate(string pathFromRoot, bool isDirectory)
    {
        var path = ToBaseRelative(pathFromRoot);
        if (path is null || path.Length == 0)
        {
            return IgnoreVerdict.None;
        }

        string basename = path[(path.LastIndexOf('/') + 1)..];
        for (int i = _patterns.Count - 1; i >= 0; i--)
        {
            var p = _patterns[i];
            if (p.DirectoryOnly && !isDirectory)
            {
                continue;
            }

            bool hit = p.BasenameOnly ? p.Regex.IsMatch(basename) : p.Regex.IsMatch(path);
            if (hit)
            {
                return p.Negated ? IgnoreVerdict.Included : IgnoreVerdict.Ignored;
            }
        }

        return IgnoreVerdict.None;
    }

    private string? ToBaseRelative(string pathFromRoot)
    {
        if (_rootFromBase is not null)
        {
            return _rootFromBase.Length == 0 ? pathFromRoot : _rootFromBase + "/" + pathFromRoot;
        }

        var baseDir = _baseFromRoot!;
        if (baseDir.Length == 0)
        {
            return pathFromRoot;
        }

        if (pathFromRoot.Length > baseDir.Length
            && pathFromRoot.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)
            && pathFromRoot[baseDir.Length] == '/')
        {
            return pathFromRoot[(baseDir.Length + 1)..];
        }

        return null;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private static List<Pattern> Parse(IEnumerable<string> lines)
    {
        var list = new List<Pattern>();
        foreach (var raw in lines)
        {
            var pattern = ParseLine(raw);
            if (pattern is not null)
            {
                list.Add(pattern);
            }
        }

        return list;
    }

    private static Pattern? ParseLine(string line)
    {
        line = line.TrimEnd('\r', '\n');
        if (line.Length == 0 || line[0] == '#')
        {
            return null;
        }

        // Trailing spaces are dropped unless escaped with a backslash.
        int end = line.Length;
        while (end > 0 && line[end - 1] == ' ' && !(end >= 2 && line[end - 2] == '\\'))
        {
            end--;
        }

        line = line[..end];
        bool negated = false;
        if (line.StartsWith('!'))
        {
            negated = true;
            line = line[1..];
        }
        else if (line.StartsWith("\\!", StringComparison.Ordinal) || line.StartsWith("\\#", StringComparison.Ordinal))
        {
            line = line[1..];
        }

        bool directoryOnly = line.EndsWith('/');
        line = line.TrimEnd('/');
        if (line.Length == 0)
        {
            return null;
        }

        // A slash anywhere but the end anchors the pattern to the rules' directory.
        bool anchored = line.Contains('/');
        line = line.TrimStart('/');
        if (line.Length == 0)
        {
            return null;
        }

        var regex = new Regex("^" + GlobToRegex(line) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return new Pattern(regex, negated, directoryOnly, BasenameOnly: !anchored);
    }

    internal static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < glob.Length)
        {
            char c = glob[i];
            if (c == '*')
            {
                bool isDouble = i + 1 < glob.Length && glob[i + 1] == '*';
                if (isDouble)
                {
                    bool atStart = i == 0;
                    bool afterSlash = i > 0 && glob[i - 1] == '/';
                    bool beforeSlash = i + 2 < glob.Length && glob[i + 2] == '/';
                    bool atEnd = i + 2 == glob.Length;
                    if ((atStart || afterSlash) && beforeSlash)
                    {
                        sb.Append("(?:.*/)?");
                        i += 3;
                        continue;
                    }

                    if ((atStart || afterSlash) && atEnd)
                    {
                        sb.Append(".*");
                        i += 2;
                        continue;
                    }

                    sb.Append("[^/]*");
                    i += 2;
                    continue;
                }

                sb.Append("[^/]*");
                i++;
                continue;
            }

            if (c == '?')
            {
                sb.Append("[^/]");
                i++;
                continue;
            }

            if (c == '[' && i + 2 <= glob.Length)
            {
                int close = glob.IndexOf(']', i + 2);
                if (close > i)
                {
                    var body = glob.Substring(i + 1, close - i - 1);
                    if (body.StartsWith('!'))
                    {
                        body = "^" + body[1..];
                    }

                    sb.Append('[').Append(body.Replace("\\", "\\\\")).Append(']');
                    i = close + 1;
                    continue;
                }
            }

            if (c == '\\' && i + 1 < glob.Length)
            {
                sb.Append(Regex.Escape(glob[i + 1].ToString()));
                i += 2;
                continue;
            }

            sb.Append(Regex.Escape(c.ToString()));
            i++;
        }

        return sb.ToString();
    }

    private sealed record Pattern(Regex Regex, bool Negated, bool DirectoryOnly, bool BasenameOnly);
}
