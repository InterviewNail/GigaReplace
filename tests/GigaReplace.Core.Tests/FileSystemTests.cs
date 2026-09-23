using GigaReplace.Core.FileSystem;

namespace GigaReplace.Core.Tests;

public class IgnoreRulesTests
{
    [Theory]
    [InlineData("bin/", "src/bin", true, true)]
    [InlineData("bin/", "src/bin", false, false)]
    [InlineData("*.user", "a/b/c.csproj.user", false, true)]
    [InlineData("/build", "build", true, true)]
    [InlineData("/build", "src/build", true, false)]
    [InlineData("docs/**/*.md", "docs/a/b/c.md", false, true)]
    [InlineData("**/obj", "x/y/obj", true, true)]
    [InlineData("secret?.txt", "secret1.txt", false, true)]
    [InlineData("[Dd]ebug/", "Debug", true, true)]
    public void Matches_like_git(string pattern, string path, bool isDirectory, bool ignored)
    {
        var rules = IgnoreRules.AtRootRelative("", [pattern]);
        Assert.Equal(ignored ? IgnoreVerdict.Ignored : IgnoreVerdict.None, rules.Evaluate(path, isDirectory));
    }

    [Fact]
    public void Later_negations_win()
    {
        var rules = IgnoreRules.AtRootRelative("", ["*.log", "!keep.log"]);
        Assert.Equal(IgnoreVerdict.Ignored, rules.Evaluate("a.log", false));
        Assert.Equal(IgnoreVerdict.Included, rules.Evaluate("keep.log", false));
    }

    [Fact]
    public void Rules_below_the_root_only_apply_inside_their_folder()
    {
        var rules = IgnoreRules.AtRootRelative("src", ["generated.cs"]);
        Assert.Equal(IgnoreVerdict.Ignored, rules.Evaluate("src/generated.cs", false));
        Assert.Equal(IgnoreVerdict.None, rules.Evaluate("generated.cs", false));
    }
}

public class SourceTreeTests
{
    [Fact]
    public void Honors_exclusions_and_nested_gitignore_files()
    {
        using var tree = new TempTree()
            .Add(".gitignore", "*.log\n")
            .Add("app/.gitignore", "secret.txt\n")
            .Add("app/secret.txt", "x")
            .Add("app/keep.cs", "x")
            .Add("app/run.log", "x")
            .Add("app/bin/Debug/app.dll", "x")
            .Add("app/obj/project.assets.json", "x");

        var result = SourceTreeEnumerator.Enumerate(tree.Root, ["bin/", "obj/"], respectGitIgnore: true);

        var files = result.Files.Select(f => f.RelativePath).ToHashSet();
        Assert.Contains(@"app\keep.cs", files);
        Assert.Contains(@"app\.gitignore", files);
        Assert.DoesNotContain(@"app\secret.txt", files);
        Assert.DoesNotContain(@"app\run.log", files);
        Assert.DoesNotContain(result.Directories, d => d.EndsWith("bin", StringComparison.Ordinal));
        Assert.Contains("bin", result.GetChildNames("app"));
    }

    [Fact]
    public void Never_enters_git_folders()
    {
        using var tree = new TempTree().Add(".git/config", "x").Add("a.txt", "x");
        var result = SourceTreeEnumerator.Enumerate(tree.Root, [], respectGitIgnore: false);
        Assert.Equal(["a.txt"], result.Files.Select(f => f.RelativePath));
    }
}
