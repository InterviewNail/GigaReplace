using System.Text.RegularExpressions;

namespace GigaReplace.Core.Planning;

/// <summary>The user's own terms. A line wrapped in slashes (/Secret\w*/) is a regular expression.</summary>
public sealed class WatchList
{
    private readonly List<(string Label, Func<string, bool> Test)> _terms;

    private WatchList(List<(string, Func<string, bool>)> terms) => _terms = terms;

    public static WatchList Empty { get; } = new([]);

    public int Count => _terms.Count;

    public static WatchList Compile(IEnumerable<string> lines)
    {
        var terms = new List<(string, Func<string, bool>)>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Length > 2 && line.StartsWith('/') && line.EndsWith('/'))
            {
                try
                {
                    var regex = new Regex(line[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                    terms.Add((line, text => SafeIsMatch(regex, text)));
                    continue;
                }
                catch (ArgumentException)
                {
                    // Not a valid pattern: fall back to plain text.
                }
            }

            terms.Add((line, text => text.Contains(line, StringComparison.OrdinalIgnoreCase)));
        }

        return new WatchList(terms);
    }

    /// <summary>The first term found in <paramref name="text"/>, or null.</summary>
    public string? FirstHit(string text)
    {
        foreach (var (label, test) in _terms)
        {
            if (test(text))
            {
                return label;
            }
        }

        return null;
    }

    private static bool SafeIsMatch(Regex regex, string text)
    {
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
