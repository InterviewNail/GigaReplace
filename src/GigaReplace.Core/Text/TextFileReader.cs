using System.Security.Cryptography;
using System.Text;

namespace GigaReplace.Core.Text;

/// <summary>A decoded text file plus everything needed to write it back byte-for-byte in the same format.</summary>
public sealed class TextFileContent
{
    public required string Text { get; init; }

    /// <summary>Encodes the body only; the preamble (BOM) is written separately, exactly as found.</summary>
    public required Encoding Encoding { get; init; }

    public required byte[] Preamble { get; init; }

    public required string EncodingName { get; init; }

    /// <summary>Not valid UTF-8; decoded byte-for-byte so untouched bytes survive exactly.</summary>
    public bool IsLegacy8Bit { get; init; }

    public bool CanEncode(string text) => !IsLegacy8Bit || text.All(c => c <= 'ÿ');

    public byte[] Encode(string text)
    {
        var body = Encoding.GetBytes(text);
        if (Preamble.Length == 0)
        {
            return body;
        }

        var all = new byte[Preamble.Length + body.Length];
        Preamble.CopyTo(all, 0);
        body.CopyTo(all, Preamble.Length);
        return all;
    }
}

public enum FileReadStatus
{
    Text,
    Binary,
    TooLarge,
    Unreadable,
}

public sealed record FileReadResult(FileReadStatus Status, TextFileContent? Content, byte[]? Hash, string? Error);

public static class TextFileReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding Utf16Le = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding Utf16Be = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UTF32Encoding Utf32Le = new(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true);
    private static readonly UTF32Encoding Utf32Be = new(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true);

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".pdb", ".so", ".dylib", ".lib", ".a", ".o", ".obj", ".winmd", ".ilk", ".exp",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".icns", ".cur", ".ani", ".tif", ".tiff", ".webp", ".heic", ".psd",
        ".zip", ".7z", ".rar", ".gz", ".tgz", ".tar", ".bz2", ".xz", ".nupkg", ".snupkg", ".vsix", ".msi", ".msm",
        ".msix", ".msixbundle", ".appx", ".appxbundle", ".cab", ".snk", ".pfx", ".p12", ".keystore", ".jks",
        ".ttf", ".otf", ".woff", ".woff2", ".eot", ".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aac", ".wma",
        ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".webm", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".odt", ".ods", ".db", ".sqlite", ".sqlite3", ".mdf", ".ldf", ".bin", ".dat", ".onnx", ".pt", ".pth", ".gguf",
        ".ggml", ".safetensors", ".pb", ".tflite", ".class", ".jar", ".pyc", ".blend", ".fbx", ".glb", ".cache",
        ".resources", ".baml", ".chm", ".swf", ".xnb", ".suo", ".vsidx", ".lock",
    };

    public static bool HasBinaryExtension(string path) => BinaryExtensions.Contains(Path.GetExtension(path));

    public static FileReadResult Read(string fullPath, long maxBytes)
    {
        try
        {
            if (HasBinaryExtension(fullPath))
            {
                return new FileReadResult(FileReadStatus.Binary, null, null, null);
            }

            if (new FileInfo(fullPath).Length > maxBytes)
            {
                return new FileReadResult(FileReadStatus.TooLarge, null, null, null);
            }

            var bytes = File.ReadAllBytes(fullPath);
            var content = TryDecode(bytes);
            return content is null
                ? new FileReadResult(FileReadStatus.Binary, null, null, null)
                : new FileReadResult(FileReadStatus.Text, content, SHA256.HashData(bytes), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileReadResult(FileReadStatus.Unreadable, null, null, ex.Message);
        }
    }

    /// <summary>
    /// Decodes text, or returns null for binary data. A file only counts as text when re-encoding
    /// the decoded string reproduces the original bytes exactly.
    /// </summary>
    public static TextFileContent? TryDecode(byte[] bytes)
    {
        Encoding encoding;
        int bom;
        string name;
        bool legacy = false;

        if (StartsWith(bytes, 0xEF, 0xBB, 0xBF))
        {
            (encoding, bom, name) = (StrictUtf8, 3, "UTF-8 with BOM");
        }
        else if (StartsWith(bytes, 0xFF, 0xFE, 0x00, 0x00))
        {
            (encoding, bom, name) = (Utf32Le, 4, "UTF-32 LE");
        }
        else if (StartsWith(bytes, 0x00, 0x00, 0xFE, 0xFF))
        {
            (encoding, bom, name) = (Utf32Be, 4, "UTF-32 BE");
        }
        else if (StartsWith(bytes, 0xFF, 0xFE))
        {
            (encoding, bom, name) = (Utf16Le, 2, "UTF-16 LE");
        }
        else if (StartsWith(bytes, 0xFE, 0xFF))
        {
            (encoding, bom, name) = (Utf16Be, 2, "UTF-16 BE");
        }
        else
        {
            if (bytes.AsSpan().IndexOf((byte)0) >= 0)
            {
                return null;
            }

            (encoding, bom, name) = (StrictUtf8, 0, "UTF-8");
            if (TryGetString(StrictUtf8, bytes, 0) is null)
            {
                (encoding, name, legacy) = (Encoding.Latin1, "ANSI (8-bit)", true);
            }
        }

        var text = TryGetString(encoding, bytes, bom);
        if (text is null)
        {
            return null;
        }

        if (!encoding.GetBytes(text).AsSpan().SequenceEqual(bytes.AsSpan(bom)))
        {
            return null;
        }

        return new TextFileContent
        {
            Text = text,
            Encoding = encoding,
            Preamble = bytes[..bom],
            EncodingName = name,
            IsLegacy8Bit = legacy,
        };
    }

    private static string? TryGetString(Encoding encoding, byte[] bytes, int offset)
    {
        try
        {
            return encoding.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool StartsWith(byte[] bytes, params byte[] prefix) => bytes.AsSpan().StartsWith(prefix);
}
