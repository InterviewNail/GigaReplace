using GigaReplace.Core.Profiles;

namespace GigaReplace.Core.Tests;

public class ProfileTests
{
    [Fact]
    public void Profiles_round_trip_through_json()
    {
        using var tree = new TempTree();
        var profile = new ReplaceProfile
        {
            Name = "Rename OrderHub",
            SourcePath = @"C:\src\OrderHub\OrderHub.slnx",
            Rules = [new ReplacementRule { Find = "OrderHub", Replace = "SalesDesk", MatchCase = true, PreserveCase = false }],
            WatchList = ["ConnectionString", "/Secret\\w*/"],
            RiskChecks = new RiskCheckSettings { PersistedData = false },
            WriteMode = WriteMode.InPlace,
            PromptMode = PromptMode.PromptDuringRun,
            RegenerateIdentityGuids = true,
        };

        var path = tree.Full("profile.grprofile.json");
        ProfileStore.Save(profile, path);
        var loaded = ProfileStore.Load(path);

        Assert.Equal(profile.Name, loaded.Name);
        Assert.Equal(profile.SourcePath, loaded.SourcePath);
        var rule = Assert.Single(loaded.Rules);
        Assert.Equal(("OrderHub", "SalesDesk", true, false), (rule.Find, rule.Replace, rule.MatchCase, rule.PreserveCase));
        Assert.Equal(profile.WatchList, loaded.WatchList);
        Assert.False(loaded.RiskChecks.PersistedData);
        Assert.True(loaded.RiskChecks.BuildBreakers);
        Assert.Equal(WriteMode.InPlace, loaded.WriteMode);
        Assert.Equal(PromptMode.PromptDuringRun, loaded.PromptMode);
        Assert.True(loaded.RegenerateIdentityGuids);
        Assert.Contains("\"writeMode\": \"InPlace\"", File.ReadAllText(path));
    }

    [Fact]
    public void The_csv_report_lists_every_item_and_defuses_formulas()
    {
        using var tree = new TempTree().Add("a.txt", "=OrderHub, \"quoted\" OrderHub");
        var scan = tree.Scan(("OrderHub", "SalesDesk"));
        var path = tree.Full("report.csv");

        CsvReport.Write(scan, path);

        var lines = File.ReadAllLines(path);
        Assert.Equal(scan.Items.Count + 1, lines.Length);
        Assert.StartsWith("Id,Kind,Path", lines[0].TrimStart('\uFEFF'));
        Assert.DoesNotContain(lines.Skip(1), l => l.Contains(",=", StringComparison.Ordinal));
    }
}
