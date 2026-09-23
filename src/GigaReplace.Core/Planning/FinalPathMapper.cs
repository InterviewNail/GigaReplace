using System.Text;
using GigaReplace.Core.Text;

namespace GigaReplace.Core.Planning;

/// <summary>Maps original relative paths to where they end up once approved renames are applied.</summary>
public sealed class FinalPathMapper
{
    private readonly Dictionary<string, string> _newNames;

    private FinalPathMapper(Dictionary<string, string> newNames) => _newNames = newNames;

    /// <summary>Original relative path → its new name (only for items whose name changes).</summary>
    public IReadOnlyDictionary<string, string> NewNames => _newNames;

    public bool HasRenames => _newNames.Count > 0;

    public static FinalPathMapper FromDecisions(ScanResult scan)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in scan.RenameGroups)
        {
            var items = group.ToList();
            if (!items.Any(i => i.WillChange))
            {
                continue;
            }

            string name = items[0].WordText;
            string newName = TextEdits.ApplyDecisions(name, items);
            if (!string.Equals(newName, name, StringComparison.Ordinal))
            {
                names[group.Key] = newName;
            }
        }

        return new FinalPathMapper(names);
    }

    public string? NewNameOf(string relativePath) => _newNames.GetValueOrDefault(relativePath);

    public string Map(string relativePath)
    {
        if (_newNames.Count == 0 || relativePath.Length == 0)
        {
            return relativePath;
        }

        var parts = relativePath.Split('\\');
        var result = new StringBuilder(relativePath.Length + 16);
        var original = new StringBuilder(relativePath.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                original.Append('\\');
                result.Append('\\');
            }

            original.Append(parts[i]);
            result.Append(_newNames.TryGetValue(original.ToString(), out var newName) ? newName : parts[i]);
        }

        return result.ToString();
    }
}
