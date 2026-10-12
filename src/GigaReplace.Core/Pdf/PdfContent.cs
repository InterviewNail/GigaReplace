using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Tokens;

namespace GigaReplace.Core.Pdf;

/// <summary>Where a match sits in a PDF and whether it can be changed there.</summary>
public sealed record PdfMatchInfo(MatchContext Context, string Detail, string Place, string? ReadOnlyReason, double? FitPercent);

/// <summary>A content stream whose text operators we may rewrite.</summary>
internal sealed class PdfStream
{
    public required IndirectReference Reference { get; init; }

    public required StreamToken Token { get; init; }

    public required byte[] Data { get; init; }

    public required List<PdfOperation> Ops { get; init; }
}

/// <summary>One glyph (<see cref="Code"/> ≥ 0) or one TJ position adjustment (<see cref="Kern"/>).</summary>
internal readonly record struct PdfPart(int Code, double Kern)
{
    public bool IsKern => Code < 0;
}

/// <summary>Glyphs <see cref="First"/>..<see cref="Last"/> of one operator, taking <see cref="Width"/> in text space.</summary>
internal readonly record struct PdfSegment(PdfShow Show, int First, int Last, double Width);

/// <summary>A text-showing operator (Tj, TJ, ' or ") and the text state it ran with.</summary>
internal sealed class PdfShow
{
    public required PdfStream Stream { get; init; }

    public required PdfOperation Op { get; init; }

    public required PdfFont Font { get; init; }

    public required int Page { get; init; }

    public double Size { get; init; }

    public double CharSpacing { get; init; }

    public double WordSpacing { get; init; }

    /// <summary>Tz / 100.</summary>
    public double HorizontalScale { get; init; }

    /// <summary>Text space → user space when the operator starts (text matrix × CTM).</summary>
    public PdfMatrix Origin { get; init; }

    /// <summary>Where the pen is, in user space, after the parts before <paramref name="index"/>.</summary>
    public (double X, double Y) PointAt(int index)
    {
        double u = Advance(Parts.Take(index)) * HorizontalScale;
        return (Origin.E + Origin.A * u, Origin.F + Origin.B * u);
    }

    public required List<PdfPart> Parts { get; init; }

    /// <summary>Advance of glyphs (and kerns) in text space, before horizontal scaling.</summary>
    public double Advance(IEnumerable<PdfPart> parts)
    {
        double total = 0;
        foreach (var part in parts)
        {
            total += part.IsKern
                ? -part.Kern / 1000 * Size
                : Font.WidthOf(part.Code) / 1000 * Size + CharSpacing + (part.Code == 32 && Font.CodeLength == 1 ? WordSpacing : 0);
        }

        return total;
    }
}

internal enum PdfCharKind : byte
{
    /// <summary>Headings and labels we added; never matched.</summary>
    Label,

    /// <summary>Drawn by a glyph we can map back to its operator.</summary>
    Glyph,

    /// <summary>A space or line break we inferred from the text's position.</summary>
    Gap,

    /// <summary>Part of a document property value.</summary>
    Property,

    /// <summary>Page text we could read but not map back to the PDF.</summary>
    Fixed,
}

internal sealed class PdfProperty
{
    public required string Label { get; init; }

    public required string Value { get; init; }

    /// <summary>The Info dictionary key, for Info properties.</summary>
    public string? InfoKey { get; init; }

    /// <summary>Offset of the value in the XMP packet, for XMP properties.</summary>
    public int XmpStart { get; init; } = -1;
}

/// <summary>
/// A PDF as GigaReplace sees it: one searchable text (document properties, then each page's
/// text) where every character knows what drew it, so a match can be judged and rewritten.
/// </summary>
public sealed partial class PdfContent
{
    private readonly List<PdfCharKind> _kinds = [];
    private readonly List<int> _owners = [];
    private readonly List<int> _parts = [];
    private readonly List<(int Start, string Place, int Page)> _sections = [];
    private readonly List<PdfShow> _shows = [];
    private readonly List<PdfProperty> _properties = [];
    private readonly Dictionary<int, string> _fixedPages = [];
    private readonly List<string> _notes = [];
    private string _text = "";

