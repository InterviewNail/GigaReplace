using System.ComponentModel;
using System.Diagnostics;

namespace GigaReplace.Services;

public static class GitStatus
{
    /// <summary>True if the folder is in a git repository with uncommitted changes; null if unknown (no git, not a repo).</summary>
    public static async Task<bool?> HasUncommittedChangesAsync(string folder)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "-C", folder, "status", "--porcelain" })
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? (await output).Trim().Length > 0 : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }
}
