using GigaReplace.Core.Text;

namespace GigaReplace.Core.Analysis.Markup;

/// <summary>Project files, .props and .targets.</summary>
internal sealed class MsBuildAnalyzer(FileAnalysisContext ctx) : IFileAnalyzer
{
    private static readonly HashSet<string> FileItems = new(StringComparer.Ordinal)
    {
        "ProjectReference", "Compile", "None", "Content", "EmbeddedResource", "Page", "Resource", "ApplicationDefinition",
        "Folder", "AdditionalFiles", "Analyzer", "Protobuf", "TypeScriptCompile", "SplashScreen", "Import", "Reference",
    };

    private readonly XmlDocumentMap _map = XmlDocumentMap.Build(ctx.Text);

    public MatchAnalysis Analyze(int matchIndex)
    {
        var match = ctx.Matches[matchIndex];
        var analysis = MatchAnalysis.ForWord(ctx.Text, match, MatchContext.MarkupText, "project file");
        if (_map.Find(match.Start) is not { } span)
        {
            return analysis;
        }

        string word = analysis.Word(ctx.Text);
        switch (span.Kind)
        {
            case XmlSpanKind.Comment:
                analysis.Context = MatchContext.Comment;
                analysis.Detail = "XML comment";
                break;
            case XmlSpanKind.ElementName:
                analysis.Context = MatchContext.MarkupName;
                analysis.Detail = span.Parent == "PropertyGroup" ? "MSBuild property name" : $"MSBuild element <{span.Element}>";
                analysis.SymbolKey = word;
                break;
            case XmlSpanKind.AttributeName:
                analysis.Context = MatchContext.MarkupName;
                analysis.Detail = "attribute name";
                analysis.SymbolKey = word;
                break;
            case XmlSpanKind.AttributeValue:
                AttributeValue(span, match, word, analysis);
                break;
            case XmlSpanKind.Text or XmlSpanKind.CData:
                ElementText(span, match, word, analysis);
                break;
        }

        return analysis;
    }

    private void AttributeValue(XmlSpan span, RawMatch match, string word, MatchAnalysis analysis)
    {
        analysis.Context = MatchContext.MarkupValue;
        analysis.Detail = $"{span.Attribute} on <{span.Element}>";
        string value = ctx.Text[span.Start..span.End];
        if (XmlDocumentMap.InMsBuildReference(ctx.Text, match.Start, match.End))
        {
            analysis.Detail = "MSBuild property reference";
            analysis.SymbolKey = word;
            return;
        }

        switch (span.Attribute)
        {
            case "Include" or "Update" or "Remove":
                ItemInclude(span.Element, value, word, analysis);
                return;
            case "Name" when span.Element == "Target":
            case "DependsOnTargets" or "BeforeTargets" or "AfterTargets" or "Targets":
                analysis.Detail = "MSBuild target name";
                analysis.SymbolKey = word;
                return;
            case "Project" when span.Element == "Import":
                analysis.Detail = "imported file path";
                return;
            case "Sdk":
                analysis.Add(RiskCode.ExternalPackage, "MSBuild SDK name. An SDK with the new name must exist, or the project won't load.");
                return;
            case "Condition":
                analysis.Detail = "MSBuild condition";
                return;
        }

        ValueHeuristics.Inspect(value, match.Start - span.Start, match.Length, analysis, allowPaths: false);
    }

    private void ItemInclude(string item, string value, string word, MatchAnalysis analysis)
    {
        string id = value.Trim();
        switch (item)
        {
            case "PackageReference" or "PackageVersion" or "GlobalPackageReference" or "PackageDownload":
                if (ctx.Index.ProducedNames.Contains(id))
                {
                    analysis.Add(RiskCode.PackageIdentity, $"“{id}” is a package built by a project in this tree. The rename only works if that project's package ID changes the same way.");
                }
                else
                {
                    analysis.Add(RiskCode.ExternalPackage, $"“{id}” is a NuGet package from outside this tree. A package with the new ID must exist on your feed, or restore fails.");
                }

                analysis.Detail = $"package ID in <{item}>";
                return;
            case "Reference" when !id.Contains('\\') && !id.Contains('/'):
                if (!ctx.Index.ProducedNames.Contains(id.Split(',')[0].Trim()))
                {
                    analysis.Add(RiskCode.ExternalPackage, $"Reference to the assembly “{id}”, which isn't built in this tree. An assembly with the new name must exist.");
                }

                analysis.Detail = "assembly reference";
                return;
            case "InternalsVisibleTo":
                analysis.Add(RiskCode.AssemblyIdentity, "Names another assembly. It must match that assembly's real name.");
                return;
            case "Using":
                analysis.Detail = "global using";
                analysis.SymbolKey = word;
                return;
            case "FrameworkReference" or "SupportedPlatform" or "ProjectCapability":
                analysis.Add(RiskCode.ExternalPackage, $"<{item}> names something provided by the SDK. Renaming it breaks the reference.");
                return;
        }

        if (FileItems.Contains(item))
        {
            analysis.Detail = $"file path in <{item}>";
        }
    }

    private void ElementText(XmlSpan span, RawMatch match, string word, MatchAnalysis analysis)
    {
        analysis.Context = MatchContext.MarkupText;
        analysis.Detail = $"value of <{span.Element}>";
        if (XmlDocumentMap.InMsBuildReference(ctx.Text, match.Start, match.End))
        {
            analysis.Detail = "MSBuild property reference";
            analysis.SymbolKey = word;
            return;
        }

        switch (span.Element)
        {
            case "AssemblyName":
                analysis.Add(RiskCode.AssemblyIdentity, "The output assembly name (the .dll/.exe file name). Project references, InternalsVisibleTo entries, pack URIs and anything that loads it by name must match.");
                break;
            case "RootNamespace":
                analysis.Add(RiskCode.AssemblyIdentity, "The default namespace. It decides embedded-resource names, so code that loads resources by name must match.");
                break;
            case "PackageId":
                analysis.Add(RiskCode.PackageIdentity, "The NuGet package ID. Consumers reference this exact ID, so a new ID means a new package.");
                break;
            case "Product" or "Company" or "Authors" or "Title" or "AssemblyTitle" or "Copyright" or "Trademark" or "Description" or "NeutralLanguage":
                analysis.Add(RiskCode.AssemblyIdentity, $"<{span.Element}> is assembly metadata shown in the file's properties. Company and product names also decide where .NET keeps user settings.");
                break;
            case "UserSecretsId":
                analysis.Add(RiskCode.AssemblyIdentity, "User-secrets ID. It decides which secrets.json the project reads on developer machines.");
                break;
            case "ApplicationId" or "ApplicationTitle" or "ApplicationIdGuid" or "ToolCommandName" or "PackageName" or "AppxPackageName":
                analysis.Add(RiskCode.PackageIdentity, $"<{span.Element}> identifies the app or tool to the OS or a package manager.");
                break;
            case "DefineConstants":
                analysis.Detail = "compilation symbol";
                analysis.SymbolKey = word;
                break;
            case "StartupObject":
                analysis.Detail = "startup type";
                analysis.SymbolKey = word;
                break;
            case "PackageProjectUrl" or "RepositoryUrl" or "PublishUrl" or "InstallUrl" or "UpdateUrl" or "SupportUrl" or "PackageLicenseUrl":
                analysis.Add(RiskCode.UrlOrDomain, "Web address in project metadata. The old address may still be the live one.");
                break;
            default:
                ValueHeuristics.Inspect(ctx.Text[span.Start..span.End], match.Start - span.Start, match.Length, analysis, allowPaths: false);
                break;
        }
    }
}