    private PdfContent(byte[] bytes)
    {
        Bytes = bytes;
        Hash = SHA256.HashData(bytes);
    }

    public string Text => _text;

    public byte[] Hash { get; }

    public int PageCount { get; private set; }

    /// <summary>Things worth telling the user, such as pages whose text can only be reported.</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Why nothing in this PDF can be changed (encrypted, signed), or null.</summary>
    public string? DocumentProblem { get; private set; }

    internal byte[] Bytes { get; }

    internal IReadOnlyList<PdfShow> Shows => _shows;

    internal IReadOnlyList<PdfProperty> Properties => _properties;

    internal PdfTrailer Trailer { get; private set; } = null!;

    internal DictionaryToken? InfoDictionary { get; private set; }

    internal (IndirectReference Reference, StreamToken Token, string Xml, bool Bom)? Xmp { get; private set; }

    public static PdfContent Load(byte[] bytes)
    {
        var content = new PdfContent(bytes);
        using var document = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        content.Read(new PdfObjects(document));
        return content;
    }

    private void Read(PdfObjects objects)
    {
        var document = objects.Document;
        var structure = document.Structure;
        Trailer = new PdfTrailer(structure.Trailer.Root, structure.Trailer.Info, structure.Trailer.Identifier, structure.Trailer.Size);
        var catalog = structure.Catalog.CatalogDictionary;

        if (document.IsEncrypted)
        {
            DocumentProblem = "the PDF is encrypted";
        }
        else if (objects.Resolve<DictionaryToken>(catalog, NameToken.AcroForm) is { } form
            && ((objects.Resolve<NumericToken>(form, NameToken.SigFlags)?.Int ?? 0) & 1) != 0)
        {
            DocumentProblem = "the PDF is digitally signed, and changing it would break the signature";
        }

        ReadProperties(objects, catalog);
        var pages = new List<(DictionaryToken Page, DictionaryToken? Resources)>();
        CollectPages(objects, objects.Resolve<DictionaryToken>(catalog, NameToken.Pages), null, pages, 0);
        PageCount = pages.Count;

        var reader = new PageReader(objects);
        var pageTexts = new List<(int Page, List<object> Items)?>();
        for (int i = 0; i < pages.Count; i++)
        {
            int number = i + 1;
            List<object>? items = null;
            string? reason;
            try
            {
                (items, reason) = reader.Read(pages[i].Page, pages[i].Resources, number);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                reason = $"its content couldn't be read ({ex.Message})";
            }

            if (reason is null)
            {
                pageTexts.Add((number, items!));
                continue;
            }

            // Report-only: let PdfPig find the text, even though we can't map it back.
            pageTexts.Add(null);
            _fixedPages[number] = reason;
            try
            {
                _fixedText[number] = ContentOrderTextExtractor.GetText(document.GetPage(number));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _notes.Add($"Couldn't read the text of page {number}: {ex.Message}");
            }
        }

        BuildText(pageTexts);
    }

    private readonly Dictionary<int, string> _fixedText = [];

