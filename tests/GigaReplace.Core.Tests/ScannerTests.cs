using GigaReplace.Core.Planning;

namespace GigaReplace.Core.Tests;

public class ScannerTests
{
    private const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    [Fact]
    public void A_plain_rename_is_approved_without_questions()
    {
        using var tree = new TempTree()
            .Add("src/OrderHub.App/OrderHubService.cs", """
                namespace OrderHub.App;

                /// <summary>Runs OrderHub.</summary>
                public class OrderHubService
                {
                    public string Greeting => "Welcome to OrderHub";

                    public int Run()
                    {
                        var orderHubCount = 1;
                        return orderHubCount;
                    }
                }
                """)
            .Add("src/OrderHub.App/Program.cs", """
                using OrderHub.App;

                new OrderHubService().Run();
                """);

        var scan = tree.Scan(("OrderHub", "SalesDesk"));

        var ns = scan.Items.First(i => i.ContextDetail == "namespace name");
        Assert.Equal(MatchContext.Declaration, ns.Context);
        Assert.Empty(ns.Risks);

        var type = scan.Single("src/OrderHub.App/OrderHubService.cs", "OrderHubService");
        Assert.Equal("class declaration", type.ContextDetail);
        Assert.Equal("SalesDeskService", type.WordProposedText);
        Assert.Equal(Decision.Replace, type.Decision);

        var greeting = scan.In("src/OrderHub.App/OrderHubService.cs").Single(i => i.Context == MatchContext.String);
        Assert.Empty(greeting.Risks);

        var locals = scan.In("src/OrderHub.App/OrderHubService.cs").Where(i => i.WordText == "orderHubCount").ToList();
        Assert.Equal(2, locals.Count);
        Assert.All(locals, l => Assert.Equal("salesDeskCount", l.WordProposedText));
        Assert.Single(locals.Select(l => l.SymbolKey).Distinct());
        Assert.Contains("@", locals[0].SymbolKey);

        var usingItem = scan.In("src/OrderHub.App/Program.cs").First();
        Assert.Equal("using directive", usingItem.ContextDetail);
        Assert.Empty(usingItem.Risks);

        Assert.Equal("SalesDeskService.cs", scan.Single("src/OrderHub.App/OrderHubService.cs", "OrderHubService.cs", ChangeKind.FileRename).WordFinalText);
        Assert.Equal("SalesDesk.App", scan.Single("src/OrderHub.App", "OrderHub.App", ChangeKind.FolderRename).WordFinalText);
        Assert.DoesNotContain(scan.Items, i => i.IsPending);
    }

    [Fact]
    public void Names_that_come_from_outside_the_tree_are_flagged()
    {
        using var tree = new TempTree().Add("Worker.cs", """
            using System.Threading;

            class Runner
            {
                void Start() => new Thread(() => { }).Start();
            }
            """);

        var scan = tree.Scan(("Thread", "Worker"));

        var threading = scan.Single("Worker.cs", "Threading");
        Assert.True(threading.Has(RiskCode.MidWord));
        Assert.True(threading.Has(RiskCode.ExternalSymbol));
        Assert.True(scan.Single("Worker.cs", "Thread").Has(RiskCode.ExternalSymbol));
        Assert.All(scan.Items, i => Assert.Equal(Decision.Pending, i.Decision));
    }

    [Fact]
    public void Strings_used_outside_the_code_are_flagged()
    {
        using var tree = new TempTree().Add("Store.cs", """
            using System;
            using System.IO;
            using System.Threading;
            using Microsoft.Win32;

            class Store
            {
                const string Title = "OrderHub";
                readonly Mutex _single = new Mutex(true, "OrderHub-Single");
                string Folder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrderHub");
                void Remember() => Registry.CurrentUser.CreateSubKey(@"Software\OrderHub");
                string ApiUrl = "https://api.orderhub.com/v1";
                string Key() => Environment.GetEnvironmentVariable("ORDERHUB_HOME");
            }
            """);

        var scan = tree.Scan(("OrderHub", "SalesDesk"));
        var strings = scan.In("Store.cs").Where(i => i.Context == MatchContext.String).ToList();

        Assert.Empty(strings.Single(i => i.ContextDetail == "value of Title").Risks);
        Assert.True(strings.Single(i => i.ContextDetail.Contains("new Mutex")).Has(RiskCode.IpcName));
        Assert.True(strings.Single(i => i.ContextDetail.Contains("Path.Combine")).Has(RiskCode.FileSystemName));
        Assert.True(strings.Single(i => i.ContextDetail.Contains("CreateSubKey")).Has(RiskCode.RegistryKey));
        Assert.True(strings.Single(i => i.OriginalText == "orderhub").Has(RiskCode.UrlOrDomain));
        Assert.True(strings.Single(i => i.OriginalText == "ORDERHUB").Has(RiskCode.EnvironmentVariable));
    }

