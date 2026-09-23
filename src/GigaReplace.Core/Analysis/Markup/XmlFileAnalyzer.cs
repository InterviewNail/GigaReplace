namespace GigaReplace.Core.Analysis.Markup;

/// <summary>resx, .settings, .config, manifests, WiX sources, .slnx and other XML.</summary>
internal sealed class XmlFileAnalyzer(FileAnalysisContext ctx, FileKind kind) : IFileAnalyzer
{
    private readonly XmlDocumentMap _map = XmlDocumentMap.Build(ctx.Text);

    public MatchAnalysis Analyze(int matchIndex)
    {
        var match = ctx.Matches[matchIndex];
        var analysis = MatchAnalysis.ForWord(ctx.Text, match, MatchContext.MarkupText, "XML");
        if (_map.Find(match.Start) is not { } span)
        {
            return analysis;
        }

        analysis.Context = span.Kind switch
        {
            XmlSpanKind.ElementName or XmlSpanKind.AttributeName => MatchContext.MarkupName,
            XmlSpanKind.AttributeValue => MatchContext.MarkupValue,
            XmlSpanKind.Comment => MatchContext.Comment,
            _ => MatchContext.MarkupText,
        };
        analysis.Detail = span.Kind switch
        {
            XmlSpanKind.ElementName => $"element <{span.Element}>",
            XmlSpanKind.AttributeName => $"attribute {span.Attribute}",
            XmlSpanKind.AttributeValue => $"{span.Attribute} on <{span.Element}>",
            XmlSpanKind.Comment => "XML comment",
            _ => $"text of <{span.Element}>",
        };

        if (span.Kind == XmlSpanKind.Comment)
        {
            return analysis;
        }

        string word = analysis.Word(ctx.Text);
        switch (kind)
        {
            case FileKind.Resx:
                Resx(span, word, analysis);
                break;
            case FileKind.Settings:
                Settings(span, word, analysis);
                break;
            case FileKind.Config:
                Config(span, analysis);
                break;
            case FileKind.AppManifest:
                if (span is { Element: "assemblyIdentity", Attribute: "name" })
                {
                    analysis.Add(RiskCode.AssemblyIdentity, "The application's side-by-side identity in its manifest.");
                }

                break;
            case FileKind.PackageManifest:
                PackageManifest(span, analysis);
                break;
            case FileKind.WixSource:
                Wix(span, analysis);
                break;
            case FileKind.SolutionX:
                if (span.Attribute == "Path")
                {
                    analysis.Detail = "project path in the solution";
                }
                else if (span is { Element: "Folder", Attribute: "Name" })
                {
                    analysis.Detail = "solution folder name";
                }

                break;
        }

        if (analysis.Risks.Count == 0 && span.Kind is XmlSpanKind.AttributeValue or XmlSpanKind.Text or XmlSpanKind.CData)
        {
            ValueHeuristics.Inspect(ctx.Text[span.Start..span.End], match.Start - span.Start, match.Length, analysis, allowPaths: false);
        }

        return analysis;
    }

    private static void Resx(XmlSpan span, string word, MatchAnalysis analysis)
    {
        if (span is { Kind: XmlSpanKind.AttributeValue, Element: "data", Attribute: "name" })
        {
            analysis.Detail = "resource name (a property on the generated Resources class)";
            analysis.SymbolKey = word;
        }
        else if (span.Element == "value")
        {
            analysis.Detail = "resource value";
        }
    }

    private static void Settings(XmlSpan span, string word, MatchAnalysis analysis)
    {
        if (span is { Kind: XmlSpanKind.AttributeValue, Element: "Setting", Attribute: "Name" })
        {
            analysis.Detail = "user setting name";
            analysis.SymbolKey = word;
            analysis.Add(RiskCode.UserSettings, "Saved in user.config under this name, so existing users' saved values won't load after the rename.");
        }
        else if (span is { Kind: XmlSpanKind.AttributeValue, Attribute: "GeneratedClassNamespace" or "GeneratedClassName" })
        {
            analysis.Detail = "generated settings class";
            analysis.SymbolKey = word;
        }
    }

    private static void Config(XmlSpan span, MatchAnalysis analysis)
    {
        if (span is { Kind: XmlSpanKind.AttributeValue, Element: "add", Attribute: "key" or "name" })
        {
            analysis.Add(RiskCode.ConfigKey, "Configuration key. Code that reads it and any deployed config files must use the same name.");
        }
        else if (span is { Kind: XmlSpanKind.ElementName, Parent: "userSettings" or "applicationSettings" })
        {
            analysis.Add(RiskCode.UserSettings, "Settings section named after the settings class. Existing user.config files use the old name, so saved values won't load.");
        }
        else if (span is { Kind: XmlSpanKind.AttributeValue, Element: "setting", Attribute: "name" })
        {
            analysis.Add(RiskCode.UserSettings, "Setting name. Existing user.config files use the old name, so saved values won't load.");
        }
        else if (span is { Kind: XmlSpanKind.AttributeValue, Attribute: "type" })
        {
            analysis.Add(RiskCode.RuntimeName, "Type name resolved at run time. It must match the renamed code and assembly exactly.");
        }
    }

    private static void PackageManifest(XmlSpan span, MatchAnalysis analysis)
    {
        switch (span)
        {
            case { Element: "Identity", Kind: XmlSpanKind.AttributeValue }:
                analysis.Add(RiskCode.PackageIdentity, "Package identity. Changing it makes the OS or marketplace treat this as a different product (separate data, no upgrade path).");
                break;
            case { Element: "Application", Attribute: "Id" }:
                analysis.Add(RiskCode.InstallerIdentity, "Application ID inside the package manifest.");
                break;
            case { Element: "uap:Protocol", Attribute: "Name" }:
                analysis.Add(RiskCode.ExternalContract, "URL protocol name. Links using the old scheme stop opening the app.");
                break;
            case { Element: "id", Kind: XmlSpanKind.Text }:
                analysis.Add(RiskCode.PackageIdentity, "NuGet package ID. Consumers reference this exact ID.");
                break;
        }
    }

    private static void Wix(XmlSpan span, MatchAnalysis analysis)
    {
        switch (span)
        {
            case { Kind: XmlSpanKind.AttributeValue, Element: "Directory" or "StandardDirectory" or "Folder", Attribute: "Name" }:
                analysis.Add(RiskCode.InstallerIdentity, "Install folder name. Existing installs live under the old folder, so an upgrade may leave files behind.");
                break;
            case { Kind: XmlSpanKind.AttributeValue, Element: "RegistryKey" or "RegistryValue", Attribute: "Key" }:
                analysis.Add(RiskCode.RegistryKey, "Registry key written by the installer. Settings stored under the old key won't be read.");
                break;
            case { Kind: XmlSpanKind.AttributeValue, Element: "Package" or "Product" or "Bundle", Attribute: "Name" or "Manufacturer" }:
                analysis.Add(RiskCode.InstallerIdentity, "Product name or manufacturer shown in Installed apps, and used for install paths.");
                break;
            default:
                analysis.Add(RiskCode.InstallerIdentity, "Installer definition. Names and IDs here identify the installed product and its files.");
                break;
        }
    }
}
