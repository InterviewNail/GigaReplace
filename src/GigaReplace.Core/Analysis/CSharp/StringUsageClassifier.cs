using GigaReplace.Core.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GigaReplace.Core.Analysis.CSharp;

/// <summary>Works out what a string literal is used for: a file name, a registry key, a config key, UI text ….</summary>
internal static class StringUsageClassifier
{
    private enum Usage
    {
        None,
        Invocation,
        Creation,
        Indexer,
        Attribute,
        Initializer,
        Assignment,
        Return,
        Comparison,
    }

    private readonly record struct UsageInfo(Usage Kind, string Description, string? Name = null, string? Receiver = null);

    private static readonly HashSet<string> FileSystemMethods = new(StringComparer.Ordinal)
    {
        "Combine", "Join", "GetFullPath", "CreateDirectory", "Exists", "ReadAllText", "ReadAllTextAsync", "ReadAllLines",
        "ReadAllLinesAsync", "ReadAllBytes", "ReadAllBytesAsync", "ReadLines", "WriteAllText", "WriteAllTextAsync",
        "WriteAllLines", "WriteAllLinesAsync", "WriteAllBytes", "WriteAllBytesAsync", "AppendAllText",
        "AppendAllTextAsync", "AppendAllLines", "Open", "OpenRead", "OpenWrite", "OpenText", "Create", "CreateText",
        "AppendText", "Delete", "Move", "Copy", "Replace", "GetFiles", "GetDirectories", "EnumerateFiles",
        "EnumerateDirectories", "EnumerateFileSystemEntries", "GetFileSystemEntries", "SetCurrentDirectory",
        "ChangeExtension", "SetAttributes", "GetAttributes",
    };

    private static readonly string[] FileSystemReceivers = ["Path", "File", "Directory", "FileInfo", "DirectoryInfo"];

    private static readonly HashSet<string> FileSystemTypes = new(StringComparer.Ordinal)
    {
        "FileStream", "StreamReader", "StreamWriter", "FileInfo", "DirectoryInfo", "FileSystemWatcher",
        "PhysicalFileProvider", "SqliteConnection", "SQLiteConnection", "LiteDatabase",
    };

    private static readonly HashSet<string> ConfigFileMethods = new(StringComparer.Ordinal) { "AddJsonFile", "AddXmlFile", "AddIniFile" };

    private static readonly HashSet<string> RegistryMethods = new(StringComparer.Ordinal)
    {
        "OpenSubKey", "CreateSubKey", "DeleteSubKey", "DeleteSubKeyTree", "DeleteValue", "OpenBaseKey", "OpenRemoteBaseKey",
    };

    private static readonly HashSet<string> IpcTypes = new(StringComparer.Ordinal)
    {
        "Mutex", "Semaphore", "EventWaitHandle", "NamedPipeServerStream", "NamedPipeClientStream", "MemoryMappedFile",
    };

    private static readonly HashSet<string> IpcMethods = new(StringComparer.Ordinal)
    {
        "OpenExisting", "TryOpenExisting", "CreateOrOpen", "CreateNew", "OpenOrCreate",
    };

    private static readonly HashSet<string> EnvironmentMethods = new(StringComparer.Ordinal)
    {
        "GetEnvironmentVariable", "SetEnvironmentVariable", "ExpandEnvironmentVariables", "AddEnvironmentVariables",
    };

    private static readonly HashSet<string> ConfigMethods = new(StringComparer.Ordinal)
    {
        "GetSection", "GetRequiredSection", "GetConnectionString", "GetValue", "Bind",
    };

    private static readonly HashSet<string> ResourceMethods = new(StringComparer.Ordinal)
    {
        "FindResource", "TryFindResource", "GetManifestResourceStream", "GetManifestResourceInfo",
    };

