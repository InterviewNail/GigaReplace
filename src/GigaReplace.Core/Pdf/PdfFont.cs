using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig.Fonts;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Tokens;
using PdfEncoding = UglyToad.PdfPig.Fonts.Encodings.Encoding;
using StandardEncoding = UglyToad.PdfPig.Fonts.Encodings.StandardEncoding;
using WinAnsiEncoding = UglyToad.PdfPig.Fonts.Encodings.WinAnsiEncoding;

namespace GigaReplace.Core.Pdf;

/// <summary>
/// What we need from a font to read and rewrite text drawn with it: code → text, glyph widths,
/// and which characters can safely be drawn in place of others.
/// </summary>
internal sealed partial class PdfFont
{
    private readonly Dictionary<int, string> _unicode = [];
    private readonly Dictionary<int, double> _widths = [];
    private readonly HashSet<int> _used = [];
    private Dictionary<string, int>? _reverse;
    private double _defaultWidth;

    private PdfFont(string name)
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary>Bytes per character code: 1 for simple fonts, 2 for Identity-H/V composite fonts.</summary>
    public int CodeLength { get; private set; } = 1;

    /// <summary>Codes can be turned into text. When false, nothing drawn with this font can be found.</summary>
    public bool Decodable { get; private set; } = true;

    /// <summary>Why text in this font can't be changed, or null if it can.</summary>
    public string? NotEditableReason { get; private set; }

    /// <summary>Only the glyphs this document uses are embedded (the usual case), so only those can be drawn.</summary>
    public bool IsSubset { get; private set; }

    public static PdfFont Load(DictionaryToken font, PdfObjects objects)
    {
        string baseFont = objects.Resolve<NameToken>(font, NameToken.BaseFont)?.Data ?? "unnamed font";
        var result = new PdfFont(SubsetPrefix().Replace(baseFont, ""))
        {
            IsSubset = SubsetPrefix().IsMatch(baseFont),
        };
        try
        {
            result.Read(font, baseFont, objects);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result.Decodable = false;
            result.NotEditableReason = $"its font {result.Name} couldn't be read";
        }

        return result;
    }

    private void Read(DictionaryToken font, string baseFont, PdfObjects objects)
    {
        string subtype = objects.Resolve<NameToken>(font, NameToken.Subtype)?.Data ?? "";
        var toUnicode = objects.Resolve<StreamToken>(font, NameToken.ToUnicode) is { } cmap ? ToUnicodeCMap.Parse(objects.Decode(cmap)) : null;

        if (subtype == "Type0")
        {
            ReadComposite(font, objects, toUnicode);
            return;
        }

        var descriptor = objects.Resolve<DictionaryToken>(font, NameToken.FontDescriptor);
        if (subtype == "Type3")
        {
            NotEditableReason = $"it uses the Type 3 font {Name}, which is drawn from shapes";
        }

        // Code → text: ToUnicode wins, otherwise the font's encoding and glyph names.
        var names = SimpleEncoding(font, baseFont, descriptor, objects, out bool symbolicWithoutEncoding);
        for (int code = 0; code < 256; code++)
        {
            if (toUnicode is not null && toUnicode.TryGetValue(code, out var mapped))
            {
                _unicode[code] = mapped;
            }
            else if (names.TryGetValue(code, out var glyphName) && GlyphText(glyphName) is { } text)
            {
                _unicode[code] = text;
            }
        }

        if (toUnicode is null && symbolicWithoutEncoding)
        {
            Decodable = false;
            NotEditableReason ??= $"its font {Name} has no character map";
        }

        // Widths, in thousandths of the font size.
        double scale = 1;
        if (subtype == "Type3" && objects.Resolve<ArrayToken>(font, NameToken.FontMatrix) is { Length: > 0 } matrix && matrix[0] is NumericToken m)
        {
            scale = m.Double * 1000;
        }

        _defaultWidth = descriptor is not null && objects.Resolve<NumericToken>(descriptor, NameToken.MissingWidth) is { } missing ? missing.Double : 0;
        if (objects.Resolve<ArrayToken>(font, NameToken.Widths) is { } widths)
        {
            int first = objects.Resolve<NumericToken>(font, NameToken.FirstChar)?.Int ?? 0;
            for (int i = 0; i < widths.Length; i++)
            {
                if (objects.Resolve<NumericToken>(widths[i]) is { } w)
                {
                    _widths[first + i] = w.Double * scale;
                }
            }
        }
        else if (Standard14.IsFontInStandard14(baseFont) && Standard14.GetAdobeFontMetrics(baseFont) is { } afm)
        {
            foreach (var (code, glyphName) in names)
            {
                if (afm.CharacterMetrics.TryGetValue(glyphName, out var metric))
                {
                    _widths[code] = metric.Width.X;
                }
            }
        }
    }

