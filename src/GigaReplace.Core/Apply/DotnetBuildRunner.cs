using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace GigaReplace.Core.Apply;

public sealed record BuildDiagnostic(bool IsError, string Code, string Message, string? File, int Line);

public sealed class BuildOutcome
{
    public required int ExitCode { get; init; }

    public required IReadOnlyList<BuildDiagnostic> Diagnostics { get; init; }

    public required TimeSpan Duration { get; init; }

    public bool Succeeded => ExitCode == 0;

    public int ErrorCount => Diagnostics.Count(d => d.IsError);

    public int WarningCount => Diagnostics.Count(d => !d.IsError);
}

/// <summary>Runs <c>dotnet build</c> on the result so problems the analysis missed show up.</summary>
public static partial class DotnetBuildRunner
{
    [GeneratedRegex(@"^\s*(?:(?<file>.+?)(?:\((?<line>\d+)(?:,\d+)*\))?\s*:\s*)?(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?\s*$")]
    private static partial Regex DiagnosticLine();

    /// <summary>The solution or project to build in <paramref name="root"/>.</summary>
    public static string? FindBuildTarget(string root, string? preferred)
    {
        if (preferred is not null && File.Exists(preferred))
        {
            return preferred;
        }

        if (!Directory.Exists(root))
        {
            return null;
        }

        foreach (var pattern in new[] { "*.slnx", "*.sln", "*.csproj", "*.vbproj", "*.fsproj" })
        {
            var found = Directory.EnumerateFiles(root, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    public static async Task<BuildOutcome> BuildAsync(string target, Action<string> output, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(target) ?? Environment.CurrentDirectory,
        };
        foreach (var arg in new[] { "build", target, "-nologo", "-v:minimal", "-tl:off", "-clp:NoSummary", "-nodeReuse:false" })
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        // Long-lived helper processes (reused MSBuild nodes, the MSBuild server) can inherit the
        // output pipes and keep them open long after the build ends.
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        var diagnostics = new List<BuildDiagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var gate = new object();
        void OnLine(string line)
        {
            lock (gate)
            {
                output(line);
                var m = DiagnosticLine().Match(line);
                if (m.Success && seen.Add(line.Trim()))
                {
                    diagnostics.Add(new BuildDiagnostic(
                        m.Groups["sev"].Value == "error",
                        m.Groups["code"].Value,
                        m.Groups["msg"].Value,
                        m.Groups["file"].Success ? m.Groups["file"].Value.Trim() : null,
                        int.TryParse(m.Groups["line"].Value, out var n) ? n : 0));
                }
            }
        }

        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutClosed.TrySetResult();
            }
            else
            {
                OnLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrClosed.TrySetResult();
            }
            else
            {
                OnLine(e.Data);
            }
        };
        process.Exited += (_, _) => exited.TrySetResult();

        if (!process.Start())
        {
            throw new InvalidOperationException("Couldn't start dotnet.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await exited.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw;
        }

        // Output can trail the exit slightly; don't wait on a pipe some leftover process still holds.
        await Task.WhenAny(Task.WhenAll(stdoutClosed.Task, stderrClosed.Task), Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None));

        List<BuildDiagnostic> snapshot;
        lock (gate)
        {
            snapshot = [.. diagnostics];
        }

        return new BuildOutcome { ExitCode = process.ExitCode, Diagnostics = snapshot, Duration = clock.Elapsed };
    }
}