    private static readonly HashSet<string> ReflectionMethods = new(StringComparer.Ordinal)
    {
        "GetType", "Load", "LoadFrom", "LoadFile", "LoadWithPartialName", "CreateInstance", "CreateInstanceFrom",
        "GetMethod", "GetProperty", "GetField", "GetEvent", "GetMember", "GetNestedType", "GetInterface", "InvokeMember",
        "OnPropertyChanged", "RaisePropertyChanged", "NotifyPropertyChanged", "OnPropertyChanging",
    };

    private static readonly HashSet<string> HttpMethods = new(StringComparer.Ordinal)
    {
        "GetAsync", "PostAsync", "PutAsync", "DeleteAsync", "PatchAsync", "SendAsync", "GetStringAsync",
        "GetStreamAsync", "GetByteArrayAsync", "GetFromJsonAsync", "PostAsJsonAsync", "PutAsJsonAsync",
        "PatchAsJsonAsync", "DeleteFromJsonAsync", "Navigate", "LaunchUriAsync", "Uri",
    };

    private static readonly HashSet<string> DisplayMethods = new(StringComparer.Ordinal)
    {
        "WriteLine", "Write", "Show", "Information", "Warning", "Error", "Debug", "Verbose", "Fatal", "Trace",
        "TraceInformation", "TraceWarning", "TraceError", "Assert", "Fail", "Append", "AppendLine", "Print",
    };

    private static readonly string[] RegistryWords = ["registry", "reg", "hive", "subkey", "regkey"];
    private static readonly string[] IpcWords = ["mutex", "pipe", "semaphore", "ipc"];
    private static readonly string[] UrlWords = ["url", "uri", "endpoint", "host", "hostname", "domain", "website", "homepage", "href", "link", "api", "route", "server"];
    private static readonly string[] EnvironmentWords = ["env", "environment"];
    private static readonly string[] PathWords = ["folder", "directory", "dir", "path", "file", "filename", "extension", "db", "database", "log", "logs", "appdata"];

    private static readonly string[] StoredWords =
    [
        "key", "keys", "section", "setting", "settings", "config", "configuration", "option", "options", "header",
        "cookie", "claim", "scope", "queue", "topic", "channel", "container", "bucket", "table", "column", "collection",
        "store", "storage", "schema", "scheme", "prefix", "tag", "id", "identifier", "guid", "token", "secret",
        "tenant", "audience", "issuer",
    ];

    public static MatchAnalysis Classify(FileAnalysisContext ctx, SyntaxToken token, RawMatch match)
    {
        var (wordStart, wordLength) = WordShape.ExpandToWord(ctx.Text, match.Start, match.Length);
        int tokenStart = token.Span.Start, tokenEnd = token.Span.End;
        if (wordStart < tokenStart)
        {
            wordLength -= tokenStart - wordStart;
            wordStart = tokenStart;
        }

        if (wordStart + wordLength > tokenEnd)
        {
            wordLength = tokenEnd - wordStart;
        }

        var usage = UsageOf(token);
        var analysis = new MatchAnalysis
        {
            Context = MatchContext.String,
            Detail = usage.Description,
            WordStart = wordStart,
            WordLength = wordLength,
        };

        bool displayOnly = ByContext(usage, analysis);

        // Inspect the unescaped value, so "\n" isn't mistaken for a path separator.
        string matched = ctx.Text.Substring(match.Start, match.Length);
        string value = token.ValueText;
        int rawIndex = match.Start - tokenStart;
        int valueIndex = LocateInValue(value, matched, rawIndex - QuotePrefixLength(token));
        if (valueIndex < 0)
        {
            (value, valueIndex) = (token.Text, rawIndex);
        }

        if (analysis.Risks.Count == 0)
        {
            ValueHeuristics.Inspect(value, valueIndex, match.Length, analysis, allowPaths: !displayOnly);
        }

        if (analysis.Risks.Count == 0 && !displayOnly && value.Length > 2
            && (ctx.Index.TypeNames.Contains(value) || ctx.Index.MemberNames.Contains(value)))
        {
            analysis.Add(
                RiskCode.RuntimeName,
                $"This text is exactly the code name “{value}”. It may be used for reflection, serialization, property-change notifications or lookups, where the compiler can't check it.");
        }

        return analysis;
    }

