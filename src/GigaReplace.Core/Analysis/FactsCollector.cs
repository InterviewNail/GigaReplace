using System.Text.RegularExpressions;
using GigaReplace.Core.Analysis.CSharp;
using GigaReplace.Core.Analysis.Markup;

namespace GigaReplace.Core.Analysis;

/// <summary>First pass over each file: what it declares, which projects exist, identity GUIDs and path references.</summary>
internal static partial class FactsCollector
{
    [GeneratedRegex(@"(?<![\w.:])(?:x:)?Name\s*=\s*""(?<v>[^""]+)""")]
    private static partial Regex XamlName();

    [GeneratedRegex(@"\bx:Class\s*=\s*""(?<v>[^""]+)""")]
    private static partial Regex XamlClass();

    [GeneratedRegex(@"<data\s+name\s*=\s*""(?<v>[^""]+)""")]
    private static partial Regex ResxName();

    [GeneratedRegex(@"<Setting\s+Name\s*=\s*""(?<v>[^""]+)""")]
    private static partial Regex SettingName();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Word();

    public static FileFacts Collect(string relativePath, string text)
    {
        var facts = new FileFacts();
        var kind = FileKinds.Of(relativePath);
        switch (kind)
        {
            case FileKind.CSharp:
                var (root, alternate) = CSharpParsing.Parse(text, relativePath);
                CSharpFactsCollector.Collect(root, facts, relativePath);
                if (alternate is not null)
                {
                    CSharpFactsCollector.Collect(alternate, facts, relativePath);
                }

                break;
            case FileKind.Xaml:
                foreach (Match m in XamlName().Matches(text))
                {
                    facts.Declared.Add(m.Groups["v"].Value);
                }

                foreach (Match m in XamlClass().Matches(text))
                {
                    facts.Declared.AddRange(m.Groups["v"].Value.Split('.'));
                }

                break;
            case FileKind.Resx:
                foreach (Match m in ResxName().Matches(text))
                {
                    var name = m.Groups["v"].Value;
                    facts.Declared.Add(name);
                    facts.Declared.Add(Regex.Replace(name, @"[^\w]", "_"));
                }

                break;
            case FileKind.Settings:
                foreach (Match m in SettingName().Matches(text))
                {
                    facts.Declared.Add(m.Groups["v"].Value);
                }

                break;
            case FileKind.MsBuild when FileKinds.IsProjectFile(relativePath):
                facts.Project = ProjectFactsOf(relativePath, text);
                break;
            case FileKind.OtherCode or FileKind.CodeGenSource:
                foreach (Match m in Word().Matches(text))
                {
                    facts.WeakDeclared.Add(m.Value);
                }

                break;
        }

        IdentityGuidFinder.Find(relativePath, kind, text, facts.Guids);
        PathReferenceExtractor.Extract(relativePath, kind, text, facts.PathReferences);
        return facts;
    }

    private static ProjectFacts ProjectFactsOf(string relativePath, string text)
    {
        var facts = new ProjectFacts { RelativePath = relativePath };
        var map = XmlDocumentMap.Build(text);
        foreach (var span in map.Spans)
        {
            if (span.Kind != XmlSpanKind.Text || span.Parent != "PropertyGroup")
            {
                continue;
            }

            var value = text[span.Start..span.End].Trim();
            if (value.Length == 0)
            {
                continue;
            }

            switch (span.Element)
            {
                case "AssemblyName":
                    facts.AssemblyName = value;
                    break;
                case "RootNamespace":
                    facts.RootNamespace = value;
                    break;
                case "PackageId":
                    facts.PackageId = value;
                    break;
            }
        }

        return facts;
    }
}

internal static partial class IdentityGuidFinder
{
    private const string G = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