    private void ReadComposite(DictionaryToken font, PdfObjects objects, Dictionary<int, string>? toUnicode)
    {
        CodeLength = 2;
        string encoding = objects.Resolve<NameToken>(font, NameToken.Encoding)?.Data ?? "";
        if (encoding == "Identity-V")
        {
            NotEditableReason = $"its font {Name} writes vertically";
        }
        else if (encoding != "Identity-H")
        {
            Decodable = false;
            NotEditableReason = $"its font {Name} uses the {(encoding.Length > 0 ? encoding : "embedded")} character map";
            return;
        }

        if (toUnicode is null)
        {
            Decodable = false;
            NotEditableReason = $"its font {Name} has no character map";
            return;
        }

        foreach (var (code, text) in toUnicode)
        {
            _unicode[code] = text;
        }

        _defaultWidth = 1000;
        if (objects.Resolve<ArrayToken>(font, NameToken.DescendantFonts) is { Length: > 0 } descendants
            && objects.Resolve<DictionaryToken>(descendants[0]) is { } cid)
        {
            if (objects.Resolve<NumericToken>(cid, NameToken.Dw) is { } dw)
            {
                _defaultWidth = dw.Double;
            }

            if (objects.Resolve<ArrayToken>(cid, NameToken.W) is { } w)
            {
                ReadCidWidths(w, objects);
            }

            var descriptor = objects.Resolve<DictionaryToken>(cid, NameToken.FontDescriptor);
            bool embedded = descriptor is not null && (descriptor.ContainsKey(NameToken.FontFile) || descriptor.ContainsKey(NameToken.FontFile2) || descriptor.ContainsKey(NameToken.FontFile3));
            if (embedded)
            {
                // Identity-H codes are glyph numbers: only glyphs this document draws are known to be there.
                IsSubset = true;
            }
        }
    }

    private void ReadCidWidths(ArrayToken w, PdfObjects objects)
    {
        int i = 0;
        while (i < w.Length)
        {
            if (objects.Resolve<NumericToken>(w[i]) is not { } first)
            {
                return;
            }

            if (i + 1 < w.Length && objects.Resolve<ArrayToken>(w[i + 1]) is { } list)
            {
                for (int k = 0; k < list.Length; k++)
                {
                    if (objects.Resolve<NumericToken>(list[k]) is { } width)
                    {
                        _widths[first.Int + k] = width.Double;
                    }
                }

                i += 2;
            }
            else if (i + 2 < w.Length && objects.Resolve<NumericToken>(w[i + 1]) is { } last && objects.Resolve<NumericToken>(w[i + 2]) is { } width)
            {
                for (int code = first.Int; code <= last.Int && code - first.Int < 65536; code++)
                {
                    _widths[code] = width.Double;
                }

                i += 3;
            }
            else
            {
                return;
            }
        }
    }

    /// <summary>Code → glyph name for a simple font, from its /Encoding (and /Differences).</summary>
    private static Dictionary<int, string> SimpleEncoding(DictionaryToken font, string baseFont, DictionaryToken? descriptor, PdfObjects objects, out bool symbolicWithoutEncoding)
    {
        var names = new Dictionary<int, string>();
        PdfEncoding? baseEncoding = null;
        ArrayToken? differences = null;
        var encodingToken = objects.Resolve<IToken>(font, NameToken.Encoding);
        if (encodingToken is NameToken name)
        {
            PdfEncoding.TryGetNamedEncoding(name, out baseEncoding);
        }
        else if (encodingToken is DictionaryToken dict)
        {
            if (objects.Resolve<NameToken>(dict, NameToken.BaseEncoding) is { } baseName)
            {
                PdfEncoding.TryGetNamedEncoding(baseName, out baseEncoding);
            }

            differences = objects.Resolve<ArrayToken>(dict, NameToken.Differences);
        }

        int flags = descriptor is not null ? objects.Resolve<NumericToken>(descriptor, NameToken.Flags)?.Int ?? 0 : 0;
        bool symbolic = (flags & 4) != 0 || baseFont.Contains("Symbol", StringComparison.OrdinalIgnoreCase) || baseFont.Contains("Dingbats", StringComparison.OrdinalIgnoreCase);
        symbolicWithoutEncoding = symbolic && encodingToken is null;
        if (baseEncoding is null && !symbolicWithoutEncoding)
        {
            string subtype = objects.Resolve<NameToken>(font, NameToken.Subtype)?.Data ?? "";
            baseEncoding = subtype == "TrueType" ? WinAnsiEncoding.Instance : StandardEncoding.Instance;
        }

        if (baseEncoding is not null)
        {
            foreach (var (code, glyph) in baseEncoding.CodeToNameMap)
            {
                names[code] = glyph;
            }
        }

        if (differences is not null)
        {
            int code = 0;
            foreach (var item in differences.Data)
            {
                if (objects.Resolve<NumericToken>(item) is { } number)
                {
                    code = number.Int;
                }
                else if (item is NameToken glyph)
                {
                    names[code++] = glyph.Data;
                }
            }

            // Explicit differences make the font readable even if it's flagged symbolic.
            symbolicWithoutEncoding = false;
        }

        return names;
    }

    private static string? GlyphText(string glyphName)
    {
        try
        {
            var text = GlyphList.AdobeGlyphList.NameToUnicode(glyphName);
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
        }

        return glyphName.Length == 1 && char.IsAsciiLetterOrDigit(glyphName[0]) ? glyphName : null;
    }