    private static UsageInfo UsageOf(SyntaxToken token)
    {
        SyntaxNode? node = token.Parent;
        if (node is InterpolatedStringTextSyntax)
        {
            node = node.Parent;
        }

        if (node is null)
        {
            return new UsageInfo(Usage.None, "string");
        }

        while (node.Parent is { } wrapper && IsWrapper(wrapper))
        {
            node = wrapper;
        }

        switch (node.Parent)
        {
            case ArgumentSyntax argument:
                switch (argument.Parent?.Parent)
                {
                    case InvocationExpressionSyntax invocation:
                        var (name, receiver) = Invoked(invocation.Expression);
                        return new UsageInfo(Usage.Invocation, $"string passed to {Display(receiver, name)}(…)", name, receiver);
                    case ObjectCreationExpressionSyntax creation:
                        var typeName = Names.Simple(creation.Type);
                        return new UsageInfo(Usage.Creation, $"string passed to new {typeName}(…)", typeName);
                    case ImplicitObjectCreationExpressionSyntax implicitCreation:
                        var targetType = TargetTypeOf(implicitCreation);
                        return new UsageInfo(
                            Usage.Creation,
                            targetType is null ? "string passed to a constructor" : $"string passed to new {targetType}(…)",
                            targetType);
                    case ElementAccessExpressionSyntax access:
                        var target = access.Expression.ToString();
                        return new UsageInfo(Usage.Indexer, $"key in {Shorten(target)}[…]", null, target);
                }

                return new UsageInfo(Usage.None, "string argument");

            case AttributeArgumentSyntax attributeArgument:
                var attribute = attributeArgument.FirstAncestorOrSelf<AttributeSyntax>();
                var attributeName = attribute is null ? "attribute" : Names.AttributeName(attribute.Name);
                return new UsageInfo(Usage.Attribute, $"string in [{attributeName}]", attributeName);

            case EqualsValueClauseSyntax equalsValue:
                string? declared = equalsValue.Parent switch
                {
                    VariableDeclaratorSyntax v => v.Identifier.ValueText,
                    PropertyDeclarationSyntax p => p.Identifier.ValueText,
                    ParameterSyntax p => p.Identifier.ValueText,
                    EnumMemberDeclarationSyntax e => e.Identifier.ValueText,
                    _ => null,
                };
                return new UsageInfo(Usage.Initializer, declared is null ? "string value" : $"value of {declared}", declared);

            case AssignmentExpressionSyntax assignment when assignment.Right == node:
                string? assigned = assignment.Left switch
                {
                    IdentifierNameSyntax i => i.Identifier.ValueText,
                    MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                    _ => null,
                };
                return new UsageInfo(Usage.Assignment, assigned is null ? "assigned string" : $"value assigned to {assigned}", assigned);

            case ArrowExpressionClauseSyntax or ReturnStatementSyntax:
                string? member = node.FirstAncestorOrSelf<MemberDeclarationSyntax>() switch
                {
                    PropertyDeclarationSyntax p => p.Identifier.ValueText,
                    MethodDeclarationSyntax m => m.Identifier.ValueText,
                    _ => null,
                };
                return new UsageInfo(Usage.Return, member is null ? "returned string" : $"value returned by {member}", member);

            case BinaryExpressionSyntax or CaseSwitchLabelSyntax or ConstantPatternSyntax or IsPatternExpressionSyntax:
                return new UsageInfo(Usage.Comparison, "string compared with another value");
        }

        return new UsageInfo(Usage.None, "string");
    }

