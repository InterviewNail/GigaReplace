using System.Text;
using GigaReplace.Core.Planning;

namespace GigaReplace.Core.Tests;

/// <summary>A throwaway folder tree for a test.</summary>
internal sealed class TempTree : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "gigareplace-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public TempTree Add(string relativePath, string content, Encoding? encoding = null)
    {
        var full = Full(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, encoding ?? Utf8NoBom);
        return this;
    }

    public TempTree AddBytes(string relativePath, byte[] bytes)
    {
        var full = Full(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return this;
    }

    public string Full(string relativePath) => Path.Combine(Root, relativePath.Replace('/', '\\'));

    public string Read(string relativePath) => File.ReadAllText(Full(relativePath));

    public ReplaceProfile Profile(params (string Find, string Replace)[] rules) => new()
    {
        SourcePath = Root,
        Rules = rules.Select(r => new ReplacementRule { Find = r.Find, Replace = r.Replace }).ToList(),
    };

    public ScanResult Scan(params (string Find, string Replace)[] rules) => Scanner.Scan(Profile(rules));

    /// <summary>Every file (relative path → bytes), for before/after comparisons.</summary>
    public Dictionary<string, byte[]> Snapshot(string? root = null)
    {
        root ??= Root;
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllBytes, StringComparer.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class ScanExtensions
{
    public static ChangeItem Single(this ScanResult scan, string path, string wordText, ChangeKind kind = ChangeKind.Content) =>
        scan.Items.Single(i => i.RelativePath == path.Replace('/', '\\') && i.WordText == wordText && i.Kind == kind);

    public static IEnumerable<ChangeItem> In(this ScanResult scan, string path) =>
        scan.Items.Where(i => i.RelativePath == path.Replace('/', '\\'));

    public static bool Has(this ChangeItem item, RiskCode code) => item.Risks.Any(r => r.Code == code);

    public static void ApproveAll(this ScanResult scan)
    {
        foreach (var item in scan.Items.Where(i => i.IsPending))
        {
            item.Decision = Decision.Replace;
        }
    }
}
