namespace GigaReplace.Core.Text;

/// <summary>
/// Word and "hump" boundaries, as a programmer reads them: OrderHubService is Order|Hub|Service,
/// ORDER_HUB is ORDER|HUB, and XMLParser is XML|Parser.
/// </summary>
public static class WordShape
{
    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>True when a word could start or end between <paramref name="s"/>[pos-1] and s[pos].</summary>
    public static bool IsBoundary(string s, int pos)
    {
        if (pos <= 0 || pos >= s.Length)
        {
            return true;
        }

        char a = s[pos - 1];
        char b = s[pos];
        if (!char.IsLetterOrDigit(a) || !char.IsLetterOrDigit(b))
        {
            return true;
        }

        if (char.IsDigit(a) != char.IsDigit(b))
        {
            return true;
        }

        if (char.IsLower(a) && char.IsUpper(b))
        {
            return true;
        }

        // End of an acronym: "XML|Parser".
        return char.IsUpper(a) && char.IsUpper(b) && pos + 1 < s.Length && char.IsLower(s[pos + 1]);
    }

    /// <summary>
    /// True when the match starts and ends on word boundaries, so it is a whole word or a whole
    /// hump of a compound name. A trailing plural "s"/"es" is tolerated (OrderHubs).
    /// </summary>
    public static bool IsAligned(string s, int start, int length)
    {
        int end = start + length;
        if (length == 0 || !IsBoundary(s, start))
        {
            return false;
        }

        if (IsBoundary(s, end))
        {
            return true;
        }

        return HasPluralSuffix(s, end, "s") || HasPluralSuffix(s, end, "es");
    }

    private static bool HasPluralSuffix(string s, int end, string suffix)
    {
        if (end == 0 || end + suffix.Length > s.Length || !char.IsLetter(s[end - 1]))
        {
            return false;
        }

        if (string.Compare(s, end, suffix, 0, suffix.Length, StringComparison.OrdinalIgnoreCase) != 0)
        {
            return false;
        }

        // The suffix must continue the match's casing: OrderHubs or ORDERHUBS, not ORDERHUBs.
        bool matchUpper = char.IsUpper(s[end - 1]);
        for (int i = 0; i < suffix.Length; i++)
        {
            if (char.IsUpper(s[end + i]) != matchUpper)
            {
                return false;
            }
        }

        return IsBoundary(s, end + suffix.Length);
    }

    /// <summary>Grows a span outward to the whole word around it.</summary>
    public static (int Start, int Length) ExpandToWord(string s, int start, int length, Func<char, bool>? isWordChar = null)
    {
        isWordChar ??= IsWordChar;
        int a = start;
        while (a > 0 && isWordChar(s[a - 1]))
        {
            a--;
        }

        int b = start + length;
        while (b < s.Length && isWordChar(s[b]))
        {
            b++;
        }

        return (a, b - a);
    }

    /// <summary>Splits text into words: runs of letters/digits, cut at humps.</summary>
    public static List<(int Start, int Length)> Words(string s)
    {
        var words = new List<(int, int)>();
        int i = 0;
        while (i < s.Length)
        {
            if (!char.IsLetterOrDigit(s[i]))
            {
                i++;
                continue;
            }

            int start = i++;
            while (i < s.Length && char.IsLetterOrDigit(s[i]) && !IsBoundary(s, i))
            {
                i++;
            }

            words.Add((start, i - start));
        }

        return words;
    }

    /// <summary>The words of a name, lower-cased, for keyword hints ("SettingsFolderPath" → settings, folder, path).</summary>
    public static HashSet<string> LowerWords(string s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (start, length) in Words(s))
        {
            set.Add(s.Substring(start, length).ToLowerInvariant());
        }

        return set;
    }
}
