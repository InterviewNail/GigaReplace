using System.Text.RegularExpressions;
using GigaReplace.Core.Analysis.CSharp;
using GigaReplace.Core.Analysis.Markup;
using GigaReplace.Core.Text;

namespace GigaReplace.Core.Analysis;

internal static class AnalyzerFactory
{
    public static IFileAnalyzer Create(FileAnalysisContext ctx) => ctx.Kind switch
    {
        FileKind.CSharp => new CSharpMatchAnalyzer(ctx),
        FileKind.MsBuild => new MsBuildAnalyzer(ctx),
        FileKind.Xaml => new XamlAnalyzer(ctx),
        FileKind.Solution => new SolutionFileAnalyzer(ctx),
        FileKind.Json => new JsonAnalyzer(ctx),
        var kind when FileKinds.IsXmlLike(kind) => new XmlFileAnalyzer(ctx, kind),
        var kind => new TextAnalyzer(ctx, kind),
    };
}

/// <summary>JSON and JSONC. Keys of configuration files are flagged; values get address/path checks.</summary>
internal sealed class JsonAnalyzer : IFileAnalyzer
{
    private static readonly HashSet<string> NonConfigFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "package-lock.json", "global.json", "launchsettings.json", "jsconfig.json", "tsconfig.json",
        "nuget.json", "dotnet-tools.json", "omnisharp.json", "libman.json", "bundleconfig.json", "compilerconfig.json",
    };

    private readonly FileAnalysisContext _ctx;
    private readonly List<(int Start, int End, bool IsKey)> _strings = [];
    private readonly bool _isConfig;

    public JsonAnalyzer(FileAnalysisContext ctx)
    {
        _ctx = ctx;
        var name = Path.GetFileName(ctx.RelativePath);
        _isConfig = !NonConfigFiles.Contains(name)
            && !name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
            && (name.Contains("settings", StringComparison.OrdinalIgnoreCase) || name.Contains("config", StringComparison.OrdinalIgnoreCase));
        ScanStrings(ctx.Text);
    }

    public MatchAnalysis Analyze(int matchIndex)
    {
        var match = _ctx.Matches[matchIndex];
        var analysis = MatchAnalysis.ForWord(_ctx.Text, match, MatchContext.JsonValue, "JSON");
        var hit = _strings.FirstOrDefault(s => match.Start >= s.Start && match.End <= s.End);
        if (hit == default)
        {
            analysis.Context = MatchContext.Text;
            return analysis;
        }

        string value = _ctx.Text[hit.Start..hit.End];
        int index = match.Start - hit.Start;
        if (hit.IsKey)
        {
            analysis.Context = MatchContext.JsonKey;
            analysis.Detail = _isConfig ? "configuration key" : "JSON key";
            if (_isConfig)
            {
                analysis.Add(RiskCode.ConfigKey, "Configuration key. Code that reads it (GetSection, options binding, indexers) and environment-variable overrides must use the same name.");
            }
        }
        else
        {
            analysis.Detail = _isConfig ? "configuration value" : "JSON value";
            ValueHeuristics.Inspect(value, index, match.Length, analysis, allowPaths: _isConfig);
        }

        return analysis;
    }

    private void ScanStrings(string text)
    {
        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                int end = text.IndexOf('\n', i);
                i = end < 0 ? n : end;
                continue;
            }

            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? n : end + 2;
                continue;
            }

            if (c != '"')
            {
                i++;
                continue;
            }

            int start = i + 1;
            int j = start;
            while (j < n && text[j] != '"')
            {
                j += text[j] == '\\' ? 2 : 1;
            }

            j = Math.Min(j, n);
            int after = j + 1;
            while (after < n && char.IsWhiteSpace(text[after]))
            {
                after++;
            }

            _strings.Add((start, j, after < n && text[after] == ':'));
            i = j + 1;
        }
    }
}