    /// <summary>Splits a shown string into character codes.</summary>
    public IEnumerable<int> Codes(byte[] bytes)
    {
        for (int i = 0; i + CodeLength <= bytes.Length; i += CodeLength)
        {
            yield return CodeLength == 1 ? bytes[i] : (bytes[i] << 8) | bytes[i + 1];
        }
    }

    public string TextOf(int code) => _unicode.TryGetValue(code, out var text) ? text : "�";

    /// <summary>Glyph advance in thousandths of the font size.</summary>
    public double WidthOf(int code) => _widths.TryGetValue(code, out var w) ? w : _defaultWidth;

    public bool HasWidths => _widths.Count > 0;

    public void MarkUsed(int code) => _used.Add(code);

    /// <summary>
    /// The codes that draw <paramref name="text"/>, one per character, or null if some character
    /// can't be drawn. Subset fonts only offer glyphs the document already draws with them.
    /// </summary>
    public int[]? Encode(string text)
    {
        _reverse ??= BuildReverse();
        var codes = new List<int>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (!_reverse.TryGetValue(rune.ToString(), out int code))
            {
                return null;
            }

            codes.Add(code);
        }

        return [.. codes];
    }

    /// <summary>The characters of <paramref name="text"/> this font can't draw.</summary>
    public string MissingCharacters(string text)
    {
        _reverse ??= BuildReverse();
        var missing = text.EnumerateRunes().Select(r => r.ToString()).Where(s => !_reverse.ContainsKey(s)).Distinct();
        return string.Concat(missing);
    }

    private Dictionary<string, int> BuildReverse()
    {
        var reverse = new Dictionary<string, int>(StringComparer.Ordinal);
        IEnumerable<int> candidates = IsSubset ? _used : _unicode.Keys.Where(c => _used.Contains(c) || CodeLength == 2 || WidthOf(c) > 0 || !HasWidths);

        // Prefer codes the document already uses, then the lowest code.
        foreach (int code in candidates.OrderBy(c => _used.Contains(c) ? 0 : 1).ThenBy(c => c))
        {
            if (_unicode.TryGetValue(code, out var text) && text.EnumerateRunes().Count() == 1 && !reverse.ContainsKey(text))
            {
                reverse[text] = code;
            }
        }

        return reverse;
    }

    public byte[] Bytes(IEnumerable<int> codes)
    {
        var bytes = new List<byte>();
        foreach (int code in codes)
        {
            if (CodeLength == 2)
            {
                bytes.Add((byte)(code >> 8));
            }

            bytes.Add((byte)code);
        }

        return [.. bytes];
    }

    [GeneratedRegex("^[A-Z]{6}\\+")]
    private static partial Regex SubsetPrefix();
}

/// <summary>Reads the bfchar/bfrange mappings of a ToUnicode CMap.</summary>
internal static class ToUnicodeCMap
{
    public static Dictionary<int, string> Parse(byte[] data)
    {
        var map = new Dictionary<int, string>();
        foreach (var op in PdfContentLexer.Parse(data))
        {
            var args = op.Operands;
            if (op.Operator == "endbfchar")
            {
                for (int i = 0; i + 1 < args.Count; i += 2)
                {
                    if (args[i] is PdfBytes src && args[i + 1] is PdfBytes dst)
                    {
                        map[Code(src.Value)] = Utf16(dst.Value);
                    }
                }
            }
            else if (op.Operator == "endbfrange")
            {
                for (int i = 0; i + 2 < args.Count; i += 3)
                {
                    if (args[i] is not PdfBytes lo || args[i + 1] is not PdfBytes hi)
                    {
                        continue;
                    }

                    int first = Code(lo.Value), last = Code(hi.Value);
                    if (last < first || last - first > 65535)
                    {
                        continue;
                    }

                    if (args[i + 2] is PdfBytes dst)
                    {
                        var start = Utf16(dst.Value);
                        for (int code = first; code <= last; code++)
                        {
                            map[code] = Increment(start, code - first);
                        }
                    }
                    else if (args[i + 2] is PdfArray list)
                    {
                        for (int k = 0; k < list.Items.Count && first + k <= last; k++)
                        {
                            if (list.Items[k] is PdfBytes item)
                            {
                                map[first + k] = Utf16(item.Value);
                            }
                        }
                    }
                }
            }
        }

        return map;
    }

    private static int Code(byte[] bytes)
    {
        int value = 0;
        foreach (byte b in bytes)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    private static string Utf16(byte[] bytes)
    {
        if (bytes.Length == 1)
        {
            return ((char)bytes[0]).ToString();
        }

        return Encoding.BigEndianUnicode.GetString(bytes, 0, bytes.Length & ~1);
    }

    private static string Increment(string start, int by)
    {
        if (by == 0 || start.Length == 0)
        {
            return start;
        }

        return string.Concat(start.AsSpan(0, start.Length - 1), ((char)(start[^1] + by)).ToString());
    }
}