    [Fact]
    public void Target_typed_new_is_resolved_from_the_declaration()
    {
        using var tree = new TempTree().Add("Single.cs", """
            using System.Threading;

            class App
            {
                static readonly Mutex SingleInstance = new(false, "OrderHub.SingleInstance");
            }
            """);

        var scan = tree.Scan(("OrderHub", "SalesDesk"));

        var item = scan.In("Single.cs").Single(i => i.Context == MatchContext.String);
        Assert.Equal("string passed to new Mutex(…)", item.ContextDetail);
        Assert.True(item.Has(RiskCode.IpcName));
    }

    [Fact]
    public void Comments_naming_a_compound_symbol_follow_its_decision()
    {
        using var tree = new TempTree().Add("Service.cs", """
            /// <summary>See <see cref="OrderHubService"/>. OrderHub is the product.</summary>
            class OrderHubService { }
            """);

        var scan = tree.Scan(("OrderHub", "SalesDesk"));
        var declaration = scan.In("Service.cs").Single(i => i.Context == MatchContext.Declaration);

        var group = scan.SameSymbol(declaration);
        Assert.Contains(group, i => i.Context == MatchContext.Comment && i.WordText == "OrderHubService");
        Assert.DoesNotContain(group, i => i.WordText == "OrderHub");
    }

    [Fact]
    public void Serialized_members_are_flagged_unless_their_stored_name_is_fixed()
    {
        using var tree = new TempTree().Add("Settings.cs", """
            using System.Text.Json;
            using System.Text.Json.Serialization;

            public class AppSettings
            {
                public string OrderHubPath { get; set; } = "";

                [JsonPropertyName("hubName")]
                public string OrderHubName { get; set; } = "";
            }

            static class Loader
            {
                public static AppSettings Load(string json) => JsonSerializer.Deserialize<AppSettings>(json)!;
            }
            """);

        var scan = tree.Scan(("OrderHub", "SalesDesk"));

        Assert.True(scan.Single("Settings.cs", "OrderHubPath").Has(RiskCode.SerializedMember));
        Assert.False(scan.Single("Settings.cs", "OrderHubName").Has(RiskCode.SerializedMember));
    }

    [Fact]
    public void Clashes_and_shadowing_are_flagged()
    {
        using var tree = new TempTree().Add("Types.cs", """
            class OrderHubService { }
            class SalesDeskService { }

            class Counter
            {
                int salesDesk;

                int Total()
                {
                    int orderHub = 1;
                    return orderHub + salesDesk;
                }
            }
            """);

        var scan = tree.Scan(("OrderHub", "SalesDesk"));

        Assert.True(scan.Single("Types.cs", "OrderHubService").Has(RiskCode.NameCollision));
        var local = scan.In("Types.cs").First(i => i.WordText == "orderHub" && i.Context == MatchContext.Declaration);
        Assert.True(local.Has(RiskCode.Shadowing));
    }

    [Fact]
    public void Code_in_inactive_if_blocks_is_still_analyzed()
    {
        using var tree = new TempTree().Add("Pro.cs", """
            #if ORDERHUB_PRO
            class OrderHubPro { }
            #endif
            """);

        var scan = tree.Scan(("OrderHub", "SalesDesk"));

        Assert.Equal(MatchContext.Preprocessor, scan.Single("Pro.cs", "ORDERHUB_PRO").Context);
        Assert.Equal(MatchContext.Declaration, scan.Single("Pro.cs", "OrderHubPro").Context);
    }

