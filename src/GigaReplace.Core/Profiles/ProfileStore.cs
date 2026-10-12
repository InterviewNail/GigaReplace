using System.Text;
using System.Text.Json;
using GigaReplace.Core.Planning;

namespace GigaReplace.Core.Profiles;

public static class ProfileStore
{
    public const string FileFilter = "GigaReplace profile (*.grprofile.json)|*.grprofile.json|JSON (*.json)|*.json";

    public static string AppDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GigaReplace");

    public static string LastProfilePath => Path.Combine(AppDataFolder, "last-profile.json");

    public static ReplaceProfile Load(string path)
    {
        var profile = JsonSerializer.Deserialize<ReplaceProfile>(File.ReadAllText(path), ReplaceProfile.JsonOptions)
            ?? throw new InvalidDataException("The profile file is empty.");
        profile.Rules ??= [];
        profile.ExcludePatterns ??= [.. ReplaceProfile.DefaultExcludePatterns];
        profile.WatchList ??= [];
        profile.RiskChecks ??= new RiskCheckSettings();
        return profile;
    }

    public static void Save(ReplaceProfile profile, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profile, ReplaceProfile.JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public static ReplaceProfile? TryLoadLast()
    {
        try
        {
            return File.Exists(LastProfilePath) ? Load(LastProfilePath) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void SaveLast(ReplaceProfile profile)
    {
        try
        {
            Save(profile, LastProfilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Remembering the last profile is a convenience only.
        }
    }
}

public static class CsvReport
{
    public static void Write(ScanResult scan, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,Kind,Path,Line,Column,Context,Detail,Before,After,Decision,Will change,Categories,Reasons");
        foreach (var item in scan.Items)
        {
            sb.AppendJoin(',',
                item.Id.ToString(),
                Escape(item.KindDisplay),
                Escape(item.RelativePath),
                Escape(item.LineDisplay),
                item.Place is null ? item.Column.ToString() : "",
                Escape(item.ContextDisplay),
                Escape(item.ContextDetail),
                Escape(item.WordText),
                Escape(item.WordFinalText),
                item.Decision.ToString(),
                item.WillChange ? "yes" : "no",
                Escape(string.Join("; ", item.Risks.Select(r => r.CategoryTitle).Distinct())),
                Escape(string.Join(" | ", item.Risks.Select(r => $"{r.Title}: {r.Message}"))));
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Escape(string value)
    {
        // Leading =, +, - or @ would be read as a formula by spreadsheet apps.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
