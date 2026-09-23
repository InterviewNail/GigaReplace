using System.Diagnostics;
using System.Text;
using GigaReplace.Core.Apply;
using GigaReplace.Core.Planning;

namespace GigaReplace.Core.Tests;

public class ApplyTests
{
    private static TempTree SampleTree() => new TempTree()
        .Add("OrderHub.slnx", "<Solution>\n  <Project Path=\"src/OrderHub.Core/OrderHub.Core.csproj\" />\n</Solution>\n")
        .Add("src/OrderHub.Core/OrderHub.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>OrderHub.Core</AssemblyName><RootNamespace>OrderHub.Core</RootNamespace></PropertyGroup></Project>")
        .Add("src/OrderHub.Core/Services/OrderHubService.cs", "namespace OrderHub.Core.Services;\r\n\r\npublic class OrderHubService { }\r\n")
        .Add("src/OrderHub.Core/Readme.txt", "OrderHub notes\n", new UnicodeEncoding(false, true))
        .AddBytes("src/OrderHub.Core/OrderHub.png", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);

    [Fact]
    public void In_place_run_applies_everything_and_undo_restores_it_exactly()
    {
        using var tree = SampleTree();
        var before = tree.Snapshot();
        var journals = new JournalStore(Path.Combine(tree.Root, "..", Path.GetFileName(tree.Root) + "-journals"));
        try
        {
            var scan = tree.Scan(("OrderHub", "SalesDesk"));
            scan.ApproveAll();
            Assert.Empty(PlanValidator.Validate(scan));

            var result = InPlaceApplier.Apply(ApplyPlan.Build(scan), journals);

            Assert.True(File.Exists(tree.Full("SalesDesk.slnx")));
            Assert.True(File.Exists(tree.Full("src/SalesDesk.Core/SalesDesk.Core.csproj")));
            Assert.True(File.Exists(tree.Full("src/SalesDesk.Core/SalesDesk.png")));
            Assert.Equal("namespace SalesDesk.Core.Services;\r\n\r\npublic class SalesDeskService { }\r\n", tree.Read("src/SalesDesk.Core/Services/SalesDeskService.cs"));
            Assert.Contains("src/SalesDesk.Core/SalesDesk.Core.csproj", tree.Read("SalesDesk.slnx"));

            var readme = File.ReadAllBytes(tree.Full("src/SalesDesk.Core/Readme.txt"));
            Assert.Equal(new byte[] { 0xFF, 0xFE }, readme[..2]);
            Assert.Equal("SalesDesk notes\n", Encoding.Unicode.GetString(readme, 2, readme.Length - 2));

            Assert.Equal(JournalState.Applied, result.Journal!.State);
            Assert.True(UndoService.Check(result.Journal).IsClean);

            var undo = UndoService.Undo(result.Journal, overwriteChangedFiles: false);
            Assert.True(undo.Success, string.Join("; ", undo.Errors));

            var after = tree.Snapshot();
            Assert.Equal(before.Keys.Order(), after.Keys.Order());
            foreach (var (path, bytes) in before)
            {
                Assert.Equal(bytes, after[path]);
            }
        }
        finally
        {
            if (Directory.Exists(journals.BaseFolder))
            {
                Directory.Delete(journals.BaseFolder, true);
            }
        }
    }

