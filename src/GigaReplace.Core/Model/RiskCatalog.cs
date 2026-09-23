namespace GigaReplace.Core;

/// <summary>A reason a change needs the user's decision.</summary>
public sealed record RiskFinding(RiskCode Code, string Message)
{
    public RiskCategory Category => RiskCatalog.CategoryOf(Code);

    public string Title => RiskCatalog.Title(Code);

    public string CategoryTitle => RiskCatalog.Title(Category);
}

public static class RiskCatalog
{
    public static RiskCategory CategoryOf(RiskCode code) => code switch
    {
        RiskCode.MidWord or RiskCode.AmbiguousCasing or RiskCode.ExternalSymbol or RiskCode.NameCollision
            or RiskCode.Shadowing or RiskCode.InvalidIdentifier or RiskCode.InvalidFileName
            or RiskCode.RenameTargetExists or RiskCode.ExternalPackage or RiskCode.NativeLibrary
            => RiskCategory.BuildBreaker,
        RiskCode.AssemblyIdentity or RiskCode.DefaultProjectNames or RiskCode.InstallerIdentity
            or RiskCode.PackageIdentity or RiskCode.AppUserModelId or RiskCode.AssemblyReference
            => RiskCategory.AppIdentity,
        RiskCode.WatchListTerm => RiskCategory.WatchList,
        _ => RiskCategory.PersistedData,
    };

    public static string Title(RiskCode code) => code switch
    {
        RiskCode.MidWord => "Mid-word match",
        RiskCode.AmbiguousCasing => "Unclear casing",
        RiskCode.ExternalSymbol => "Not declared here",
        RiskCode.NameCollision => "Name clash",
        RiskCode.Shadowing => "Hides another name",
        RiskCode.InvalidIdentifier => "Invalid code name",
        RiskCode.InvalidFileName => "Invalid file name",
        RiskCode.RenameTargetExists => "Name already taken",
        RiskCode.ExternalPackage => "External package",
        RiskCode.NativeLibrary => "Native library",
        RiskCode.SerializedMember => "Serialized member",
        RiskCode.SerializedTypeName => "Serialized type name",
        RiskCode.SerializedKey => "Serialized name",
        RiskCode.ConfigKey => "Configuration key",
        RiskCode.UserSettings => "User setting",
        RiskCode.FileSystemName => "File or folder on disk",
        RiskCode.RegistryKey => "Registry key",
        RiskCode.UrlOrDomain => "Web address",
        RiskCode.IpcName => "Mutex or pipe name",
        RiskCode.EnvironmentVariable => "Environment variable",
        RiskCode.ResourceKey => "Resource key",
        RiskCode.RuntimeName => "Looked up by name",
        RiskCode.ExternalContract => "External contract",
        RiskCode.NamedValue => "Stored value",
        RiskCode.AssemblyIdentity => "Assembly identity",
        RiskCode.DefaultProjectNames => "Default names change",
        RiskCode.InstallerIdentity => "Installer identity",
        RiskCode.PackageIdentity => "Package identity",
        RiskCode.AppUserModelId => "Windows app ID",
        RiskCode.AssemblyReference => "Assembly name reference",
        RiskCode.WatchListTerm => "Watch-list",
        _ => code.ToString(),
    };

    public static string Title(RiskCategory category) => category switch
    {
        RiskCategory.BuildBreaker => "Build breakers",
        RiskCategory.PersistedData => "Persisted/external data",
        RiskCategory.AppIdentity => "App identity",
        RiskCategory.WatchList => "Watch-list",
        _ => category.ToString(),
    };
}
