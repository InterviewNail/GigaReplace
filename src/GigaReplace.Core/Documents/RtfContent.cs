using System.Security.Cryptography;
using System.Text;
using GigaReplace.Core.Text;

namespace GigaReplace.Core.Documents;

/// <summary>
/// An RTF document, read as the text it shows rather than its markup. Each character remembers
/// the bytes that produced it (a plain byte, a \'hh escape, a \uN sequence or a symbol word), so
/// a replacement can be written over exactly those bytes and nothing else.
/// </summary>
public sealed class RtfContent : IDocumentContent
{
    /// <summary>Destinations that never hold visible text.</summary>
    private static readonly HashSet<string> SkippedDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "listtable", "listoverridetable", "revtbl", "rsidtbl", "generator",
        "xmlnstbl", "mmathPr", "pict", "object", "objdata", "themedata", "colorschememapping", "datastore",
        "latentstyles", "pgdsctbl", "filetbl", "fldinst", "bkmkstart", "bkmkend", "shppict", "nonshppict",
        "picprop", "sp", "sn", "sv", "userprops", "docvar", "template", "falt", "panose", "fname", "leveltext",
        "levelnumbers", "listname", "listpicture", "protusertbl", "wgrffmtfilter", "background", "passwordhash",
    };

    private static readonly Dictionary<string, string> InfoProperties = new(StringComparer.Ordinal)
    {
        ["title"] = "Title", ["subject"] = "Subject", ["author"] = "Author", ["keywords"] = "Keywords",
        ["doccomm"] = "Comments", ["company"] = "Company", ["manager"] = "Manager", ["category"] = "Category",
        ["operator"] = "Last saved by",
    };

    private static readonly Dictionary<string, char> SymbolWords = new(StringComparer.Ordinal)
    {
        ["emdash"] = '—', ["endash"] = '–', ["lquote"] = '‘', ["rquote"] = '’', ["ldblquote"] = '“', ["rdblquote"] = '”',
        ["bullet"] = '•', ["emspace"] = ' ', ["enspace"] = ' ', ["qmspace"] = ' ',
    };

    private readonly byte[] _bytes;
    private readonly List<Span> _spans = [];
    private readonly List<string> _properties = [];
    private readonly DocumentTextBuilder _text = new();
    private Encoding _codePage = Encoding.GetEncoding(1252);

    static RtfContent() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private RtfContent(byte[] bytes)
    {
        _bytes = bytes;
        Hash = SHA256.HashData(bytes);
    }

    public string KindName => "RTF document";

    public string Text => _text.Text;

    public byte[] Hash { get; }

    public IReadOnlyList<string> Notes { get; } = [];

    /// <summary>The bytes behind one character, with the state needed to write a replacement there.</summary>
    private sealed record Span(int Start, int End, int Uc, string Format, string? Field);

    private sealed class State
    {
        public bool Skip;
        public bool Property;
        public bool AtStart = true;
        public bool CaptureInstruction;
        public bool Story;
        public int Uc = 1;
        public string Format = "";
        public string? Field;

        public State Child() => new() { Skip = Skip, Property = Property, Uc = Uc, Format = Format, Field = Field, CaptureInstruction = CaptureInstruction };
    }

    public static RtfContent Load(byte[] bytes)
    {
        if (!bytes.AsSpan().StartsWith("{\\rtf"u8))
        {
            throw new InvalidDataException("It isn't an RTF document (it doesn't start with {\\rtf).");
        }

        var content = new RtfContent(bytes);
        content.Parse();
        return content;
    }

    private void Parse()
    {
        var data = _bytes;
        var stack = new Stack<State>();
        var state = new State();
        var instruction = new StringBuilder();
        bool inBody = false, inProperties = false;
        var decoder = _codePage.GetDecoder();
        int pendingStart = -1;
        int pos = 0;

        void Emit(char c, int start, int end, bool gap = false)
        {
            if (state.Skip)
            {
                if (state.CaptureInstruction)
                {
                    instruction.Append(c);
                }

                return;
            }

            if (state.Property)
            {
                _text.Add(c.ToString(), DocumentCharKind.Property, _spans.Count);
            }
            else
            {
                if (!inBody)
                {
                    inBody = true;
                    _text.Section("body", 0);
                    _text.Label("Body\n");
                }

                _text.Add(c.ToString(), gap ? DocumentCharKind.Gap : DocumentCharKind.Text, gap ? -1 : _spans.Count);
                if (gap)
                {
                    return;
                }
            }

            _spans.Add(new Span(start, end, state.Uc, state.Format, state.Field));
        }

        void Break()
        {
            if (!state.Skip && !state.Property && inBody && _text.Length > 0 && _text.Text[^1] != '\n')
            {
                _text.Label("\n");
            }
        }

        void DecodeByte(byte b, int start, int end)
        {
            Span<char> chars = stackalloc char[2];
            int n = decoder.GetChars([b], chars, flush: false);
            if (n == 0)
            {
                // First byte of a two-byte character: the span starts here.
                pendingStart = start;
                return;
            }

            int from = pendingStart >= 0 ? pendingStart : start;
            pendingStart = -1;
            for (int k = 0; k < n; k++)
            {
                Emit(chars[k], from, end);
            }
        }

        while (pos < data.Length)
        {
            byte b = data[pos];
            if (b == '{')
            {
                stack.Push(state);
                state = state.Child();
                pos++;
                if (pos + 1 < data.Length && data[pos] == '\\' && data[pos + 1] == '*')
                {
                    // An optional destination: readers skip what they don't know. Only shapes (text
                    // boxes) and annotations (comments) hold text worth finding; fldinst is captured.
                    pos += 2;
                    int peek = pos;
                    while (peek < data.Length && data[peek] is (byte)' ' or (byte)'\r' or (byte)'\n')
                    {
                        peek++;
                    }

                    string destination = peek + 1 < data.Length && data[peek] == '\\' ? ReadWord(data, peek + 1) : "";
                    if (destination is not ("shpinst" or "shptxt" or "annotation" or "fldinst"))
                    {
                        state.Skip = true;
                        state.CaptureInstruction = false;
                    }
                }

                continue;
            }

            if (b == '}')
            {
                if (state.Property && stack.Count > 0 && !stack.Peek().Property)
                {
                    _text.Label("\n");
                }

                if (state.Story)
                {
                    Break();
                }

                state = stack.Count > 0 ? stack.Pop() : new State();
                pos++;
                continue;
            }

            if (b is (byte)'\r' or (byte)'\n')
            {
                pos++;
                continue;
            }

            if (b != '\\')
            {
                DecodeByte(b, pos, pos + 1);
                state.AtStart = false;
                pos++;
                continue;
            }

            // Control word or symbol.
            int start = pos;
            if (pos + 1 >= data.Length)
            {
                break;
            }

            byte next = data[pos + 1];
            if (next == '\'')
            {
                if (pos + 3 < data.Length && IsHex(data[pos + 2]) && IsHex(data[pos + 3]))
                {
                    DecodeByte((byte)(Hex(data[pos + 2]) * 16 + Hex(data[pos + 3])), start, pos + 4);
                }

                pos += 4;
                state.AtStart = false;
                continue;
            }

            if (!IsLetter(next))
            {
                pos += 2;
                state.AtStart = false;
                switch ((char)next)
                {
                    case '\\' or '{' or '}':
                        Emit((char)next, start, pos);
                        break;
                    case '~':
                        Emit(' ', start, pos);
                        break;
                    case '_':
                        Emit('‑', start, pos);
                        break;
                    case '\r' or '\n':
                        Break();
                        break;
                }

                continue;
            }

            int wordStart = pos + 1;
            pos = wordStart;
            while (pos < data.Length && IsLetter(data[pos]))
            {
                pos++;
            }

            string word = Encoding.ASCII.GetString(data, wordStart, pos - wordStart);
            int? param = null;
            int paramStart = pos;
            if (pos < data.Length && (data[pos] == '-' || IsDigit(data[pos])))
            {
                pos++;
                while (pos < data.Length && IsDigit(data[pos]))
                {
                    pos++;
                }

                if (int.TryParse(Encoding.ASCII.GetString(data, paramStart, pos - paramStart), out int value))
                {
                    param = value;
                }
            }

            if (pos < data.Length && data[pos] == ' ')
            {
                pos++;
            }

            bool atStart = state.AtStart;
            state.AtStart = false;
            if (atStart && SkippedDestinations.Contains(word))
            {
                state.Skip = true;
                state.CaptureInstruction = word == "fldinst";
                if (word == "fldinst")
                {
                    instruction.Clear();
                }

                continue;
            }

            switch (word)
            {
                case "info" when atStart:
                    state.Skip = true;
                    break;
                case var w when atStart && InfoProperties.TryGetValue(w, out var label) && stack.Count > 0:
                    state.Skip = false;
                    state.Property = true;
                    if (!inProperties)
                    {
                        inProperties = true;
                        _text.Section("properties", 0);
                        _text.Label("Document properties\n");
                    }

                    _properties.Add(label);
                    _text.Label($"  {label}: ");
                    break;
                case "fldrslt" when atStart:
                    // A hyperlink's text is just its label; Word doesn't recompute it.
                    var field = instruction.ToString().Trim();
                    state.Field = field.StartsWith("HYPERLINK", StringComparison.OrdinalIgnoreCase) ? null : field;
                    break;
                case "footnote" or "header" or "headerl" or "headerr" or "headerf" or "footer" or "footerl" or "footerr" or "footerf" or "annotation" or "shptxt" when atStart:
                    // Its own story: keep it on its own lines, not run into the paragraph around it.
                    Break();
                    state.Story = true;
                    break;
                case "ansicpg" when param is int cp:
                    try
                    {
                        _codePage = Encoding.GetEncoding(cp);
                        decoder = _codePage.GetDecoder();
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                    {
                    }

                    break;
                case "uc" when param is int uc:
                    state.Uc = Math.Max(0, uc);
                    break;
                case "u" when param is int code:
                    {
                        // \uN, then N fallback characters for readers without Unicode: skip those, but keep them in the span.
                        int end = SkipFallback(data, pos, state.Uc);
                        Emit((char)(code < 0 ? code + 65536 : code), start, end);
                        pos = end;
                        break;
                    }

                case "bin" when param is int length:
                    pos = Math.Min(data.Length, pos + Math.Max(0, length));
                    break;
                case "par" or "sect" or "page" or "row" or "cell" or "nestcell" or "sbkpage":
                    Break();
                    break;
                case "line":
                    Emit(' ', start, pos, gap: true);
                    break;
                case "tab":
                    Emit('\t', start, pos, gap: true);
                    break;
                case var w when SymbolWords.TryGetValue(w, out char symbol):
                    Emit(symbol, start, pos);
                    break;
                case "plain":
                    state.Format = "";
                    break;
                case "pard" or "sectd" or "trowd":
                    break;
                default:
                    if (!state.Skip)
                    {
                        state.Format += "\\" + word + param;
                    }

                    break;
            }
        }
    }

    private static string ReadWord(byte[] data, int pos)
    {
        int start = pos;
        while (pos < data.Length && IsLetter(data[pos]))
        {
            pos++;
        }

        return Encoding.ASCII.GetString(data, start, pos - start);
    }

    /// <summary>Skips <paramref name="count"/> fallback characters after \uN (each a byte, an escape or a symbol).</summary>
    private static int SkipFallback(byte[] data, int pos, int count)
    {
        for (int k = 0; k < count && pos < data.Length; k++)
        {
            if (data[pos] is (byte)'{' or (byte)'}')
            {
                break;
            }

            if (data[pos] == '\\' && pos + 1 < data.Length)
            {
                if (data[pos + 1] == '\'')
                {
                    pos += 4;
                    continue;
                }

                if (IsLetter(data[pos + 1]))
                {
                    // A control word ends the fallback early.
                    break;
                }

                pos += 2;
                continue;
            }

            pos++;
        }

        return Math.Min(pos, data.Length);
    }

    public bool IsSearchable(int start, int length) => _text.IsSearchable(start, length);

    public DocumentMatchInfo Describe(int start, int length, string replacement, ReplaceProfile profile)
    {
        var (place, _) = _text.SectionAt(start);
        if (_text.KindAt(start) == DocumentCharKind.Property)
        {
            int index = PropertyIndexAt(start);
            var label = index >= 0 && index < _properties.Count ? _properties[index] : "property";
            return new DocumentMatchInfo(MatchContext.DocumentProperty, $"document {label.ToLowerInvariant()}", "properties", CannotWrite(start, length, replacement), []);
        }

        if (CannotWrite(start, length, replacement) is { } reason)
        {
            return new DocumentMatchInfo(MatchContext.DocumentText, place, place, reason, []);
        }

        var spans = Enumerable.Range(start, length).Select(i => _spans[_text.OwnerAt(i)]).ToList();
        var risks = new List<RiskFinding>();
        var original = _text.Text.Substring(start, length);
        if (spans.Select(s => s.Format).Distinct().Count() > 1)
        {
            risks.Add(new RiskFinding(RiskCode.DocMixedFormatting,
                $"“{original}” has more than one kind of formatting. “{replacement}” will all take the formatting of its first letter."));
        }

        if (spans.FirstOrDefault(s => s.Field is not null)?.Field is { } field)
        {
            var name = field.Length > 60 ? field[..60] + "…" : field;
            risks.Add(new RiskFinding(RiskCode.DocField,
                $"This text is shown by a field ({(name.Length == 0 ? "a field" : name)}). When the field is updated the old text would come back, so its source may need changing too."));
        }

        return new DocumentMatchInfo(MatchContext.DocumentText, place, place, null, risks);
    }

    /// <summary>Which property a character belongs to: properties are numbered in the order they appear.</summary>
    private int PropertyIndexAt(int offset)
    {
        int index = -1;
        bool inProperty = false;
        for (int i = 0; i <= offset; i++)
        {
            bool isProperty = _text.KindAt(i) == DocumentCharKind.Property;
            if (isProperty && !inProperty)
            {
                index++;
            }

            inProperty = isProperty;
        }

        return index;
    }

    public string? CannotWrite(int start, int length, string replacement)
    {
        var kind = _text.KindAt(start);
        if (kind is not (DocumentCharKind.Text or DocumentCharKind.Property) || !_text.AllOfKind(start, length, kind))
        {
            return "The match runs across a tab or line break, so it can't be replaced safely. Edit it by hand.";
        }

        if (replacement.Any(c => c is '\n' or '\r' or '\t' || char.IsControl(c)))
        {
            return "The replacement contains a tab, line break or control character, which isn't plain text in an RTF document. Edit it by hand.";
        }

        return null;
    }

    public byte[] Apply(IEnumerable<ChangeItem> items)
    {
        var edits = new List<(int Start, int End, string Text)>();
        foreach (var item in items.Where(i => i.WillChange))
        {
            if (CannotWrite(item.Offset, item.Length, item.FinalText) is { } reason)
            {
                throw new InvalidOperationException($"“{item.OriginalText}” → “{item.FinalText}”: {reason}");
            }

            // The replacement goes where the first letter was; the other letters are removed one by
            // one, so any formatting markup between them stays exactly as it was.
            for (int i = item.Offset; i < item.Offset + item.Length; i++)
            {
                var span = _spans[_text.OwnerAt(i)];
                edits.Add((span.Start, span.End, i == item.Offset ? Encode(item.FinalText, span.Uc) : ""));
            }
        }

        var output = new MemoryStream(_bytes.Length + 256);
        int pos = 0;
        foreach (var (start, end, text) in edits.OrderBy(e => e.Start))
        {
            if (start < pos)
            {
                continue;
            }

            output.Write(_bytes, pos, start - pos);
            output.Write(Encoding.ASCII.GetBytes(text));
            pos = end;
        }

        output.Write(_bytes, pos, _bytes.Length - pos);
        return output.ToArray();
    }

    /// <summary>Plain ASCII as is, other letters as \'hh in the document's code page, or \uN with a "?" fallback.</summary>
    private string Encode(string text, int uc)
    {
        var sb = new StringBuilder();
        var exact = Encoding.GetEncoding(_codePage.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        foreach (char c in text)
        {
            if (c is '\\' or '{' or '}')
            {
                sb.Append('\\').Append(c);
            }
            else if (c >= 32 && c < 127)
            {
                sb.Append(c);
            }
            else if (!char.IsSurrogate(c) && TryEncode(exact, c) is { Length: 1 } single)
            {
                sb.Append("\\'").Append(single[0].ToString("x2"));
            }
            else
            {
                sb.Append("\\u").Append((short)c).Append(' ').Append('?', uc);
            }
        }

        return sb.ToString();
    }

    private static byte[]? TryEncode(Encoding encoding, char c)
    {
        try
        {
            return encoding.GetBytes(c.ToString());
        }
        catch (EncoderFallbackException)
        {
            return null;
        }
    }

    private static bool IsLetter(byte b) => (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z');

    private static bool IsDigit(byte b) => b >= '0' && b <= '9';

    private static bool IsHex(byte b) => IsDigit(b) || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F');

    private static int Hex(byte b) => b <= '9' ? b - '0' : (b | 0x20) - 'a' + 10;
}