    [Fact]
    public void Project_files_identity_and_msbuild_names_are_understood()
    {
        using var tree = new TempTree().Add("OrderHub.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <AssemblyName>OrderHub</AssemblyName>
                <OrderHubVersion>1</OrderHubVersion>
                <Version>$(OrderHubVersion).0</Version>
                <DefineConstants>$(DefineConstants);ORDERHUB_PRO</DefineConstants>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="OrderHub.Contracts" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        var scan = tree.Scan(("OrderHub", "SalesDesk"));
        var items = scan.In("OrderHub.csproj").ToList();

        Assert.True(items.Single(i => i.ContextDetail == "value of <AssemblyName>").Has(RiskCode.AssemblyIdentity));
        Assert.True(items.Single(i => i.ContextDetail.StartsWith("package ID")).Has(RiskCode.ExternalPackage));
        Assert.All(items.Where(i => i.WordText == "OrderHubVersion"), i => Assert.Equal("OrderHubVersion", i.SymbolKey));
        Assert.Equal(3, items.Count(i => i.WordText == "OrderHubVersion"));
        Assert.Equal("ORDERHUB_PRO", items.Single(i => i.WordText == "ORDERHUB_PRO").SymbolKey);

        // AssemblyName is set explicitly, so only the default namespace follows the file name.
        var rename = scan.Single("OrderHub.csproj", "OrderHub.csproj", ChangeKind.FileRename);
        var risk = rename.Risks.Single(r => r.Code == RiskCode.DefaultProjectNames);
        Assert.Contains("default namespace", risk.Message);
        Assert.DoesNotContain("assembly will be built", risk.Message);
    }

    [Fact]
    public void Renaming_a_project_file_warns_when_names_come_from_it()
    {
        using var tree = new TempTree().Add("OrderHub.csproj", Csproj);
        var scan = tree.Scan(("OrderHub", "SalesDesk"));
        var rename = scan.Single("OrderHub.csproj", "OrderHub.csproj", ChangeKind.FileRename);
        Assert.True(rename.Has(RiskCode.DefaultProjectNames));
        Assert.True(rename.IsPending);
    }

    [Fact]
    public void Xaml_names_bindings_and_display_text_are_told_apart()
    {
        using var tree = new TempTree().Add("MainWindow.xaml", """
            <Window x:Class="OrderHub.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Title="OrderHub">
              <TextBlock x:Name="orderHubLabel" Text="{Binding OrderHubName}" />
            </Window>
            """);

        var scan = tree.Scan(("OrderHub", "SalesDesk"));
        var items = scan.In("MainWindow.xaml").ToList();

        Assert.Equal("OrderHub", items.Single(i => i.ContextDetail.StartsWith("x:Class")).SymbolKey);
        Assert.Null(items.Single(i => i.ContextDetail == "display text").SymbolKey);
        Assert.Equal("orderHubLabel", items.Single(i => i.ContextDetail.StartsWith("x:Name")).SymbolKey);
        Assert.Equal("OrderHubName", items.Single(i => i.ContextDetail.StartsWith("binding")).SymbolKey);
    }

    [Fact]
    public void Watch_list_terms_flag_their_lines()
    {
        using var tree = new TempTree().Add("appsettings.json", """
            {
              "ConnectionStrings": { "Main": "Server=.;Database=OrderHub" },
              "Title": "OrderHub"
            }
            """);

        var profile = tree.Profile(("OrderHub", "SalesDesk"));
        profile.WatchList = ["Database="];
        var scan = Scanner.Scan(profile);

        var items = scan.In("appsettings.json").ToList();
        Assert.True(items.Single(i => i.LineText.Contains("Database=")).Has(RiskCode.WatchListTerm));
        Assert.False(items.Single(i => i.LineText.Contains("Title")).Has(RiskCode.WatchListTerm));
    }

    [Fact]
    public void Switching_a_check_off_stops_its_questions()
    {
        using var tree = new TempTree().Add("Store.cs", """
            class Store
            {
                string Folder() => System.IO.Path.Combine("data", "OrderHub");
            }
            """);

        var profile = tree.Profile(("OrderHub", "SalesDesk"));
        profile.RiskChecks.PersistedData = false;
        var scan = Scanner.Scan(profile);

        var item = scan.In("Store.cs").Single();
        Assert.Empty(item.Risks);
        Assert.Equal(Decision.Replace, item.Decision);
    }

    [Fact]
    public void Identity_guids_can_be_regenerated_consistently()
    {
        const string guid = "8a5b2f21-1111-4c3d-9e8f-0123456789ab";
        using var tree = new TempTree()
            .Add("App.csproj", $"<Project><PropertyGroup><ProjectGuid>{{{guid.ToUpperInvariant()}}}</ProjectGuid></PropertyGroup></Project>")
            .Add("App.sln", $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"App\", \"App.csproj\", \"{{{guid.ToUpperInvariant()}}}\"\r\nEndProject\r\n");

        var profile = tree.Profile();
        profile.RegenerateIdentityGuids = true;
        var scan = Scanner.Scan(profile);

        var guidItems = scan.Items.Where(i => i.Context == MatchContext.IdentityGuid).ToList();
        Assert.Equal(2, guidItems.Count);
        Assert.Single(guidItems.Select(i => i.ProposedText).Distinct());
        Assert.All(guidItems, i => Assert.Equal(i.ProposedText.ToUpperInvariant(), i.ProposedText));
        Assert.DoesNotContain(scan.Items, i => i.OriginalText.Equals("FAE04EC0-301F-11D3-BF4B-00C04F79EFBC", StringComparison.OrdinalIgnoreCase));
    }
}