    /// <summary>The type a target-typed <c>new(…)</c> creates, from the declaration it initializes.</summary>
    private static string? TargetTypeOf(ExpressionSyntax creation)
    {
        var parent = creation.Parent;
        while (parent is ParenthesizedExpressionSyntax or CastExpressionSyntax)
        {
            parent = parent.Parent;
        }

        return parent switch
        {
            EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } } => Names.Simple(declaration.Type),
            EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax property } => Names.Simple(property.Type),
            EqualsValueClauseSyntax { Parent: ParameterSyntax parameter } => Names.Simple(parameter.Type),
            ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax property } => Names.Simple(property.Type),
            ArrowExpressionClauseSyntax { Parent: MethodDeclarationSyntax method } => Names.Simple(method.ReturnType),
            ReturnStatementSyntax => creation.FirstAncestorOrSelf<MethodDeclarationSyntax>() is { } method ? Names.Simple(method.ReturnType) : null,
            _ => null,
        };
    }

    private static bool IsWrapper(SyntaxNode node) => node switch
    {
        ParenthesizedExpressionSyntax or CastExpressionSyntax or ConditionalExpressionSyntax or InitializerExpressionSyntax
            or ArrayCreationExpressionSyntax or ImplicitArrayCreationExpressionSyntax or CollectionExpressionSyntax
            or ExpressionElementSyntax or SwitchExpressionArmSyntax or SwitchExpressionSyntax or InterpolationSyntax
            or InterpolatedStringExpressionSyntax or AwaitExpressionSyntax or PostfixUnaryExpressionSyntax => true,
        BinaryExpressionSyntax binary => !binary.IsKind(SyntaxKind.EqualsExpression) && !binary.IsKind(SyntaxKind.NotEqualsExpression)
            && !binary.IsKind(SyntaxKind.IsExpression) && !binary.IsKind(SyntaxKind.AsExpression),
        _ => false,
    };

    private static (string? Name, string? Receiver) Invoked(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax access => (access.Name.Identifier.ValueText, access.Expression.ToString()),
        MemberBindingExpressionSyntax binding => (binding.Name.Identifier.ValueText, null),
        GenericNameSyntax generic => (generic.Identifier.ValueText, null),
        IdentifierNameSyntax identifier => (identifier.Identifier.ValueText, null),
        _ => (null, null),
    };

    /// <summary>Adds a finding based on how the string is used. Returns true when it's only shown or logged.</summary>
    private static bool ByContext(UsageInfo usage, MatchAnalysis analysis)
    {
        string name = usage.Name ?? "";
        string receiver = usage.Receiver ?? "";
        switch (usage.Kind)
        {
            case Usage.Attribute:
                AttributeRisk(name, analysis);
                return false;

            case Usage.Invocation or Usage.Creation:
                if (DisplayMethods.Contains(name) || name.StartsWith("Log", StringComparison.Ordinal))
                {
                    return true;
                }

                if (ConfigFileMethods.Contains(name) || FileSystemTypes.Contains(name)
                    || (FileSystemMethods.Contains(name) && FileSystemReceivers.Any(r => receiver == r || receiver.EndsWith("." + r, StringComparison.Ordinal))))
                {
                    analysis.Add(RiskCode.FileSystemName, "Used as a file or folder name on disk. Files that existing installs already created under the old name won't be found.");
                }
                else if (RegistryMethods.Contains(name)
                    || (name is "GetValue" or "SetValue" && (receiver.Contains("Registry", StringComparison.Ordinal) || receiver.Contains("key", StringComparison.OrdinalIgnoreCase))))
                {
                    analysis.Add(RiskCode.RegistryKey, "Registry key or value name. Settings already stored under the old name won't be read.");
                }
                else if (IpcTypes.Contains(name) || (IpcMethods.Contains(name) && IpcTypes.Any(t => receiver.EndsWith(t, StringComparison.Ordinal))))
                {
                    analysis.Add(RiskCode.IpcName, "Named mutex, pipe, event or shared memory. Old and new builds won't see each other, which affects single-instance checks and messages between processes.");
                }
                else if (EnvironmentMethods.Contains(name))
                {
                    analysis.Add(RiskCode.EnvironmentVariable, "Environment variable name. Machines and scripts that set the old name won't be picked up.");
                }
                else if (ConfigMethods.Contains(name))
                {
                    analysis.Add(RiskCode.ConfigKey, "Configuration key. Existing appsettings files, user secrets and environment overrides use the old name.");
                }
                else if (ResourceMethods.Contains(name) || (name is "GetString" or "GetObject" or "GetStream" && receiver.Contains("Resource", StringComparison.Ordinal)))
                {
                    analysis.Add(
                        RiskCode.ResourceKey,
                        name.StartsWith("GetManifestResource", StringComparison.Ordinal)
                            ? "Embedded resource name. It's built from the project's root namespace and folder path, so it must match after the rename."
                            : "Resource key. It must match the key defined in the .resx or XAML resources.");
                }
                else if (ReflectionMethods.Contains(name))
                {
                    analysis.Add(RiskCode.RuntimeName, "Looked up by name at run time. It must match the renamed code exactly, and the compiler can't check it.");
                }
                else if (name == "SetCurrentProcessExplicitAppUserModelID")
                {
                    analysis.Add(RiskCode.AppUserModelId, "Windows app user model ID. It groups taskbar buttons and routes notifications; changing it makes Windows treat this as a different app.");
                }
                else if (HttpMethods.Contains(name))
                {
                    analysis.Add(RiskCode.UrlOrDomain, "Address of a web page or service. The old address may still be the live one.");
                }
                else if (name is "Add" or "TryAddWithoutValidation" or "Remove" && receiver.EndsWith("Headers", StringComparison.Ordinal))
                {
                    analysis.Add(RiskCode.ExternalContract, "HTTP header name. The server or client on the other end expects this exact name.");
                }

                return false;

            case Usage.Indexer:
                if (receiver.Contains("onfig", StringComparison.Ordinal) || receiver.Contains("Settings", StringComparison.OrdinalIgnoreCase))
                {
                    analysis.Add(RiskCode.ConfigKey, "Configuration key. Existing config files and environment overrides use the old name.");
                }
                else if (receiver.Contains("Resources", StringComparison.Ordinal))
                {
                    analysis.Add(RiskCode.ResourceKey, "Resource key. It must match the key defined in the .resx or XAML resources.");
                }
                else if (receiver.EndsWith("Headers", StringComparison.Ordinal))
                {
                    analysis.Add(RiskCode.ExternalContract, "HTTP header name. The other end expects this exact name.");
                }
                else if (receiver.Contains("Environment", StringComparison.Ordinal))
                {
                    analysis.Add(RiskCode.EnvironmentVariable, "Environment variable name. Machines and scripts that set the old name won't be picked up.");
                }

                return false;

            case Usage.Initializer or Usage.Assignment or Usage.Return:
                NameHint(usage.Name, analysis);
                return false;
        }

        return false;
    }

    private static void AttributeRisk(string attribute, MatchAnalysis analysis)
    {
        switch (attribute)
        {
            case "JsonPropertyName" or "JsonProperty" or "DataMember" or "DataContract" or "XmlElement" or "XmlAttribute"
                or "XmlRoot" or "XmlType" or "XmlEnum" or "XmlArray" or "XmlArrayItem" or "Column" or "Table"
                or "BsonElement" or "JsonDerivedType" or "EnumMember" or "JsonStringEnumMemberName" or "JsonPolymorphic"
                or "ProtoMember" or "Key":
                analysis.Add(RiskCode.SerializedKey, $"The name written into serialized data ([{attribute}]). Saved files, database columns or API payloads use this exact text.");
                break;
            case "InternalsVisibleTo":
                analysis.Add(RiskCode.AssemblyIdentity, "[InternalsVisibleTo] names another assembly. It must match that assembly's real name.");
                break;
            case "AssemblyTitle" or "AssemblyProduct" or "AssemblyCompany" or "AssemblyDescription" or "AssemblyTrademark"
                or "AssemblyCopyright" or "AssemblyInformationalVersion" or "AssemblyConfiguration":
                analysis.Add(RiskCode.AssemblyIdentity, $"Assembly metadata ([{attribute}]) shows in the file's properties. Company and product names also decide where .NET keeps user settings.");
                break;
            case "DllImport" or "LibraryImport":
                analysis.Add(RiskCode.NativeLibrary, $"[{attribute}] names a native DLL. That file must really have the new name, or the call fails at run time.");
                break;
            case "Route" or "RoutePrefix" or "HttpGet" or "HttpPost" or "HttpPut" or "HttpDelete" or "HttpPatch" or "HttpHead" or "HttpOptions":
                analysis.Add(RiskCode.ExternalContract, $"Web route ([{attribute}]). Existing clients call this exact path.");
                break;
            case "ConfigurationProperty" or "SettingsProvider":
                analysis.Add(RiskCode.ConfigKey, $"Configuration name ([{attribute}]). Existing config files use the old name.");
                break;
            case "NotifyPropertyChangedFor" or "NotifyCanExecuteChangedFor" or "DependsOn":
                analysis.Add(RiskCode.RuntimeName, $"Names another member ([{attribute}]). It must match that member's final name.");
                break;
        }
    }

    private static void NameHint(string? name, MatchAnalysis analysis)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        var words = WordShape.LowerWords(name);
        if (words.Overlaps(RegistryWords))
        {
            analysis.Add(RiskCode.RegistryKey, $"Stored in “{name}”, which sounds like a registry location. Settings already stored under the old key won't be read.");
        }
        else if (words.Overlaps(IpcWords))
        {
            analysis.Add(RiskCode.IpcName, $"Stored in “{name}”, which sounds like a mutex or pipe name. Old and new builds won't see each other.");
        }
        else if (words.Overlaps(UrlWords))
        {
            analysis.Add(RiskCode.UrlOrDomain, $"Stored in “{name}”, which sounds like a web address. The old address may still be the live one.");
        }
        else if (words.Overlaps(EnvironmentWords))
        {
            analysis.Add(RiskCode.EnvironmentVariable, $"Stored in “{name}”, which sounds like an environment variable. Machines and scripts that set the old name won't match.");
        }
        else if (words.Overlaps(PathWords))
        {
            analysis.Add(RiskCode.FileSystemName, $"Stored in “{name}”, which sounds like a file or folder name. Files that existing installs created under the old name won't be found.");
        }
        else if (words.Overlaps(StoredWords))
        {
            analysis.Add(RiskCode.NamedValue, $"Stored in “{name}”, which sounds like a key or ID that's saved or shared outside the app.");
        }
    }

    private static int QuotePrefixLength(SyntaxToken token)
    {
        if (token.IsKind(SyntaxKind.InterpolatedStringTextToken))
        {
            return 0;
        }

        int quote = token.Text.IndexOf('"');
        if (quote < 0)
        {
            quote = token.Text.IndexOf('\'');
        }

        int end = quote;
        while (end < token.Text.Length && end >= 0 && token.Text[end] is '"' or '\'')
        {
            end++;
        }

        return Math.Max(end, 0);
    }

    private static int LocateInValue(string value, string matched, int approximate)
    {
        int best = -1, bestDistance = int.MaxValue;
        for (int i = value.IndexOf(matched, StringComparison.Ordinal); i >= 0; i = value.IndexOf(matched, i + 1, StringComparison.Ordinal))
        {
            int distance = Math.Abs(i - approximate);
            if (distance < bestDistance)
            {
                (best, bestDistance) = (i, distance);
            }
        }

        return best;
    }

    private static string Display(string? receiver, string? name) =>
        string.IsNullOrEmpty(receiver) ? name ?? "a method" : $"{Shorten(receiver)}.{name}";

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..37] + "…";
}
