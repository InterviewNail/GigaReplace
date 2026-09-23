using System.Text.RegularExpressions;

namespace GigaReplace.Core.Analysis;

/// <summary>Recognizes values that live outside the code: addresses, registry paths, pipe names, file paths ….</summary>
internal static partial class ValueHeuristics
{
    private static readonly HashSet<string> TopLevelDomains = new(StringComparer.Ordinal)
    {
        "com", "net", "org", "io", "dev", "app", "ai", "co", "us", "uk", "de", "eu", "info", "biz", "cloud", "tech",
        "me", "tv", "xyz", "site", "online", "store", "shop", "blog", "gg", "sh", "ly", "fm", "ca", "au", "in",
        "jp", "fr", "nl", "se", "no", "ch", "es", "it", "pl", "ru", "br", "mx", "nz", "za", "cn", "hk", "sg", "kr",
        "tw", "edu", "gov", "email", "pro", "page", "link", "live", "news", "space", "software", "services",
        "studio", "digital", "network", "zone", "world", "today", "top", "one", "cc", "ws", "ms", "at", "be", "dk",
        "fi", "ie", "pt", "cz", "gr", "hu", "ro", "tr", "ua", "il", "ae", "vn", "ar", "cl", "local", "internal",
    };

    private static readonly HashSet<string> DataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "jsonl", "xml", "db", "sqlite", "sqlite3", "log", "txt", "dat", "config", "ini", "settings",
        "bin", "lock", "key", "pem", "cache", "csv", "yaml", "yml", "ldb", "mdb", "accdb", "bak", "tmp", "exe", "dll",
    };

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+.\-]*://[^\s""'<>]+")]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"[\w.+\-]+@[\w\-]+(?:\.[\w\-]+)+")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<![\w.\-])(?:[A-Za-z0-9\-]+\.)+(?<tld>[a-z]{2,})(?![\w\-])")]
    private static partial Regex DomainRegex();

    [GeneratedRegex(@"^[@$u8""]*(Global|Local)\\", RegexOptions.IgnoreCase)]
    private static partial Regex KernelObjectRegex();

    [GeneratedRegex(@"^\W*[A-Za-z_]\w*(?:\.\w+)+(?:`\d+)?\s*,\s*[A-Za-z_][\w.]*\W*$")]
    private static partial Regex AssemblyQualifiedRegex();

    /// <summary>
    /// Adds at most one finding describing <paramref name="value"/>, where the match sits at
    /// <paramref name="index"/>. Paths are only considered when <paramref name="allowPaths"/> is set.
    /// </summary>
    public static void Inspect(string value, int index, int length, MatchAnalysis analysis, bool allowPaths)
    {
        if (index < 0 || index + length > value.Length)
        {
            return;
        }

        if (IsInUrlOrDomain(value, index, length))
        {
            analysis.Add(RiskCode.UrlOrDomain, "Part of a web address or e-mail domain. The old address may still be the live one, and other systems and links point to it.");
        }
        else if (LooksLikeRegistryPath(value))
        {
            analysis.Add(RiskCode.RegistryKey, "Registry path. Settings already stored under the old key won't be read after the rename.");
        }
        else if (KernelObjectRegex().IsMatch(value))
        {
            analysis.Add(RiskCode.IpcName, "Named mutex, event or pipe (Global\\ / Local\\). Old and new builds won't see each other, which affects single-instance checks.");
        }
        else if (IsInsideEnvironmentReference(value, index, length))
        {
            analysis.Add(RiskCode.EnvironmentVariable, "Environment variable reference. Machines and scripts that define the old name won't match.");
        }
        else if (IsPackUriAssembly(value, index))
        {
            analysis.Add(RiskCode.AssemblyReference, "Pack URI naming an assembly. It must match that assembly's final name.");
        }
        else if (LooksLikeConnectionString(value))
        {
            analysis.Add(RiskCode.ExternalContract, "Part of a connection string. The server or database it names has to exist under the new name.");
        }
        else if (AssemblyQualifiedRegex().IsMatch(value))
        {
            analysis.Add(RiskCode.RuntimeName, "Looks like a type or assembly name resolved at run time. It must match the renamed code exactly.");
        }
        else if (allowPaths && LooksLikePath(value, index, length))
        {
            analysis.Add(RiskCode.FileSystemName, "Looks like a file or folder path. If it names a file in this tree, rename that file the same way; if the app creates it at run time, existing installs still use the old name.");
        }
    }

    public static bool IsInUrlOrDomain(string value, int index, int length)
    {
        int end = index + length;
        foreach (Match m in UrlRegex().Matches(value))
        {
            int schemeEnd = value.IndexOf("://", m.Index, StringComparison.Ordinal) + 3;
            if (index >= schemeEnd && end <= m.Index + m.Length)
            {
                return true;
            }
        }

        foreach (Match m in EmailRegex().Matches(value))
        {
            if (index >= m.Index && end <= m.Index + m.Length)
            {
                return true;
            }
        }

        foreach (Match m in DomainRegex().Matches(value))
        {
            if (index >= m.Index && end <= m.Index + m.Length && TopLevelDomains.Contains(m.Groups["tld"].Value))
            {
                return true;
            }
        }

        return false;
    }

    public static bool LooksLikeRegistryPath(string value) =>
        value.Contains("HKEY_", StringComparison.OrdinalIgnoreCase)
        || value.Contains(@"HKLM\", StringComparison.OrdinalIgnoreCase)
        || value.Contains(@"HKCU\", StringComparison.OrdinalIgnoreCase)
        || value.Contains("HKLM:", StringComparison.OrdinalIgnoreCase)
        || value.Contains("HKCU:", StringComparison.OrdinalIgnoreCase)
        || value.Contains(@"Software\", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] ConnectionStringKeys =
    [
        "Database=", "Initial Catalog=", "Data Source=", "Server=", "Host=", "AccountName=", "Endpoint=", "mongodb://", "Uid=", "User ID=",
    ];

    public static bool LooksLikeConnectionString(string value) =>
        value.Contains(';') && ConnectionStringKeys.Any(k => value.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static bool IsInsideEnvironmentReference(string value, int index, int length)
    {
        int end = index + length;
        int open = index - 1;
        while (open >= 0 && IsEnvChar(value[open]))
        {
            open--;
        }

        int close = end;
        while (close < value.Length && IsEnvChar(value[close]))
        {
            close++;
        }

        if (open >= 0 && close < value.Length && value[open] == '%' && value[close] == '%')
        {
            return true;
        }

        return open >= 4 && value.AsSpan(0, open + 1).EndsWith("$env:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEnvChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsPackUriAssembly(string value, int index)
    {
        int component = value.IndexOf(";component", StringComparison.OrdinalIgnoreCase);
        if (component < 0 || index >= component)
        {
            return false;
        }

        int slash = value.LastIndexOf('/', component);
        return index > slash;
    }

    private static bool LooksLikePath(string value, int index, int length)
    {
        if (value.Contains('\\') || value.Contains('/'))
        {
            return true;
        }

        // "orderhub.db", "OrderHub.settings.json"
        int end = index + length;
        int dot = value.IndexOf('.', end);
        if (dot >= 0)
        {
            int extEnd = dot + 1;
            while (extEnd < value.Length && char.IsLetterOrDigit(value[extEnd]))
            {
                extEnd++;
            }

            var lastDot = value.LastIndexOf('.', extEnd - 1);
            var ext = value[(lastDot + 1)..extEnd];
            if (DataExtensions.Contains(ext))
            {
                return true;
            }
        }

        return false;
    }
}