    [Fact]
    public void Copy_mode_writes_a_renamed_copy_and_leaves_the_source_alone()
    {
        using var tree = SampleTree();
        var before = tree.Snapshot();
        var destination = tree.Root + "-copy";
        try
        {
            var scan = tree.Scan(("OrderHub", "SalesDesk"));
            scan.ApproveAll();

            var result = CopyApplier.Apply(ApplyPlan.Build(scan), destination, copyGitFolder: false);

            var copy = tree.Snapshot(destination);
            Assert.Contains(@"src\SalesDesk.Core\Services\SalesDeskService.cs", copy.Keys);
            Assert.Contains(@"src\SalesDesk.Core\SalesDesk.png", copy.Keys);
            Assert.Equal(before[@"src\OrderHub.Core\OrderHub.png"], copy[@"src\SalesDesk.Core\SalesDesk.png"]);
            Assert.Equal(Path.Combine(destination, "SalesDesk.slnx"), result.BuildTarget);

            var after = tree.Snapshot();
            Assert.Equal(before.Keys.Order(), after.Keys.Order());
        }
        finally
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, true);
            }
        }
    }

    [Fact]
    public void Copy_mode_refuses_a_non_empty_or_nested_destination()
    {
        using var tree = SampleTree();
        var scan = tree.Scan(("OrderHub", "SalesDesk"));
        scan.ApproveAll();
        var plan = ApplyPlan.Build(scan);

        Assert.Throws<ApplyException>(() => CopyApplier.Apply(plan, Path.Combine(tree.Root, "out"), false));
        Assert.Throws<ApplyException>(() => CopyApplier.Apply(plan, tree.Full("src"), false));
    }

    [Fact]
    public void Case_only_renames_work_on_windows()
    {
        using var tree = new TempTree().Add("orderhub.txt", "x").Add("orderhub/a.txt", "y");
        var journals = new JournalStore(tree.Root + "-journals");
        try
        {
            var profile = tree.Profile(("orderhub", "OrderHub"));
            profile.Rules[0].MatchCase = true;
            var scan = Scanner.Scan(profile);
            scan.ApproveAll();

            var result = InPlaceApplier.Apply(ApplyPlan.Build(scan), journals);

            var names = Directory.EnumerateFileSystemEntries(tree.Root).Select(Path.GetFileName).ToList();
            Assert.Contains("OrderHub.txt", names);
            Assert.Contains("OrderHub", names);

            UndoService.Undo(result.Journal!, overwriteChangedFiles: false);
            names = Directory.EnumerateFileSystemEntries(tree.Root).Select(Path.GetFileName).ToList();
            Assert.Contains("orderhub.txt", names);
            Assert.Contains("orderhub", names);
        }
        finally
        {
            if (Directory.Exists(journals.BaseFolder))
            {
                Directory.Delete(journals.BaseFolder, true);
            }
        }
    }

    [Fact]
    public async Task Folder_renames_wait_out_a_brief_lock()
    {
        using var tree = new TempTree().Add("OrderHub/data.txt", "x");
        var locked = new FileStream(tree.Full("OrderHub/data.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Delay(400).ContinueWith(_ => locked.Dispose());

        FileMoves.Move(tree.Full("OrderHub"), tree.Full("SalesDesk"), isDirectory: true);

        await release;
        Assert.True(File.Exists(tree.Full("SalesDesk/data.txt")));
    }

    [Fact]
    public void A_folder_another_program_is_using_is_moved_piece_by_piece_and_undo_puts_it_back()
    {
        using var tree = SampleTree();
        var before = tree.Snapshot();
        var journals = new JournalStore(tree.Root + "-journals");

        // A process whose current folder is inside the tree blocks renaming that folder (like an IDE's build node).
        using var holder = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul")
        {
            WorkingDirectory = tree.Full("src/OrderHub.Core"),
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        try
        {
            var scan = tree.Scan(("OrderHub", "SalesDesk"));
            scan.ApproveAll();

            var result = InPlaceApplier.Apply(ApplyPlan.Build(scan), journals);

            Assert.True(File.Exists(tree.Full("src/SalesDesk.Core/Services/SalesDeskService.cs")));
            Assert.True(FileMoves.IsEmptyDirectory(tree.Full("src/OrderHub.Core")));
            Assert.Contains(result.Notes, n => n.Contains("left behind empty", StringComparison.Ordinal));

            var undo = UndoService.Undo(result.Journal!, overwriteChangedFiles: false);
            Assert.True(undo.Success, string.Join("; ", undo.Errors));
            var after = tree.Snapshot();
            Assert.Equal(before.Keys.Order(), after.Keys.Order());
            foreach (var (path, bytes) in before)
            {
                Assert.Equal(bytes, after[path]);
            }
        }
        finally
        {
            holder.Kill(entireProcessTree: true);
            holder.WaitForExit();
            if (Directory.Exists(journals.BaseFolder))
            {
                Directory.Delete(journals.BaseFolder, true);
            }
        }
    }

    [Fact]
    public void Files_changed_after_the_scan_stop_the_run_before_anything_is_written()
    {
        using var tree = SampleTree();
        var journals = new JournalStore(tree.Root + "-journals");
        var scan = tree.Scan(("OrderHub", "SalesDesk"));
        scan.ApproveAll();
        var plan = ApplyPlan.Build(scan);

        File.AppendAllText(tree.Full("src/OrderHub.Core/Services/OrderHubService.cs"), "// edited\n");
        var before = tree.Snapshot();

        var error = Assert.Throws<ApplyException>(() => InPlaceApplier.Apply(plan, journals));
        Assert.Contains(error.Details, d => d.Contains("changed after the scan"));
        Assert.Equal(before.Keys.Order(), tree.Snapshot().Keys.Order());
    }

    [Fact]
    public void Pending_decisions_block_the_plan()
    {
        using var tree = new TempTree().Add("OrderHub.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var scan = tree.Scan(("OrderHub", "SalesDesk"));
        Assert.Contains(scan.Items, i => i.IsPending);
        Assert.Throws<ApplyException>(() => ApplyPlan.Build(scan));
    }
}
