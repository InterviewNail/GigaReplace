using System.Diagnostics;
using GigaReplace.Core.Apply;
using GigaReplace.Core.Planning;

namespace GigaReplace.Core.Tests;

/// <summary>Renames a real multi-project solution made by `dotnet new`, then builds the copy.</summary>
[Trait("Category", "Integration")]
public class EndToEndTests
{
    [Fact]
    public async Task A_renamed_copy_of_a_real_solution_still_builds()
    {
        using var tree = new TempTree();
        Dotnet(tree.Root, "new", "sln", "-n", "OrderHub");
        Dotnet(tree.Root, "new", "classlib", "-n", "OrderHub.Core", "-o", "src/OrderHub.Core", "-f", "net10.0");
        Dotnet(tree.Root, "new", "console", "-n", "OrderHub.App", "-o", "src/OrderHub.App", "-f", "net10.0");
        var solution = Directory.EnumerateFiles(tree.Root, "OrderHub.sln*").Single();
        Dotnet(tree.Root, "sln", solution, "add", "src/OrderHub.Core/OrderHub.Core.csproj", "src/OrderHub.App/OrderHub.App.csproj");
        Dotnet(tree.Root, "add", "src/OrderHub.App/OrderHub.App.csproj", "reference", "src/OrderHub.Core/OrderHub.Core.csproj");
        File.Delete(tree.Full("src/OrderHub.Core/Class1.cs"));
        tree.Add("src/OrderHub.Core/OrderHubClock.cs", """
            namespace OrderHub.Core;

            public sealed class OrderHubClock
            {
                public const string ProductName = "OrderHub";

                public string Describe(int orderHubTicks) => $"{ProductName}: {orderHubTicks}";
            }
            """);
        tree.Add("src/OrderHub.App/Program.cs", """
            using OrderHub.Core;

            var orderHubClock = new OrderHubClock();
            Console.WriteLine(orderHubClock.Describe(3));
            """);

        var profile = tree.Profile(("OrderHub", "SalesDesk"));
        profile.SourcePath = solution;
        var scan = Scanner.Scan(profile);
        scan.ApproveAll();
        Assert.Empty(PlanValidator.Validate(scan));

        var destination = tree.Root + "-renamed";
        try
        {
            var result = CopyApplier.Apply(ApplyPlan.Build(scan), destination, copyGitFolder: false);
            Assert.NotNull(result.BuildTarget);
            Assert.EndsWith("SalesDesk" + Path.GetExtension(solution), result.BuildTarget);
            Assert.False(Directory.EnumerateFileSystemEntries(destination, "*OrderHub*", SearchOption.AllDirectories).Any());

            var output = new List<string>();
            var build = await DotnetBuildRunner.BuildAsync(result.BuildTarget!, output.Add);

            Assert.True(build.Succeeded, string.Join(Environment.NewLine, output));
            Assert.True(File.Exists(Path.Combine(destination, "src", "SalesDesk.App", "bin", "Debug", "net10.0", "SalesDesk.App.dll")));
        }
        finally
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, true);
            }
        }
    }

    private static void Dotnet(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Wait for the process itself; a leftover child holding the pipes must not hang the test.
        bool finished = process.WaitForExit(TimeSpan.FromMinutes(3));
        Assert.True(finished, $"dotnet {string.Join(' ', args)} timed out:\n{string.Join('\n', output)}");
        Assert.True(process.ExitCode == 0, $"dotnet {string.Join(' ', args)} failed:\n{string.Join('\n', output)}");
    }
}
