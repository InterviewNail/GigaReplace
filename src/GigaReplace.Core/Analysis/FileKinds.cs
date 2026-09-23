namespace GigaReplace.Core.Analysis;

internal enum FileKind
{
    CSharp,
    Xaml,
    MsBuild,
    Solution,
    SolutionX,
    Json,
    Resx,
    Settings,
    Config,
    AppManifest,
    PackageManifest,
    WixSource,
    InstallerScript,
    Script,
    Markdown,
    OtherCode,
    CodeGenSource,
    Sql,
    Xml,
    Text,
}

internal static class FileKinds
{
    public static FileKind Of(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".cs" or ".csx" => FileKind.CSharp,
            ".xaml" or ".axaml" => FileKind.Xaml,
            ".csproj" or ".vbproj" or ".fsproj" or ".proj" or ".props" or ".targets" or ".projitems" or ".shproj"
                or ".wixproj" or ".sqlproj" or ".esproj" or ".vcxproj" or ".vcxitems" => FileKind.MsBuild,
            ".sln" => FileKind.Solution,
            ".slnx" => FileKind.SolutionX,
            ".json" or ".jsonc" or ".slnf" => FileKind.Json,
            ".resx" => FileKind.Resx,
            ".settings" => FileKind.Settings,
            ".config" => FileKind.Config,
            ".manifest" => FileKind.AppManifest,
            ".appxmanifest" or ".nuspec" or ".vsixmanifest" => FileKind.PackageManifest,
            ".wxs" or ".wxi" or ".wxl" => FileKind.WixSource,
            ".iss" or ".nsi" or ".nsh" => FileKind.InstallerScript,
            ".ps1" or ".psm1" or ".psd1" or ".cmd" or ".bat" or ".sh" or ".yml" or ".yaml" => FileKind.Script,
            ".md" or ".markdown" or ".txt" or ".rst" or ".adoc" => FileKind.Markdown,
            ".razor" or ".cshtml" or ".vbhtml" or ".vb" or ".fs" or ".fsi" or ".fsx" or ".ts" or ".tsx" or ".js"
                or ".jsx" or ".mjs" or ".py" or ".cpp" or ".cc" or ".h" or ".hpp" or ".c" or ".java" or ".kt"
                or ".go" or ".rs" or ".swift" => FileKind.OtherCode,
            ".proto" or ".tt" or ".graphql" or ".gql" or ".xsd" => FileKind.CodeGenSource,
            ".sql" => FileKind.Sql,
            ".xml" or ".xsl" or ".xslt" or ".runsettings" or ".ruleset" or ".dbml" or ".edmx" or ".vstemplate"
                or ".svg" or ".plist" or ".storyboard" or ".nuproj" => FileKind.Xml,
            _ => FileKind.Text,
        };
    }

    public static bool IsProjectFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".vbproj" or ".fsproj";

    public static bool IsXmlLike(FileKind kind) => kind is FileKind.Xaml or FileKind.MsBuild or FileKind.SolutionX
        or FileKind.Resx or FileKind.Settings or FileKind.Config or FileKind.AppManifest or FileKind.PackageManifest
        or FileKind.WixSource or FileKind.Xml;
}
