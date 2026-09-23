using System.Text;

namespace GigaReplace.Core.Text;

/// <summary>The replacement for one match, and whether its casing was a guess.</summary>
public readonly record struct CaseResult(string Text, bool Ambiguous);

/// <summary>
/// Mirrors the casing of a case-insensitive match onto the replacement:
/// OrderHub → SalesDesk also gives orderHub → salesDesk, ORDERHUB → SALESDESK and orderhub → salesdesk.
/// </summary>
public static class CasePreserver
{
    private enum Shape
    {
        NoLetters,
        Upper,
        Lower,
        Capitalized,
        Mixed,
    }

    public static CaseResult Propose(ReplacementRule rule, string matched)
    {
        if (rule.MatchCase || !rule.PreserveCase)
        {
            return new CaseResult(rule.Replace, false);
        }

        return Apply(rule.Find, rule.Replace, matched);
    }

    public static CaseResult Apply(string find, string replace, string matched)
    {
        if (string.Equals(matched, find, StringComparison.Ordinal))
        {
            return new CaseResult(replace, false);
        }

        var shape = ShapeOf(matched);
        switch (shape)
        {
            case Shape.NoLetters:
                return new CaseResult(replace, false);
            case Shape.Upper:
                return new CaseResult(replace.ToUpperInvariant(), false);
            case Shape.Lower:
                return new CaseResult(replace.ToLowerInvariant(), false);
        }

        // Word by word: the match's words take their casing from the matching words of the find text.
        var findWords = WordShape.Words(find);
        var replaceWords = WordShape.Words(replace);
        if (findWords.Count > 0 && findWords.Count == replaceWords.Count)
        {
            var sb = new StringBuilder(replace.Length);
            int last = 0;
            bool resolved = true;
            for (int i = 0; i < findWords.Count; i++)
            {
                var (fs, fl) = findWords[i];
                var (rs, rl) = replaceWords[i];
                string? word = CaseWord(matched.Substring(fs, fl), find.Substring(fs, fl), replace.Substring(rs, rl));
                if (word is null)
                {
                    resolved = false;
                    break;
                }

                sb.Append(replace, last, rs - last).Append(word);
                last = rs + rl;
            }

            if (resolved)
            {
                sb.Append(replace, last, replace.Length - last);
                return new CaseResult(sb.ToString(), false);
            }
        }

        if (shape == Shape.Capitalized)
        {
            return new CaseResult(Capitalize(replace), false);
        }

        if (string.Equals(matched, LowerFirst(find), StringComparison.Ordinal))
        {
            return new CaseResult(LowerFirstWord(replace), false);
        }

        if (string.Equals(matched, UpperFirst(find), StringComparison.Ordinal))
        {
            return new CaseResult(UpperFirst(replace), false);
        }

        return new CaseResult(replace, true);
    }

    private static string? CaseWord(string matched, string find, string replace)
    {
        if (string.Equals(matched, find, StringComparison.Ordinal))
        {
            return replace;
        }

        return ShapeOf(matched) switch
        {
            Shape.NoLetters => replace,
            Shape.Upper => replace.ToUpperInvariant(),
            Shape.Lower => replace.ToLowerInvariant(),
            Shape.Capitalized => Capitalize(replace),
            _ => null,
        };
    }

    private static Shape ShapeOf(string s)
    {
        bool anyLetter = false, anyUpper = false, anyLower = false, firstLetterUpper = false, restLower = true;
        foreach (char c in s)
        {
            if (!char.IsLetter(c))
            {
                continue;
            }

            if (!anyLetter)
            {
                anyLetter = true;
                firstLetterUpper = char.IsUpper(c);
            }
            else if (!char.IsLower(c))
            {
                restLower = false;
            }

            anyUpper |= char.IsUpper(c);
            anyLower |= char.IsLower(c);
        }

        if (!anyLetter)
        {
            return Shape.NoLetters;
        }

        if (anyUpper && !anyLower)
        {
            return Shape.Upper;
        }

        if (anyLower && !anyUpper)
        {
            return Shape.Lower;
        }

        return firstLetterUpper && restLower ? Shape.Capitalized : Shape.Mixed;
    }

    private static string Capitalize(string s)
    {
        int first = IndexOfLetter(s);
        if (first < 0)
        {
            return s;
        }

        return string.Concat(s.AsSpan(0, first), char.ToUpperInvariant(s[first]).ToString(), s[(first + 1)..].ToLowerInvariant());
    }

    private static string UpperFirst(string s)
    {
        int first = IndexOfLetter(s);
        return first < 0 ? s : string.Concat(s.AsSpan(0, first), char.ToUpperInvariant(s[first]).ToString(), s.AsSpan(first + 1));
    }

    private static string LowerFirst(string s)
    {
        int first = IndexOfLetter(s);
        return first < 0 ? s : string.Concat(s.AsSpan(0, first), char.ToLowerInvariant(s[first]).ToString(), s.AsSpan(first + 1));
    }

    /// <summary>camelCase for a PascalCase or ACRONYMWord replacement: SalesDesk → salesDesk, XMLDesk → xmlDesk.</summary>
    private static string LowerFirstWord(string s)
    {
        var words = WordShape.Words(s);
        if (words.Count == 0)
        {
            return s;
        }

        var (start, length) = words[0];
        return string.Concat(s.AsSpan(0, start), s.Substring(start, length).ToLowerInvariant(), s.AsSpan(start + length));
    }

    private static int IndexOfLetter(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsLetter(s[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
