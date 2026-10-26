using GigaReplace.Core.Apply;
using GigaReplace.Core.Memory;
using GigaReplace.Core.Planning;

namespace GigaReplace.Core.Tests;

public sealed class MemoryTests : IDisposable
{
    private readonly string _memoryFolder = Path.Combine(Path.GetTempPath(), "gigareplace-tests", "memory-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _previousFolder = DecisionMemoryStore.Folder;

    public MemoryTests() => DecisionMemoryStore.Folder = _memoryFolder;

    public void Dispose()
    {
        DecisionMemoryStore.Folder = _previousFolder;
        if (Directory.Exists(_memoryFolder))
        {
            Directory.Delete(_memoryFolder, true);
        }
    }

    // "nail" inside "Thumbnail" is a flagged mid-word match; the bare "nail" is approved automatically.
    private const string Notes = "Thumbnail size\nnail it down\n";

    private static (ScanResult Scan, RecallSummary Recall) ScanWithMemory(TempTree tree, params (string, string)[] rules)
    {
        var scan = tree.Scan(rules);
        var recall = DecisionRecall.Apply(scan, DecisionMemoryStore.Load(scan.RootPath));
        return (scan, recall);
    }

    private static void Remember(ScanResult scan, params ChangeItem[] likeThis)
    {
        var memory = DecisionMemoryStore.Load(scan.RootPath);
        DecisionRecall.Record(scan, memory, likeThis, []);
        DecisionMemoryStore.Save(memory);
    }

    [Fact]
    public void An_unchanged_file_gets_the_same_decisions_again()
    {
        using var tree = new TempTree().Add("notes.txt", Notes);
        var first = tree.Scan(("nail", "pin"));
        first.Single("notes.txt", "Thumbnail").Decision = Decision.Skip;
        Remember(first);

        var (scan, recall) = ScanWithMemory(tree, ("nail", "pin"));

        var item = scan.Single("notes.txt", "Thumbnail");
        Assert.Equal(Decision.Skip, item.Decision);
        Assert.Equal(RememberState.Same, item.Remembered);
        Assert.Equal(RememberState.None, scan.Single("notes.txt", "nail").Remembered);
        Assert.Equal(new RecallSummary(1, 0, 0, 0), recall);
    }

    [Fact]
    public void Custom_text_is_remembered_too()
    {
        using var tree = new TempTree().Add("notes.txt", Notes);
        var first = tree.Scan(("nail", "pin"));
        first.Single("notes.txt", "Thumbnail").SetDecision(Decision.Custom, "Thumbpin");
        Remember(first);

        var item = ScanWithMemory(tree, ("nail", "pin")).Scan.Single("notes.txt", "Thumbnail");

        Assert.Equal(Decision.Custom, item.Decision);
        Assert.Equal("Thumbpin", item.FinalText);
    }

    [Fact]
    public void A_changed_file_keeps_decisions_for_lines_that_didnt_change()
    {
        using var tree = new TempTree().Add("notes.txt", Notes);
        var first = tree.Scan(("nail", "pin"));
        first.Single("notes.txt", "Thumbnail").Decision = Decision.Skip;
        Remember(first);
        tree.Add("notes.txt", "A new first line\n" + Notes);

        var item = ScanWithMemory(tree, ("nail", "pin")).Scan.Single("notes.txt", "Thumbnail");

        Assert.Equal(Decision.Skip, item.Decision);
        Assert.Equal(RememberState.Same, item.Remembered);
    }

    [Fact]
    public void A_changed_line_reuses_the_decision_but_asks_for_a_check()
    {
        using var tree = new TempTree().Add("notes.txt", Notes);
        var first = tree.Scan(("nail", "pin"));
        first.Single("notes.txt", "Thumbnail").Decision = Decision.Skip;
        Remember(first);
        tree.Add("notes.txt", "Thumbnail width\nnail it down\n");

        var (scan, recall) = ScanWithMemory(tree, ("nail", "pin"));

        var item = scan.Single("notes.txt", "Thumbnail");
        Assert.Equal(Decision.Skip, item.Decision);
        Assert.Equal(RememberState.Changed, item.Remembered);
        Assert.True(item.NeedsRememberedCheck);
        Assert.Equal(1, recall.ToCheck);
    }

    [Fact]
    public void A_changed_line_decision_goes_to_one_match_only()
    {
        // Two "Thumbnail" matches; only the first was decided. Then its line is edited.
        using var tree = new TempTree().Add("notes.txt", "Thumbnail size\nMore Thumbnail notes\n");
        var first = tree.Scan(("nail", "pin"));
        first.Items.First(i => i.LineText == "Thumbnail size").Decision = Decision.Skip;
        Remember(first);
        tree.Add("notes.txt", "Intro\nThumbnail size in px\nMore Thumbnail notes\n");

        var scan = ScanWithMemory(tree, ("nail", "pin")).Scan;

        var edited = scan.Items.Single(i => i.LineText == "Thumbnail size in px");
        var untouched = scan.Items.Single(i => i.LineText == "More Thumbnail notes");
        Assert.Equal((Decision.Skip, RememberState.Changed), (edited.Decision, edited.Remembered));
        Assert.Equal((Decision.Pending, RememberState.None), (untouched.Decision, untouched.Remembered));
    }

    [Fact]
    public void A_line_rewritten_beyond_recognition_is_asked_about_again()
    {
        using var tree = new TempTree().Add("notes.txt", Notes);
        var first = tree.Scan(("nail", "pin"));
        first.Single("notes.txt", "Thumbnail").Decision = Decision.Skip;
        Remember(first);
        tree.Add("notes.txt", "The Thumbnail cache is regenerated nightly by the worker\nnail it down\n");

        Assert.Equal(Decision.Pending, ScanWithMemory(tree, ("nail", "pin")).Scan.Single("notes.txt", "Thumbnail").Decision);
    }

    [Fact]
    public void All_like_this_choices_carry_over_to_new_files()
    {
        using var tree = new TempTree().Add("notes.txt", Notes);
        var first = tree.Scan(("nail", "pin"));
        var thumb = first.Single("notes.txt", "Thumbnail");
        thumb.Decision = Decision.Skip;
        Remember(first, thumb);
        tree.Add("more/other.txt", "Thumbnail again\n");

        var item = ScanWithMemory(tree, ("nail", "pin")).Scan.Single("more/other.txt", "Thumbnail");

        Assert.Equal(Decision.Skip, item.Decision);
        Assert.Equal(RememberState.Pattern, item.Remembered);
    }

    [Fact]
    public void Forgotten_patterns_stop_applying()
    {
        using var tree = new TempTree().Add("notes.txt", Notes);
        var first = tree.Scan(("nail", "pin"));
        var thumb = first.Single("notes.txt", "Thumbnail");
        thumb.Decision = Decision.Skip;
        Remember(first, thumb);

        var second = tree.Scan(("nail", "pin"));
        var memory = DecisionMemoryStore.Load(second.RootPath);
        DecisionRecall.Record(second, memory, [], [DecisionRecall.PatternKey(second.Single("notes.txt", "Thumbnail"))]);

        Assert.Empty(memory.Patterns);
    }

    [Fact]
    public void Changing_the_rule_drops_its_decisions()
    {
        using var tree = new TempTree().Add("notes.txt", Notes);
        var first = tree.Scan(("nail", "pin"));
        first.Single("notes.txt", "Thumbnail").Decision = Decision.Skip;
        Remember(first);

        var (scan, recall) = ScanWithMemory(tree, ("nail", "tack"));

        Assert.Equal(Decision.Pending, scan.Single("notes.txt", "Thumbnail").Decision);
        Assert.Equal(0, recall.Total);
    }

    [Fact]
    public void Changes_approved_automatically_are_not_stored()
    {
        using var tree = new TempTree().Add("notes.txt", Notes);
        var first = tree.Scan(("nail", "pin"));
        first.Single("notes.txt", "Thumbnail").Decision = Decision.Replace;
        Remember(first);

        var memory = DecisionMemoryStore.Load(first.RootPath);

        var decision = Assert.Single(Assert.Single(memory.Files).Decisions);
        Assert.Equal("Thumbnail", decision.Word);
    }

    [Fact]
    public void Undecided_scans_leave_no_memory_file()
    {
        using var tree = new TempTree().Add("notes.txt", Notes);
        Remember(tree.Scan(("nail", "pin")));

        Assert.False(DecisionMemoryStore.Exists(tree.Root));
    }

    [Fact]
    public void Files_written_by_a_run_are_recognized_as_already_converted()
    {
        using var tree = new TempTree().Add("notes.txt", Notes);
        var output = tree.Root + "-out";
        try
        {
            var scan = tree.Scan(("nail", "pin"));
            scan.Single("notes.txt", "Thumbnail").Decision = Decision.Skip;
            var plan = ApplyPlan.Build(scan);
            CopyApplier.Apply(plan, output, copyGitFolder: false);
            var outputMemory = DecisionMemoryStore.Load(output);
            DecisionRecall.RecordOutputs(plan, outputMemory);
            DecisionMemoryStore.Save(outputMemory);

            var rescan = Scanner.Scan(new ReplaceProfile { SourcePath = output, Rules = [new ReplacementRule { Find = "nail", Replace = "pin" }] });
            var recall = DecisionRecall.Apply(rescan, DecisionMemoryStore.Load(output));

            var item = Assert.Single(rescan.Items);
            Assert.Equal("Thumbnail", item.WordText);
            Assert.Equal(Decision.Skip, item.Decision);
            Assert.Equal(RememberState.AlreadyConverted, item.Remembered);
            Assert.Equal(1, recall.AlreadyConverted);
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, true);
            }
        }
    }

    [Fact]
    public void Deciding_again_clears_the_remembered_mark()
    {
        using var tree = new TempTree().Add("notes.txt", Notes);
        var first = tree.Scan(("nail", "pin"));
        first.Single("notes.txt", "Thumbnail").Decision = Decision.Skip;
        Remember(first);
        var item = ScanWithMemory(tree, ("nail", "pin")).Scan.Single("notes.txt", "Thumbnail");

        item.Decision = Decision.Replace;

        Assert.False(item.IsRemembered);
        Assert.Equal("Replace", item.DecisionDisplay);
    }

    [Fact]
    public void Different_folders_with_the_same_name_have_separate_memories()
    {
        Assert.NotEqual(DecisionMemoryStore.PathFor(@"C:\one\src"), DecisionMemoryStore.PathFor(@"C:\two\src"));
        Assert.Equal(DecisionMemoryStore.PathFor(@"C:\one\src"), DecisionMemoryStore.PathFor(@"c:\ONE\src\"));
    }
}