    [GeneratedRegex($$"""<ProjectGuid>\s*\{?(?<g>{{G}})\}?\s*</ProjectGuid>""", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectGuid();

    [GeneratedRegex($$"""<UserSecretsId>\s*(?<g>{{G}})\s*</UserSecretsId>""", RegexOptions.IgnoreCase)]
    private static partial Regex UserSecretsId();

    [GeneratedRegex($$"""<ApplicationIdGuid>\s*(?<g>{{G}})\s*</ApplicationIdGuid>""", RegexOptions.IgnoreCase)]
    private static partial Regex ApplicationIdGuid();

    [GeneratedRegex($$"""\[\s*assembly\s*:\s*(?:System\.Runtime\.InteropServices\.)?Guid(?:Attribute)?\s*\(\s*"(?<g>{{G}})""", RegexOptions.IgnoreCase)]
    private static partial Regex AssemblyGuid();

    [GeneratedRegex($$"""\bUpgradeCode\s*=\s*"\{?(?<g>{{G}})""", RegexOptions.IgnoreCase)]
    private static partial Regex UpgradeCode();

    [GeneratedRegex($$"""\bProductCode\s*=\s*"\{?(?<g>{{G}})""", RegexOptions.IgnoreCase)]
    private static partial Regex ProductCode();

    [GeneratedRegex($$"""<Product\b[^>]*?\bId\s*=\s*"\{?(?<g>{{G}})""", RegexOptions.IgnoreCase)]
    private static partial Regex WixProductId();

    [GeneratedRegex($$"""<Component\b[^>]*?\bGuid\s*=\s*"\{?(?<g>{{G}})""", RegexOptions.IgnoreCase)]
    private static partial Regex WixComponentGuid();

    [GeneratedRegex($$"""\bAppId\s*=\s*\{\{?(?<g>{{G}})\}""", RegexOptions.IgnoreCase)]
    private static partial Regex InnoAppId();

    [GeneratedRegex($$"""^Project\("\{[^}]+\}"\)\s*=\s*"[^"]*"\s*,\s*"[^"]*"\s*,\s*"\{(?<g>{{G}})\}""", RegexOptions.Multiline)]
    private static partial Regex SolutionProjectGuid();

    [GeneratedRegex($$"""SolutionGuid\s*=\s*\{(?<g>{{G}})\}""", RegexOptions.IgnoreCase)]
    private static partial Regex SolutionGuid();

    [GeneratedRegex($$"""PhoneProductId\s*=\s*"(?<g>{{G}})""", RegexOptions.IgnoreCase)]
    private static partial Regex PhoneProductId();

    public static void Find(string relativePath, FileKind kind, string text, List<IdentityGuidCandidate> into)
    {
        void Scan(Regex regex, string label)
        {
            foreach (Match m in regex.Matches(text))
            {
                into.Add(new IdentityGuidCandidate(m.Groups["g"].Value.ToLowerInvariant(), label, relativePath));
            }
        }

        switch (kind)
        {
            case FileKind.MsBuild:
                Scan(ProjectGuid(), "ProjectGuid");
                Scan(UserSecretsId(), "UserSecretsId");
                Scan(ApplicationIdGuid(), "ApplicationIdGuid");
                break;
            case FileKind.CSharp:
                Scan(AssemblyGuid(), "Assembly GUID");
                break;
            case FileKind.WixSource:
                Scan(UpgradeCode(), "Installer UpgradeCode");
                Scan(ProductCode(), "Installer ProductCode");
                Scan(WixProductId(), "Installer ProductCode");
                Scan(WixComponentGuid(), "Installer component GUID");
                break;
            case FileKind.InstallerScript:
                Scan(InnoAppId(), "Inno Setup AppId");
                break;
            case FileKind.Solution:
                Scan(SolutionProjectGuid(), "Project GUID in solution");
                Scan(SolutionGuid(), "Solution GUID");
                break;
            case FileKind.PackageManifest:
                Scan(PhoneProductId(), "PhoneProductId");
                break;
        }
    }
}

/// <summary>Finds file paths written inside project, solution, XAML, resx and WiX files, to check they still resolve after renames.</summary>
internal static partial class PathReferenceExtractor
{
    private static readonly HashSet<string> PathItems = new(StringComparer.Ordinal)
    {
        "ProjectReference", "Compile", "None", "Content", "EmbeddedResource", "Page", "Resource", "ApplicationDefinition",
        "AdditionalFiles", "Analyzer", "Protobuf", "SplashScreen", "Import",
    };

    private static readonly HashSet<string> PathProperties = new(StringComparer.Ordinal)
    {
        "ApplicationIcon", "ApplicationManifest", "Win32Resource", "AssemblyOriginatorKeyFile", "HintPath", "CodeAnalysisRuleSet",
    };

    private static readonly HashSet<string> XamlPathAttributes = new(StringComparer.Ordinal)
    {
        "Source", "ImageSource", "Icon", "StartupUri", "UriSource",
    };

    [GeneratedRegex(@"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""(?<p>[^""]*)""", RegexOptions.Multiline)]
    private static partial Regex SolutionProjectPath();

    [GeneratedRegex(@"<data\b[^>]*type\s*=\s*""System\.Resources\.ResXFileRef[^""]*""[^>]*>\s*<value>(?<p>[^;<]+);", RegexOptions.IgnoreCase)]
    private static partial Regex ResxFileRef();

    public static void Extract(string relativePath, FileKind kind, string text, List<PathReference> into)
    {
        switch (kind)
        {
            case FileKind.MsBuild:
                MsBuild(relativePath, text, into);
                break;
            case FileKind.Solution:
                foreach (Match m in SolutionProjectPath().Matches(text))
                {
                    var group = m.Groups["p"];
                    if (Path.HasExtension(group.Value))
                    {
                        Add(relativePath, group.Index, group.Length, group.Value, ReferenceBase.FileDirectory, into);
                    }
                }

                break;
            case FileKind.SolutionX:
                foreach (var span in XmlDocumentMap.Build(text).Spans)
                {
                    if (span is { Kind: XmlSpanKind.AttributeValue, Attribute: "Path", Element: "Project" or "File" })
                    {
                        Add(relativePath, span.Start, span.End - span.Start, text[span.Start..span.End], ReferenceBase.FileDirectory, into);
                    }
                }

                break;
            case FileKind.Xaml:
                Xaml(relativePath, text, into);
                break;
            case FileKind.Resx:
                foreach (Match m in ResxFileRef().Matches(text))
                {
                    var group = m.Groups["p"];
                    Add(relativePath, group.Index, group.Length, group.Value, ReferenceBase.FileDirectory, into);
                }

                break;
            case FileKind.WixSource:
                foreach (var span in XmlDocumentMap.Build(text).Spans)
                {
                    if (span is { Kind: XmlSpanKind.AttributeValue, Attribute: "Source" or "SourceFile" })
                    {
                        Add(relativePath, span.Start, span.End - span.Start, text[span.Start..span.End], ReferenceBase.FileDirectory, into);
                    }
                }

                break;
        }
    }

    private static void MsBuild(string relativePath, string text, List<PathReference> into)
    {
        foreach (var span in XmlDocumentMap.Build(text).Spans)
        {
            bool isPath = span switch
            {
                { Kind: XmlSpanKind.AttributeValue, Attribute: "Include" or "Update" } => PathItems.Contains(span.Element),
                { Kind: XmlSpanKind.AttributeValue, Attribute: "Project", Element: "Import" } => true,
                { Kind: XmlSpanKind.Text } => PathProperties.Contains(span.Element),
                _ => false,
            };

            if (!isPath)
            {
                continue;
            }

            // Item includes can list several paths separated by semicolons.
            int position = span.Start;
            foreach (var part in text[span.Start..span.End].Split(';'))
            {
                int leading = part.Length - part.TrimStart().Length;
                var trimmed = part.Trim();
                Add(relativePath, position + leading, trimmed.Length, trimmed, ReferenceBase.FileDirectory, into);
                position += part.Length + 1;
            }
        }
    }

    private static void Xaml(string relativePath, string text, List<PathReference> into)
    {
        foreach (var span in XmlDocumentMap.Build(text).Spans)
        {
            if (span.Kind != XmlSpanKind.AttributeValue || span.Attribute is null || !XamlPathAttributes.Contains(span.Attribute))
            {
                continue;
            }

            var value = text[span.Start..span.End];
            const string packPrefix = "pack://application:,,,/";
            if (value.StartsWith(packPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (!value.Contains(";component", StringComparison.OrdinalIgnoreCase))
                {
                    Add(relativePath, span.Start + packPrefix.Length, value.Length - packPrefix.Length, value[packPrefix.Length..], ReferenceBase.ProjectDirectory, into);
                }
            }
            else if (value.StartsWith('/'))
            {
                Add(relativePath, span.Start + 1, value.Length - 1, value[1..], ReferenceBase.ProjectDirectory, into);
            }
            else
            {
                Add(relativePath, span.Start, value.Length, value, ReferenceBase.FileDirectory, into);
            }
        }
    }

    private static void Add(string file, int start, int length, string value, ReferenceBase referenceBase, List<PathReference> into)
    {
        if (value.Length == 0 || value.IndexOfAny(['*', '?', '{', '}', '|', '<', '>']) >= 0
            || value.Contains("$(", StringComparison.Ordinal) || value.Contains("@(", StringComparison.Ordinal)
            || value.Contains("%(", StringComparison.Ordinal) || value.Contains("!(", StringComparison.Ordinal)
            || value.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(value))
        {
            return;
        }

        into.Add(new PathReference(file, start, length, referenceBase));
    }
}
