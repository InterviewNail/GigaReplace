namespace GigaReplace.Core;

/// <summary>Where approved changes are written.</summary>
public enum WriteMode
{
    /// <summary>Copy the tree to a new folder and change only the copy.</summary>
    CopyToFolder,

    /// <summary>Edit the source tree directly, keeping an undo journal.</summary>
    InPlace,
}

/// <summary>How risky matches are put to the user.</summary>
public enum PromptMode
{
    /// <summary>Scan everything, then decide on a review screen.</summary>
    ReviewFirst,

    /// <summary>Ask about each risky match as the run reaches it.</summary>
    PromptDuringRun,
}

public enum RuleOrigin
{
    User,
    IdentityGuid,
}

public enum ChangeKind
{
    Content,
    FileRename,
    FolderRename,
}

public enum Decision
{
    Pending,
    Replace,
    Skip,
    Custom,
}

/// <summary>What kind of text a match sits in.</summary>
public enum MatchContext
{
    Declaration,
    Identifier,
    Code,
    String,
    Comment,
    Preprocessor,
    InactiveCode,
    MarkupName,
    MarkupValue,
    MarkupText,
    JsonKey,
    JsonValue,
    Text,
    FileName,
    FolderName,
    IdentityGuid,
}

public enum RiskCategory
{
    BuildBreaker,
    PersistedData,
    AppIdentity,
    WatchList,
}

public enum RiskCode
{
    // Build breakers
    MidWord,
    AmbiguousCasing,
    ExternalSymbol,
    NameCollision,
    Shadowing,
    InvalidIdentifier,
    InvalidFileName,
    RenameTargetExists,
    ExternalPackage,
    NativeLibrary,

    // Persisted / external data
    SerializedMember,
    SerializedTypeName,
    SerializedKey,
    ConfigKey,
    UserSettings,
    FileSystemName,
    RegistryKey,
    UrlOrDomain,
    IpcName,
    EnvironmentVariable,
    ResourceKey,
    RuntimeName,
    ExternalContract,
    NamedValue,

    // App identity
    AssemblyIdentity,
    DefaultProjectNames,
    InstallerIdentity,
    PackageIdentity,
    AppUserModelId,
    AssemblyReference,

    // Watch-list
    WatchListTerm,
}

public static class ContextNames
{
    public static string Display(MatchContext context) => context switch
    {
        MatchContext.Declaration => "Declaration",
        MatchContext.Identifier => "Code name",
        MatchContext.Code => "Code",
        MatchContext.String => "String",
        MatchContext.Comment => "Comment",
        MatchContext.Preprocessor => "Preprocessor",
        MatchContext.InactiveCode => "Inactive code",
        MatchContext.MarkupName => "Markup name",
        MatchContext.MarkupValue => "Markup value",
        MatchContext.MarkupText => "Markup text",
        MatchContext.JsonKey => "JSON key",
        MatchContext.JsonValue => "JSON value",
        MatchContext.Text => "Text",
        MatchContext.FileName => "File name",
        MatchContext.FolderName => "Folder name",
        MatchContext.IdentityGuid => "Identity GUID",
        _ => context.ToString(),
    };
}
