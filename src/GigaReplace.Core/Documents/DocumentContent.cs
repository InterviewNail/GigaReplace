using GigaReplace.Core.Pdf;

namespace GigaReplace.Core.Documents;

/// <summary>Where a match sits in a document, whether it can be changed there, and anything worth a look.</summary>
public sealed record DocumentMatchInfo(MatchContext Context, string Detail, string Place, string? ReadOnlyReason, IReadOnlyList<RiskFinding> Risks);

/// <summary>
/// A document (PDF, Word, RTF) seen as one searchable text in which every character knows where
/// it came from, so matches can be judged and the approved ones written back into the file.
/// </summary>
public interface IDocumentContent
{
    /// <summary>"PDF", "Word document", …, for messages.</summary>
    string KindName { get; }

    string Text { get; }

    /// <summary>SHA-256 of the file's bytes when it was read.</summary>
    byte[] Hash { get; }

    /// <summary>Things worth telling the user, such as parts that can be found but not changed.</summary>
    IReadOnlyList<string> Notes { get; }

    /// <summary>False if the range touches headings or labels the text view added.</summary>
    bool IsSearchable(int start, int length);

    DocumentMatchInfo Describe(int start, int length, string replacement, ReplaceProfile profile);

    /// <summary>Why <paramref name="replacement"/> can't be written over the match, or null if it can.</summary>
    string? CannotWrite(int start, int length, string replacement);

    /// <summary>The file's bytes with the approved changes of <paramref name="items"/> written in.</summary>
    byte[] Apply(IEnumerable<ChangeItem> items);
}

/// <summary>Which files are read as documents, and how.</summary>
public static class DocumentReader
{
    /// <summary>True if the profile asks for the contents of files like this one to be searched as a document.</summary>
    public static bool Handles(string path, ReplaceProfile profile) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => profile.SearchPdfs,
        ".docx" or ".docm" or ".dotx" or ".dotm" or ".doc" or ".dot" => profile.SearchWordDocuments,
        ".rtf" => profile.SearchRtfDocuments,
        _ => false,
    };

    public static IDocumentContent Load(string path, byte[] bytes) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => PdfContent.Load(bytes),
        ".doc" or ".dot" => WordBinaryContent.Load(bytes),
        ".rtf" => RtfContent.Load(bytes),
        _ => DocxContent.Load(bytes),
    };
}
