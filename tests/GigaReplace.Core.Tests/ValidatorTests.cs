using GigaReplace.Core.Planning;

namespace GigaReplace.Core.Tests;

public class ValidatorTests
{
    [Fact]
    public void Renaming_a_name_in_only_some_places_is_reported_with_fixes()
    {
        using var tree = new TempTree()
            .Add("A.cs", "class OrderHubService { }")
            .Add("B.cs", "class Use { OrderHubService? service; }");

        var scan = tree.Scan(("OrderHub", "SalesDesk"));
        Assert.Empty(PlanValidator.Validate(scan));

        scan.Single("B.cs", "OrderHubService").Decision = Decision.Skip;
        var problem = Assert.Single(PlanValidator.Validate(scan));
        Assert.Equal("Name only partly renamed", problem.Title);

        var keep = problem.Fixes.Single(f => f.Decision == Decision.Skip);
        foreach (var item in keep.Items)
        {
            item.Decision = keep.Decision;
        }

        Assert.Empty(PlanValidator.Validate(scan));
    }

    [Fact]
    public void Msbuild_properties_and_preprocessor_symbols_must_stay_in_step()
    {
        using var tree = new TempTree()
            .Add("App.csproj", "<Project><PropertyGroup><DefineConstants>ORDERHUB_PRO</DefineConstants></PropertyGroup></Project>")
            .Add("Code.cs", "#if ORDERHUB_PRO\nclass X { }\n#endif\n");

        var scan = tree.Scan(("OrderHub", "SalesDesk"));
        scan.Single("Code.cs", "ORDERHUB_PRO").Decision = Decision.Skip;

        Assert.Contains(PlanValidator.Validate(scan), p => p.Title == "Name only partly renamed");
    }

    [Fact]
    public void A_file_reference_left_pointing_at_the_old_name_is_reported()
    {
        using var tree = new TempTree()
            .Add("OrderHub.slnx", "<Solution>\n  <Project Path=\"src/OrderHub/OrderHub.csproj\" />\n</Solution>\n")
            .Add("src/OrderHub/OrderHub.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>OrderHub</AssemblyName><RootNamespace>OrderHub</RootNamespace></PropertyGroup></Project>");

        var scan = tree.Scan(("OrderHub", "SalesDesk"));
        scan.ApproveAll();
        Assert.Empty(PlanValidator.Validate(scan));

        foreach (var item in scan.In("OrderHub.slnx").Where(i => i.Kind == ChangeKind.Content))
        {
            item.Decision = Decision.Skip;
        }

        var problem = Assert.Single(PlanValidator.Validate(scan), p => p.Title == "Broken file reference");
        Assert.Contains(problem.Fixes, f => f.Label == "Update the reference");
    }

    [Fact]
    public void Two_items_ending_up_with_one_name_is_an_error()
    {
        using var tree = new TempTree().Add("OrderHub.txt", "a").Add("SalesDesk.txt", "b");

        var scan = tree.Scan(("OrderHub", "SalesDesk"));
        var rename = scan.Single("OrderHub.txt", "OrderHub.txt", ChangeKind.FileRename);
        Assert.True(rename.Has(RiskCode.RenameTargetExists));

        rename.Decision = Decision.Replace;
        var problem = Assert.Single(PlanValidator.Validate(scan));
        Assert.True(problem.IsError);
        Assert.Equal("Name clash", problem.Title);
    }

    [Fact]
    public void Invalid_custom_names_are_errors()
    {
        using var tree = new TempTree().Add("OrderHub.txt", "a");
        var scan = tree.Scan(("OrderHub", "SalesDesk"));
        scan.Single("OrderHub.txt", "OrderHub.txt", ChangeKind.FileRename).SetDecision(Decision.Custom, "Sales:Desk");

        Assert.Contains(PlanValidator.Validate(scan), p => p.IsError && p.Title == "Invalid name");
    }
}
