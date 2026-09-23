namespace GigaReplace.Core.Text;

public readonly record struct RawMatch(int Start, int Length, ReplacementRule Rule)
{
    public int End => Start + Length;
}

/// <summary>Finds rule matches. Earlier rules claim text first; claimed text is never matched again.</summary>
public static class RuleMatcher
{
    public static List<RawMatch> FindAll(string text, IReadOnlyList<ReplacementRule> rules)
    {
        var result = new List<RawMatch>();
        if (text.Length == 0)
        {
            return result;
        }

        // Claimed intervals, sorted by start; they never overlap, so they are also sorted by end.
        var claimed = new List<(int Start, int End)>();
        foreach (var rule in rules)
        {
            if (!rule.IsUsable)
            {
                continue;
            }

            var comparison = rule.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int index = 0;
            while (index <= text.Length - rule.Find.Length)
            {
                int found = text.IndexOf(rule.Find, index, comparison);
                if (found < 0)
                {
                    break;
                }

                int end = found + rule.Find.Length;
                int slot = FirstEndingAfter(claimed, found);
                if (slot < claimed.Count && claimed[slot].Start < end)
                {
                    // Overlaps text an earlier rule (or this one) already claimed.
                    index = found + 1;
                    continue;
                }

                claimed.Insert(slot, (found, end));
                result.Add(new RawMatch(found, rule.Find.Length, rule));
                index = end;
            }
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    /// <summary>Index of the first interval whose end is after <paramref name="position"/>.</summary>
    private static int FirstEndingAfter(List<(int Start, int End)> intervals, int position)
    {
        int lo = 0, hi = intervals.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (intervals[mid].End <= position)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