    private void ReadProperties(PdfObjects objects, DictionaryToken catalog)
    {
        var info = objects.Resolve<DictionaryToken>(Trailer.Info);
        InfoDictionary = info;
        if (info is not null)
        {
            foreach (var (key, token) in info.Data)
            {
                if (key is "CreationDate" or "ModDate" or "Trapped")
                {
                    continue;
                }

                var value = objects.Resolve<IToken>(token) switch
                {
                    StringToken s => s.Data,
                    HexToken h => PdfText.Decode(h.Bytes.ToArray()),
                    _ => null,
                };
                if (!string.IsNullOrEmpty(value))
                {
                    _properties.Add(new PdfProperty { Label = key, Value = value, InfoKey = key });
                }
            }
        }

        if (catalog.Data.TryGetValue(NameToken.Metadata.Data, out var metaToken) && metaToken is IndirectReferenceToken metaRef
            && objects.Get(metaRef.Data) is StreamToken metaStream)
        {
            var bytes = objects.Decode(metaStream);
            bool bom = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]);
            var xml = Encoding.UTF8.GetString(bom ? bytes.AsSpan(3) : bytes);
            Xmp = (metaRef.Data, metaStream, xml, bom);
            foreach (var (label, start, value) in XmpValues(xml))
            {
                _properties.Add(new PdfProperty { Label = "XMP " + label, Value = value, XmpStart = start });
            }
        }
    }

    /// <summary>Text values in an XMP packet: element text and prefixed attributes.</summary>
    private static IEnumerable<(string Label, int Start, string Value)> XmpValues(string xml)
    {
        var tags = XmpStartTag().Matches(xml).Select(m => (m.Index, Name: m.Groups[1].Value)).ToList();
        foreach (Match m in XmpText().Matches(xml))
        {
            var value = m.Groups[1];
            if (string.IsNullOrWhiteSpace(value.Value) || (m.Index > 0 && xml[m.Index - 1] == '?'))
            {
                continue;
            }

            var owner = tags.LastOrDefault(t => t.Index < m.Index && !t.Name.StartsWith("rdf:", StringComparison.Ordinal) && !t.Name.StartsWith("x:", StringComparison.Ordinal));
            yield return (owner.Name ?? "value", value.Index, value.Value);
        }

        foreach (Match m in XmpAttribute().Matches(xml))
        {
            var name = m.Groups[1].Value;
            if (name.StartsWith("xmlns:", StringComparison.Ordinal) || name.StartsWith("rdf:", StringComparison.Ordinal)
                || name.StartsWith("x:", StringComparison.Ordinal) || name.StartsWith("xml:", StringComparison.Ordinal)
                || m.Groups[2].Length == 0)
            {
                continue;
            }

            yield return (name, m.Groups[2].Index, m.Groups[2].Value);
        }
    }

    private static void CollectPages(PdfObjects objects, DictionaryToken? node, DictionaryToken? inherited, List<(DictionaryToken, DictionaryToken?)> pages, int depth)
    {
        if (node is null || depth > 64 || pages.Count > 100_000)
        {
            return;
        }

        var resources = objects.Resolve<DictionaryToken>(node, NameToken.Resources) ?? inherited;
        if (objects.Resolve<ArrayToken>(node, NameToken.Kids) is { } kids)
        {
            foreach (var kid in kids.Data)
            {
                CollectPages(objects, objects.Resolve<DictionaryToken>(kid), resources, pages, depth + 1);
            }
        }
        else
        {
            pages.Add((node, resources));
        }
    }

    private void BuildText(List<(int Page, List<object> Items)?> pageTexts)
    {
        var sb = new StringBuilder();
        void Add(string s, PdfCharKind kind, int owner = -1, int part = -1)
        {
            foreach (char c in s)
            {
                sb.Append(c is '\n' && kind != PdfCharKind.Label && kind != PdfCharKind.Gap && kind != PdfCharKind.Fixed ? ' ' : c is '\r' or '\t' ? ' ' : c);
                _kinds.Add(kind);
                _owners.Add(owner);
                _parts.Add(part);
            }
        }

        if (_properties.Count > 0)
        {
            _sections.Add((sb.Length, "properties", 0));
            Add("Document properties\n", PdfCharKind.Label);
            for (int i = 0; i < _properties.Count; i++)
            {
                Add($"  {_properties[i].Label}: ", PdfCharKind.Label);
                for (int k = 0; k < _properties[i].Value.Length; k++)
                {
                    Add(_properties[i].Value[k].ToString(), PdfCharKind.Property, i, k);
                }

                Add("\n", PdfCharKind.Label);
            }
        }

        for (int i = 0; i < pageTexts.Count; i++)
        {
            int number = i + 1;
            _sections.Add((sb.Length, $"page {number}", number));
            Add($"Page {number}\n", PdfCharKind.Label);
            if (pageTexts[i] is not { } page)
            {
                if (_fixedText.TryGetValue(number, out var text))
                {
                    Add(text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n'), PdfCharKind.Fixed, number);
                }
            }
            else
            {
                bool atStart = true;
                foreach (var item in page.Items)
                {
                    if (item is string gap)
                    {
                        // Skip leading gaps and doubled spaces.
                        if (!atStart && !(gap == " " && sb.Length > 0 && sb[^1] is ' ' or '\n') && !(gap == "\n" && sb[^1] == '\n'))
                        {
                            Add(gap, PdfCharKind.Gap, number);
                        }

                        continue;
                    }

                    var show = (PdfShow)item;
                    int showIndex = _shows.Count;
                    _shows.Add(show);
                    for (int p = 0; p < show.Parts.Count; p++)
                    {
                        if (!show.Parts[p].IsKern)
                        {
                            Add(show.Font.TextOf(show.Parts[p].Code), PdfCharKind.Glyph, showIndex, p);
                            atStart = false;
                        }
                        else if (show.Parts[p].Kern <= -200 && !atStart && sb[^1] != ' ')
                        {
                            Add(" ", PdfCharKind.Gap, number);
                        }
                    }
                }
            }

            Add("\n", PdfCharKind.Label);
        }

        _text = sb.ToString();
        if (_fixedPages.Count > 0)
        {
            var list = string.Join(", ", _fixedPages.Keys.Take(10));
            _notes.Add(_fixedPages.Count == 1
                ? $"Page {list}: its text can be found but not changed ({_fixedPages.Values.First()})."
                : $"{_fixedPages.Count} pages ({list}{(_fixedPages.Count > 10 ? ", …" : "")}): their text can be found but not changed ({_fixedPages.Values.First()}).");
        }
    }

    /// <summary>False if the range touches headings or labels this view added.</summary>
    public bool IsSearchable(int start, int length)
    {
        for (int i = start; i < start + length; i++)
        {
            if (_kinds[i] == PdfCharKind.Label)
            {
                return false;
            }
        }

        return length > 0;
    }

    /// <summary>Where the match is, and whether (and how) <paramref name="replacement"/> can be written there.</summary>
    public PdfMatchInfo Describe(int start, int length, string replacement, bool allowPageText)
    {
        var (_, place, page) = SectionAt(start);
        var kind = _kinds[start];
        if (kind == PdfCharKind.Property)
        {
            var property = _properties[_owners[start]];
            string detail = property.InfoKey is not null ? $"document {property.Label.ToLowerInvariant()}" : property.Label;
            return new PdfMatchInfo(MatchContext.PdfProperty, detail, "properties", DocumentProblem is null ? null : $"It can't be changed, because {DocumentProblem}.", null);
        }

        string pageDetail = $"page {page}";
        string? reason = PageTextProblem(start, length, replacement, allowPageText, out var segments, out int[]? codes);
        if (reason is not null)
        {
            return new PdfMatchInfo(MatchContext.PdfText, pageDetail, place, reason, null);
        }

        double before = segments!.Sum(s => s.Width);
        double after = segments[0].Show.Advance(codes!.Select(c => new PdfPart(c, 0)));
        double? fit = after > 0 && before > 0 ? before / after * 100 : null;
        return new PdfMatchInfo(MatchContext.PdfText, pageDetail, place, null, fit);
    }

    /// <summary>Why <paramref name="replacement"/> can't be written over the match, or null if it can.</summary>
    public string? CannotWrite(int start, int length, string replacement)
    {
        if (_kinds[start] == PdfCharKind.Property)
        {
            return DocumentProblem is null ? null : $"It can't be changed, because {DocumentProblem}.";
        }

        return PageTextProblem(start, length, replacement, true, out _, out _);
    }

    /// <summary>
    /// Checks a page-text match. A match may span several adjacent text operators (generators
    /// often split words where kerning applies) as long as they share a font and size.
    /// </summary>
    private string? PageTextProblem(int start, int length, string replacement, bool allowPageText, out List<PdfSegment>? segments, out int[]? codes)
    {
        segments = null;
        codes = null;
        int page = SectionAt(start).Page;
        if (!allowPageText)
        {
            return "Changing text on PDF pages is turned off, so this is listed for you to edit by hand.";
        }

        if (DocumentProblem is not null)
        {
            return $"It can't be changed, because {DocumentProblem}.";
        }

        if (_kinds[start] == PdfCharKind.Fixed)
        {
            return $"The text of page {page} can't be mapped back to the PDF, because {_fixedPages.GetValueOrDefault(page, "of how it's drawn")}. Edit it by hand.";
        }

        const string Pieces = "The PDF draws this text in separately placed pieces, so it can't be replaced safely. Edit it by hand.";
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            if (_kinds[i] != PdfCharKind.Glyph || (i > start && _owners[i] != _owners[i - 1] && _owners[i] != _owners[i - 1] + 1))
            {
                return Pieces;
            }
        }

        if ((start > 0 && _kinds[start - 1] == PdfCharKind.Glyph && _owners[start - 1] == _owners[start] && _parts[start - 1] == _parts[start])
            || (end < _kinds.Count && _kinds[end] == PdfCharKind.Glyph && _owners[end] == _owners[end - 1] && _parts[end] == _parts[end - 1]))
        {
            return "The match covers only part of a combined glyph (a ligature such as “fi”). Edit it by hand.";
        }

        var show = _shows[_owners[start]];
        var list = new List<PdfSegment>();
        for (int i = start; i < end; i++)
        {
            var current = _shows[_owners[i]];
            if (list.Count == 0 || !ReferenceEquals(list[^1].Show, current))
            {
                if (!ReferenceEquals(current.Font, show.Font) || current.Size != show.Size)
                {
                    return Pieces;
                }

                list.Add(new PdfSegment(current, _parts[i], _parts[i], 0));
            }

            list[^1] = list[^1] with { Last = _parts[i] };
        }

        segments = list.Select(s => s with { Width = s.Show.Advance(s.Show.Parts.Skip(s.First).Take(s.Last - s.First + 1)) }).ToList();
        if (segments.Count > 1 && SpanWidth(segments[0], segments[^1]) is { } span)
        {
            segments = [segments[0] with { Width = span }, .. segments.Skip(1).Select(s => s with { Width = 0 })];
        }
        if (show.Font.NotEditableReason is { } fontProblem)
        {
            return $"It can't be replaced, because {fontProblem}. Edit it by hand.";
        }

        codes = show.Font.Encode(replacement);
        if (codes is null)
        {
            var missing = show.Font.MissingCharacters(replacement);
            return show.Font.IsSubset
                ? $"The PDF only embeds the letters it uses from the font {show.Font.Name}, and “{missing}” isn't one of them. Edit it by hand (or regenerate the PDF)."
                : $"The font {show.Font.Name} can't draw “{missing}”. Edit it by hand.";
        }

        return null;
    }

    /// <summary>The width, in the first piece's text space, from the start of one piece's glyphs to the end of another's.</summary>
    private static double? SpanWidth(PdfSegment first, PdfSegment last)
    {
        var (x0, y0) = first.Show.PointAt(first.First);
        var (x1, y1) = last.Show.PointAt(last.Last + 1);
        var o = first.Show.Origin;
        // Projected onto the text direction (A, B), then divided by that vector's length and Tz.
        double norm2 = o.A * o.A + o.B * o.B;
        double along = (x1 - x0) * o.A + (y1 - y0) * o.B;
        return norm2 > 0 && along > 0 && first.Show.HorizontalScale > 0 ? along / (norm2 * first.Show.HorizontalScale) : null;
    }

    /// <summary>The page-text pieces a match covers, with the width each takes; null if it can't be written.</summary>
    internal List<PdfSegment>? Segments(int start, int length, string replacement, out int[]? codes) =>
        PageTextProblem(start, length, replacement, true, out var segments, out codes) is null ? segments : null;

    private (int Start, string Place, int Page) SectionAt(int offset)
    {
        int lo = 0, hi = _sections.Count - 1, found = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_sections[mid].Start <= offset)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return _sections.Count == 0 ? (0, "", 0) : _sections[found];
    }

    /// <summary>The PDF with the approved changes written as an incremental update.</summary>
    public byte[] Apply(IEnumerable<ChangeItem> items) => PdfWriter.Apply(this, items);


    internal (PdfProperty Property, int Offset) PropertyAt(int start) => (_properties[_owners[start]], _parts[start]);

    internal PdfCharKind KindAt(int offset) => _kinds[offset];

    [GeneratedRegex("<([A-Za-z][\\w.\\-]*:[\\w.\\-]+)")]
    private static partial Regex XmpStartTag();

    [GeneratedRegex(">([^<]+)<")]
    private static partial Regex XmpText();

    [GeneratedRegex("\\s([A-Za-z][\\w.\\-]*:[\\w.\\-]+)\\s*=\\s*\"([^\"]*)\"")]
    private static partial Regex XmpAttribute();

    /// <summary>Runs a page's content and collects its text-showing operators, with inferred spaces and line breaks.</summary>
    private sealed class PageReader(PdfObjects objects)
    {
        private readonly HashSet<IndirectReference> _visited = [];
        private readonly Dictionary<object, PdfFont> _fonts = new(ReferenceComparer.Instance);

        private sealed class State
        {
            public PdfMatrix Ctm = PdfMatrix.Identity;
            public PdfFont? Font;
            public double Size, Tc, Tw, Th = 1, TL;

            public State Clone() => (State)MemberwiseClone();
        }

        private sealed class Run
        {
            public required int Page;
            public List<object> Items = [];
            public string? Problem;
            public PdfMatrix Tm = PdfMatrix.Identity, Tlm = PdfMatrix.Identity;
            public (double X, double Y, double H)? Last;
        }

        public (List<object> Items, string? Problem) Read(DictionaryToken page, DictionaryToken? resources, int number)
        {
            var run = new Run { Page = number };
            var state = new State();
            var stack = new Stack<State>();
            foreach (var stream in ContentStreams(page))
            {
                Execute(stream, resources, run, ref state, stack, 0);
            }

            return (run.Items, run.Problem);
        }

        private IEnumerable<PdfStream> ContentStreams(DictionaryToken page)
        {
            if (!page.Data.TryGetValue(NameToken.Contents.Data, out var token))
            {
                yield break;
            }

            if (token is IndirectReferenceToken single && objects.Get(single.Data) is ArrayToken indirectArray)
            {
                token = indirectArray;
            }

            List<IndirectReferenceToken> refs = token switch
            {
                IndirectReferenceToken r => [r],
                ArrayToken array => array.Data.OfType<IndirectReferenceToken>().ToList(),
                _ => [],
            };
            foreach (var r in refs)
            {
                if (_visited.Add(r.Data) && objects.Get(r.Data) is StreamToken stream)
                {
                    yield return Load(r.Data, stream);
                }
            }
        }

        private PdfStream Load(IndirectReference reference, StreamToken token)
        {
            var data = objects.Decode(token);
            return new PdfStream { Reference = reference, Token = token, Data = data, Ops = PdfContentLexer.Parse(data) };
        }

        private void Execute(PdfStream stream, DictionaryToken? resources, Run run, ref State s, Stack<State> stack, int depth)
        {
            foreach (var op in stream.Ops)
            {
                var a = op.Operands;
                switch (op.Operator)
                {
                    case "q":
                        stack.Push(s.Clone());
                        break;
                    case "Q":
                        if (stack.Count > 0)
                        {
                            s = stack.Pop();
                        }

                        break;
                    case "cm" when a.Count >= 6:
                        s.Ctm = Matrix(a).Then(s.Ctm);
                        break;
                    case "BT":
                        run.Tm = run.Tlm = PdfMatrix.Identity;
                        break;
                    case "Tf" when a.Count >= 2:
                        s.Font = a[^2] is PdfName fontName ? FontFor(resources, fontName.Value) : null;
                        s.Size = Num(a, 1);
                        break;
                    case "Tc":
                        s.Tc = Num(a, 1);
                        break;
                    case "Tw":
                        s.Tw = Num(a, 1);
                        break;
                    case "Tz":
                        s.Th = Num(a, 1) / 100;
                        break;
                    case "TL":
                        s.TL = Num(a, 1);
                        break;
                    case "Td":
                        run.Tlm = PdfMatrix.Translate(Num(a, 2), Num(a, 1)).Then(run.Tlm);
                        run.Tm = run.Tlm;
                        break;
                    case "TD":
                        s.TL = -Num(a, 1);
                        run.Tlm = PdfMatrix.Translate(Num(a, 2), Num(a, 1)).Then(run.Tlm);
                        run.Tm = run.Tlm;
                        break;
                    case "Tm" when a.Count >= 6:
                        run.Tm = run.Tlm = Matrix(a);
                        break;
                    case "T*":
                        NextLine(run, s);
                        break;
                    case "Tj" when a.Count >= 1:
                        Show(stream, op, [a[^1]], run, s);
                        break;
                    case "TJ" when a.Count >= 1 && a[^1] is PdfArray array:
                        Show(stream, op, array.Items, run, s);
                        break;
                    case "'" when a.Count >= 1:
                        NextLine(run, s);
                        Show(stream, op, [a[^1]], run, s);
                        break;
                    case "\"" when a.Count >= 3:
                        s.Tw = Num(a, 3);
                        s.Tc = Num(a, 2);
                        NextLine(run, s);
                        Show(stream, op, [a[^1]], run, s);
                        break;
                    case "Do" when a.Count >= 1 && a[^1] is PdfName xobject && depth < 12:
                        RunForm(resources, xobject.Value, run, s, depth);
                        break;
                }
            }
        }

        private void RunForm(DictionaryToken? resources, string name, Run run, State s, int depth)
        {
            var xobjects = objects.Resolve<DictionaryToken>(resources, NameToken.Xobject);
            if (xobjects is null || !xobjects.Data.TryGetValue(name, out var token) || token is not IndirectReferenceToken reference
                || objects.Get(reference.Data) is not StreamToken form
                || objects.Resolve<NameToken>(form.StreamDictionary, NameToken.Subtype)?.Data != "Form"
                || !_visited.Add(reference.Data))
            {
                return;
            }

            var stream = Load(reference.Data, form);
            var inner = s.Clone();
            if (objects.Resolve<ArrayToken>(form.StreamDictionary, NameToken.Matrix) is { Length: 6 } m)
            {
                var values = m.Data.Select(t => (PdfOperand)new PdfNumber(objects.Resolve<NumericToken>(t)?.Double ?? 0)).ToList();
                inner.Ctm = Matrix(values).Then(inner.Ctm);
            }

            var (tm, tlm) = (run.Tm, run.Tlm);
            Execute(stream, objects.Resolve<DictionaryToken>(form.StreamDictionary, NameToken.Resources) ?? resources, run, ref inner, new Stack<State>(), depth + 1);
            (run.Tm, run.Tlm) = (tm, tlm);
        }

        private static void NextLine(Run run, State s)
        {
            run.Tlm = PdfMatrix.Translate(0, -s.TL).Then(run.Tlm);
            run.Tm = run.Tlm;
        }

        private void Show(PdfStream stream, PdfOperation op, List<PdfOperand> items, Run run, State s)
        {
            if (s.Font is not { } font)
            {
                return;
            }

            var parts = new List<PdfPart>();
            foreach (var item in items)
            {
                if (item is PdfBytes bytes)
                {
                    foreach (int code in font.Codes(bytes.Value))
                    {
                        parts.Add(new PdfPart(code, 0));
                        font.MarkUsed(code);
                    }
                }
                else if (item is PdfNumber number)
                {
                    parts.Add(new PdfPart(-1, number.Value));
                }
            }

            var start = run.Tm.Then(s.Ctm);
            double height = Math.Abs(s.Size) * start.VerticalScale;
            if (height <= 0)
            {
                height = 1;
            }

            if (run.Last is { } last && parts.Any(p => !p.IsKern))
            {
                double tolerance = Math.Max(height, last.H);
                if (Math.Abs(start.F - last.Y) > 0.5 * tolerance || start.E < last.X - 2 * tolerance)
                {
                    run.Items.Add("\n");
                }
                else if (start.E - last.X > 0.2 * tolerance)
                {
                    run.Items.Add(" ");
                }
            }

            var show = new PdfShow
            {
                Stream = stream,
                Op = op,
                Font = font,
                Page = run.Page,
                Size = s.Size,
                CharSpacing = s.Tc,
                WordSpacing = s.Tw,
                HorizontalScale = s.Th,
                Origin = start,
                Parts = parts,
            };
            run.Tm = PdfMatrix.Translate(show.Advance(parts) * s.Th, 0).Then(run.Tm);
            var end = run.Tm.Then(s.Ctm);
            if (parts.Any(p => !p.IsKern))
            {
                run.Last = (end.E, end.F, height);
            }

            if (!font.Decodable)
            {
                run.Problem ??= font.NotEditableReason ?? $"its font {font.Name} can't be read";
            }

            run.Items.Add(show);
        }

        private PdfFont? FontFor(DictionaryToken? resources, string name)
        {
            var fonts = objects.Resolve<DictionaryToken>(resources, NameToken.Font);
            if (fonts is null || !fonts.Data.TryGetValue(name, out var token))
            {
                return null;
            }

            object key = token is IndirectReferenceToken r ? r.Data : token;
            if (!_fonts.TryGetValue(key, out var font) && objects.Resolve<DictionaryToken>(token) is { } dictionary)
            {
                font = PdfFont.Load(dictionary, objects);
                _fonts[key] = font;
            }

            return font;
        }

        private static double Num(List<PdfOperand> a, int fromEnd) =>
            a.Count >= fromEnd && a[^fromEnd] is PdfNumber n ? n.Value : 0;

        private static PdfMatrix Matrix(List<PdfOperand> a) => new(Num(a, 6), Num(a, 5), Num(a, 4), Num(a, 3), Num(a, 2), Num(a, 1));
    }

    /// <summary>Indirect references compare by value; direct dictionaries by identity.</summary>
    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static ReferenceComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) => x is IndirectReference a && y is IndirectReference b ? a.Equals(b) : ReferenceEquals(x, y);

        public int GetHashCode(object obj) => obj is IndirectReference r ? r.GetHashCode() : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}

/// <summary>What an incremental update needs from the original trailer.</summary>
internal sealed record PdfTrailer(IndirectReference Root, IToken? Info, IReadOnlyList<IToken>? Identifier, int Size);

/// <summary>PDF text strings: PDFDocEncoding or UTF-16BE with a byte-order mark.</summary>
internal static class PdfText
{
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, (bytes.Length - 2) & ~1);
        }

        return Encoding.Latin1.GetString(bytes);
    }

    /// <summary>A string token: literal when plain ASCII, otherwise UTF-16BE hex.</summary>
    public static string Encode(string text)
    {
        if (text.All(c => c >= 32 && c < 127))
        {
            var sb = new StringBuilder("(");
            foreach (char c in text)
            {
                if (c is '(' or ')' or '\\')
                {
                    sb.Append('\\');
                }

                sb.Append(c);
            }

            return sb.Append(')').ToString();
        }

        return "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";
    }
}