/// <summary>Classic .sln files.</summary>
internal sealed partial class SolutionFileAnalyzer(FileAnalysisContext ctx) : IFileAnalyzer
{
    [GeneratedRegex(@"^Project\(""\{[^}]+\}""\)\s*=\s*""(?<name>[^""]*)""\s*,\s*""(?<path>[^""]*)""", RegexOptions.Multiline)]
    private static partial Regex ProjectLine();

    private readonly List<(int Start, int End, bool IsPath)> _parts = ProjectLine().Matches(ctx.Text)
        .SelectMany(m => new[]
        {
            (m.Groups["name"].Index, m.Groups["name"].Index + m.Groups["name"].Length, false),
            (m.Groups["path"].Index, m.Groups["path"].Index + m.Groups["path"].Length, true),
        })
        .ToList();

    public MatchAnalysis Analyze(int matchIndex)
    {
        var match = ctx.Matches[matchIndex];
        var analysis = MatchAnalysis.ForWord(ctx.Text, match, MatchContext.Text, "solution file");
        foreach (var (start, end, isPath) in _parts)
        {
            if (match.Start >= start && match.End <= end)
            {
                analysis.Detail = isPath ? "project path in the solution" : "project name in the solution";
                break;
            }
        }

        return analysis;
    }
}

/// <summary>Scripts, docs, other languages and anything else that's text.</summary>
internal sealed partial class TextAnalyzer(FileAnalysisContext ctx, FileKind kind) : IFileAnalyzer
{
    [GeneratedRegex(@"(?:^|[\s""'])(?:-p:|/p:|-property:|/property:)$", RegexOptions.IgnoreCase)]
    private static partial Regex MsBuildPropertySwitch();

    public MatchAnalysis Analyze(int matchIndex)
    {
        var match = ctx.Matches[matchIndex];
        switch (kind)
        {
            case FileKind.OtherCode or FileKind.CodeGenSource:
                var language = Path.GetExtension(ctx.RelativePath).TrimStart('.').ToLowerInvariant();
                return MatchAnalysis.ForWord(ctx.Text, match, MatchContext.Code, $"{language} code (not analyzed)", symbol: true);

            case FileKind.Sql:
                var sql = MatchAnalysis.ForWord(ctx.Text, match, MatchContext.Code, "SQL");
                sql.Add(RiskCode.ExternalContract, "SQL script. Table, column and procedure names must match the real database.");
                return sql;

            case FileKind.InstallerScript:
                var installer = MatchAnalysis.ForWord(ctx.Text, match, MatchContext.Text, "installer script");
                installer.Add(RiskCode.InstallerIdentity, "Installer script. Its app ID, install folder, registry keys and shortcuts identify the installed product, so existing installs are tied to the old values.");
                return installer;
        }

        var analysis = MatchAnalysis.ForWord(ctx.Text, match, MatchContext.Text, kind switch
        {
            FileKind.Script => "script",
            FileKind.Markdown => "documentation",
            _ => "text",
        });

        if (kind == FileKind.Script)
        {
            var lineStart = ctx.Text.LastIndexOf('\n', Math.Max(0, analysis.WordStart - 1)) + 1;
            var before = ctx.Text[lineStart..analysis.WordStart];
            if (MsBuildPropertySwitch().IsMatch(before))
            {
                analysis.Detail = "MSBuild property set on the command line";
                analysis.SymbolKey = analysis.Word(ctx.Text);
                return analysis;
            }
        }

        var (tokenStart, tokenLength) = WordShape.ExpandToWord(ctx.Text, match.Start, match.Length, c => !char.IsWhiteSpace(c) && c is not ('"' or '\'' or '(' or ')' or '<' or '>' or '`'));
        ValueHeuristics.Inspect(ctx.Text.Substring(tokenStart, tokenLength), match.Start - tokenStart, match.Length, analysis, allowPaths: false);
        return analysis;
    }
}
