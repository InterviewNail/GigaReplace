using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using GigaReplace.Core.Text;

namespace GigaReplace.Core.Documents;

/// <summary>
/// A Word document (.docx and friends): a zip of XML parts. Text lives in w:t elements inside runs
/// (w:r) inside paragraphs (w:p). Word reflows text itself, so any replacement fits; the care is in
/// not touching what isn't text, and in matches split over runs with different formatting.
/// </summary>
public sealed class DocxContent : IDocumentContent
{
    private static readonly string[] PropertyNames = ["title", "subject", "creator", "keywords", "description", "category", "contentStatus", "Company", "Manager"];

    private readonly byte[] _bytes;
    private readonly List<ZipPart> _entries = [];
    private readonly List<XmlPart> _parts = [];
    private readonly List<TextRun> _runs = [];
    private readonly List<Property> _properties = [];
    private readonly DocumentTextBuilder _text = new();
    private readonly List<string> _notes = [];

    private DocxContent(byte[] bytes)
    {
        _bytes = bytes;
        Hash = SHA256.HashData(bytes);
    }

    public string KindName => "Word document";

    public string Text => _text.Text;

    public byte[] Hash { get; }

    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Why nothing in this document can be changed (signed), or null.</summary>
    public string? DocumentProblem { get; private set; }

    private sealed record ZipPart(string Name, DateTimeOffset LastWrite, bool Stored, byte[] Data);

    private sealed class XmlPart(string name, XDocument xml)
    {
        public string Name { get; } = name;

        public XDocument Xml { get; } = xml;
    }

    /// <summary>One w:t element (or property element) with what's known about where it sits.</summary>
    private sealed record TextRun(XmlPart Part, XElement Element, string Format, bool Tracked, string? Field);

    private sealed record Property(XmlPart Part, XElement Element, string Label);

    public static DocxContent Load(byte[] bytes)
    {
        var content = new DocxContent(bytes);
        content.Read();
        return content;
    }

    private void Read()
    {
        using (var zip = new ZipArchive(new MemoryStream(_bytes), ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                using var stream = entry.Open();
                using var data = new MemoryStream();
                stream.CopyTo(data);
                _entries.Add(new ZipPart(entry.FullName, entry.LastWriteTime, entry.CompressedLength == entry.Length && entry.Length > 0, data.ToArray()));
            }
        }

        if (_entries.Any(e => e.Name.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase)))
        {
            DocumentProblem = "the document is digitally signed, and changing it would break the signature";
        }

        var main = MainPartName();
        var folder = main.Contains('/') ? main[..(main.LastIndexOf('/') + 1)] : "";

        _text.Section("properties", 0);
        _text.Label("Document properties\n");
        foreach (var name in new[] { "docProps/core.xml", "docProps/app.xml" })
        {
            if (LoadPart(name) is { } part)
            {
                AddProperties(part);
            }
        }

        _text.DropEmptySection();

        AddStory(main, "body");
        foreach (var (pattern, label) in new[] { ("header", "header"), ("footer", "footer"), ("footnotes", "footnotes"), ("endnotes", "endnotes"), ("comments", "comments") })
        {
            foreach (var entry in _entries.Where(e => IsStory(e.Name, folder, pattern)).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                AddStory(entry.Name, label);
            }
        }

        if (_parts.Count == 0)
        {
            throw new InvalidDataException("It isn't a Word document (no document part).");
        }
    }

    /// <summary>header1.xml, footer2.xml, footnotes.xml, … directly in the main part's folder.</summary>
    private static bool IsStory(string name, string folder, string pattern) =>
        name.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
        && System.Text.RegularExpressions.Regex.IsMatch(name[folder.Length..], pattern is "header" or "footer" ? $"^{pattern}\\d*\\.xml$" : $"^{pattern}\\.xml$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The main document part, from the package relationships (usually word/document.xml).</summary>
    private string MainPartName()
    {
        if (LoadXml("_rels/.rels") is { } rels)
        {
            foreach (var rel in rels.Descendants().Where(e => e.Name.LocalName == "Relationship"))
            {
                var type = (string?)rel.Attribute("Type") ?? "";
                if (type.EndsWith("/officeDocument", StringComparison.Ordinal) && (string?)rel.Attribute("Target") is { } target)
                {
                    return target.TrimStart('/');
                }
            }
        }

        return "word/document.xml";
    }

    private XDocument? LoadXml(string name)
    {
        var entry = _entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        using var reader = XmlReader.Create(new MemoryStream(entry.Data), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private XmlPart? LoadPart(string name)
    {
        if (LoadXml(name) is not { } xml)
        {
            return null;
        }

        var part = new XmlPart(_entries.First(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).Name, xml);
        _parts.Add(part);
        return part;
    }

    private void AddProperties(XmlPart part)
    {
        foreach (var element in part.Xml.Root?.Elements() ?? [])
        {
            if (!element.HasElements && element.Value.Length > 0 && PropertyNames.Contains(element.Name.LocalName))
            {
                var label = element.Name.LocalName switch
                {
                    "creator" => "Author",
                    "contentStatus" => "Status",
                    var n => char.ToUpperInvariant(n[0]) + n[1..],
                };
                int index = _properties.Count;
                _properties.Add(new Property(part, element, label));
                _text.Label($"  {label}: ");
                _text.Add(element.Value, DocumentCharKind.Property, index);
                _text.Label("\n");
            }
        }
    }

    /// <summary>A story (body, header, …): its paragraphs as lines, in reading order.</summary>
    private void AddStory(string name, string label)
    {
        if (LoadPart(name) is not { } part || part.Xml.Root is not { } root)
        {
            return;
        }

        XNamespace w = root.Name.Namespace;
        string place = label == "body" ? "body" : $"{label} ({Path.GetFileName(name)})";
        _text.Section(place, 0);
        _text.Label(label == "body" ? "Body\n" : $"{char.ToUpperInvariant(place[0])}{place[1..]}\n");

        // Field state runs across paragraphs: a stack of (instruction, in result?).
        var fields = new Stack<(StringBuilder Instruction, bool InResult)>();
        bool any = false;
        foreach (var paragraph in root.Descendants(w + "p").Where(p => !InFallback(p)))
        {
            bool wrote = false;
            foreach (var node in paragraph.Descendants().Where(e => NearestParagraph(e, w) == paragraph && !InFallback(e)))
            {
                switch (node.Name.LocalName)
                {
                    case "t" when node.Name.Namespace == w:
                        if (node.Value.Length == 0)
                        {
                            break;
                        }

                        string? field = FieldOf(node, w, fields);
                        int index = _runs.Count;
                        _runs.Add(new TextRun(part, node, FormatOf(node, w), Tracked(node, w), field));
                        _text.Add(node.Value, DocumentCharKind.Text, index);
                        wrote = true;
                        break;
                    case "tab" when node.Parent?.Name == w + "r":
                        _text.Add("\t", DocumentCharKind.Gap, -1);
                        break;
                    case "br" or "cr" when node.Parent?.Name == w + "r":
                        _text.Add(" ", DocumentCharKind.Gap, -1);
                        break;
                    case "fldChar":
                        var type = (string?)node.Attribute(w + "fldCharType");
                        if (type == "begin")
                        {
                            fields.Push((new StringBuilder(), false));
                        }
                        else if (type == "separate" && fields.Count > 0)
                        {
                            var top = fields.Pop();
                            fields.Push((top.Instruction, true));
                        }
                        else if (type == "end" && fields.Count > 0)
                        {
                            fields.Pop();
                        }

                        break;
                    case "instrText" when fields.Count > 0:
                        fields.Peek().Instruction.Append(node.Value);
                        break;
                }
            }

            if (wrote)
            {
                _text.Label("\n");
                any = true;
            }
        }

        if (!any)
        {
            _text.DropEmptySection();
        }
    }

    private static XElement? NearestParagraph(XElement e, XNamespace w) => e.Ancestors(w + "p").FirstOrDefault();

    /// <summary>mc:Fallback holds a second copy (for old readers) of what mc:Choice has.</summary>
    private static bool InFallback(XElement e) => e.Ancestors().Any(a => a.Name.LocalName == "Fallback");

    private static bool Tracked(XElement t, XNamespace w) => t.Ancestors().Any(a => a.Name == w + "ins" || a.Name == w + "moveTo");

    private static string? FieldOf(XElement t, XNamespace w, Stack<(StringBuilder Instruction, bool InResult)> fields)
    {
        if (t.Ancestors(w + "fldSimple").FirstOrDefault() is { } simple)
        {
            return ((string?)simple.Attribute(w + "instr"))?.Trim() ?? "field";
        }

        var open = fields.FirstOrDefault(f => f.InResult);
        return open.Instruction is null ? null : open.Instruction.ToString().Trim();
    }

    /// <summary>The run's formatting, ignoring revision ids and proofing marks that Word sprinkles everywhere.</summary>
    private static string FormatOf(XElement t, XNamespace w)
    {
        if (t.Parent?.Element(w + "rPr") is not { } rPr)
        {
            return "";
        }

        var copy = new XElement(rPr);
        copy.Descendants().Where(e => e.Name.LocalName is "lang" or "noProof").Remove();
        foreach (var element in copy.DescendantsAndSelf())
        {
            element.Attributes().Where(a => a.Name.LocalName.StartsWith("rsid", StringComparison.Ordinal) || a.Name.LocalName == "hint").Remove();
        }

        return copy.ToString(SaveOptions.DisableFormatting);
    }

    public bool IsSearchable(int start, int length) => _text.IsSearchable(start, length);

    public DocumentMatchInfo Describe(int start, int length, string replacement, ReplaceProfile profile)
    {
        var (place, _) = _text.SectionAt(start);
        if (_text.KindAt(start) == DocumentCharKind.Property)
        {
            var property = _properties[_text.OwnerAt(start)];
            return new DocumentMatchInfo(MatchContext.DocumentProperty, $"document {property.Label.ToLowerInvariant()}", "properties",
                DocumentProblem is null ? null : $"It can't be changed, because {DocumentProblem}.", []);
        }

        string detail = place;
        if (TextProblem(start, length, replacement) is { } reason)
        {
            return new DocumentMatchInfo(MatchContext.DocumentText, detail, place, reason, []);
        }

        var risks = new List<RiskFinding>();
        var runs = RunsOf(start, length).Select(i => _runs[i]).ToList();
        var original = _text.Text.Substring(start, length);
        if (runs.Select(r => r.Format).Distinct().Count() > 1)
        {
            var pieces = string.Join(" + ", SplitByRun(start, length).Select(p => $"“{p}”"));
            risks.Add(new RiskFinding(RiskCode.DocMixedFormatting,
                $"“{original}” is split across pieces with different formatting ({pieces}). “{replacement}” will all take the formatting of the first piece."));
        }

        if (runs.Any(r => r.Tracked))
        {
            risks.Add(new RiskFinding(RiskCode.DocTrackedChange,
                "This text is a tracked insertion that hasn't been accepted yet. The replacement becomes part of that insertion."));
        }

        // A hyperlink's text is just its label; Word doesn't recompute it.
        if (runs.FirstOrDefault(r => r.Field is not null && !r.Field.StartsWith("HYPERLINK", StringComparison.OrdinalIgnoreCase))?.Field is { } instruction)
        {
            risks.Add(new RiskFinding(RiskCode.DocField,
                $"This text is shown by a field ({FieldName(instruction)}). When Word updates fields it would bring the old text back, so the field's source (a document property, bookmark or setting) may need changing too."));
        }

        return new DocumentMatchInfo(MatchContext.DocumentText, detail, place, null, risks);
    }

    private static string FieldName(string instruction)
    {
        var text = instruction.Length > 60 ? instruction[..60] + "…" : instruction;
        return text.Length == 0 ? "a field" : text;
    }

    public string? CannotWrite(int start, int length, string replacement)
    {
        if (_text.KindAt(start) == DocumentCharKind.Property)
        {
            return DocumentProblem is null ? XmlProblem(replacement) : $"It can't be changed, because {DocumentProblem}.";
        }

        return TextProblem(start, length, replacement);
    }

    private string? TextProblem(int start, int length, string replacement)
    {
        if (DocumentProblem is not null)
        {
            return $"It can't be changed, because {DocumentProblem}.";
        }

        if (!_text.AllOfKind(start, length, DocumentCharKind.Text))
        {
            return "The match runs across a tab or line break, so it can't be replaced safely. Edit it by hand.";
        }

        return XmlProblem(replacement);
    }

    private static string? XmlProblem(string replacement)
    {
        foreach (char c in replacement)
        {
            if (!XmlConvert.IsXmlChar(c) && !char.IsSurrogate(c))
            {
                return $"The replacement contains a character Word documents can't store (U+{(int)c:X4}).";
            }

            if (c is '\n' or '\r' or '\t')
            {
                return "The replacement contains a tab or line break, which isn't plain text in a Word document. Edit it by hand.";
            }
        }

        return null;
    }

    private IEnumerable<int> RunsOf(int start, int length) =>
        Enumerable.Range(start, length).Select(_text.OwnerAt).Distinct();

    private IEnumerable<string> SplitByRun(int start, int length)
    {
        var sb = new StringBuilder();
        int owner = _text.OwnerAt(start);
        for (int i = start; i < start + length; i++)
        {
            if (_text.OwnerAt(i) != owner)
            {
                yield return sb.ToString();
                sb.Clear();
                owner = _text.OwnerAt(i);
            }

            sb.Append(_text.Text[i]);
        }

        yield return sb.ToString();
    }

    /// <summary>
    /// Writes the approved changes. A match over several runs gets the whole replacement in its
    /// first run; the matched letters are removed from the others, so their formatting and
    /// everything around them stays. Only the parts that changed are rewritten.
    /// </summary>
    public byte[] Apply(IEnumerable<ChangeItem> items)
    {
        // Edit copies, so the scanned document stays as it was (a plan can be applied more than once).
        var copies = new Dictionary<XmlPart, (XDocument Copy, Dictionary<XElement, XElement> Map)>();
        XElement CopyOf(XmlPart part, XElement element)
        {
            if (!copies.TryGetValue(part, out var copy))
            {
                var clone = new XDocument(part.Xml);
                var map = new Dictionary<XElement, XElement>(ReferenceEqualityComparer.Instance);
                foreach (var (original, copied) in part.Xml.Descendants().Zip(clone.Descendants()))
                {
                    map[original] = copied;
                }

                copies[part] = copy = (clone, map);
            }

            return copy.Map[element];
        }

        var edits = new Dictionary<XElement, List<TextEdit>>(ReferenceEqualityComparer.Instance);
        foreach (var item in items.Where(i => i.WillChange))
        {
            if (CannotWrite(item.Offset, item.Length, item.FinalText) is { } reason)
            {
                throw new InvalidOperationException($"“{item.OriginalText}” → “{item.FinalText}”: {reason}");
            }

            bool first = true;
            foreach (var (owner, offset, count) in _text.Pieces(item.Offset, item.Length))
            {
                var (part, element) = _text.KindAt(item.Offset) == DocumentCharKind.Property
                    ? (_properties[owner].Part, _properties[owner].Element)
                    : (_runs[owner].Part, _runs[owner].Element);
                var target = CopyOf(part, element);
                if (!edits.TryGetValue(target, out var list))
                {
                    edits[target] = list = [];
                }

                list.Add(new TextEdit(offset, count, first ? item.FinalText : ""));
                first = false;
            }
        }

        XNamespace xml = XNamespace.Xml;
        foreach (var (element, list) in edits)
        {
            var value = TextEdits.Apply(element.Value, list);
            element.Value = value;
            if (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])))
            {
                element.SetAttributeValue(xml + "space", "preserve");
            }
        }

        var changed = copies.ToDictionary(kv => kv.Key.Name, kv => Serialize(kv.Value.Copy), StringComparer.OrdinalIgnoreCase);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in _entries)
            {
                var data = changed.TryGetValue(entry.Name, out var bytes) ? bytes : entry.Data;
                var created = zip.CreateEntry(entry.Name, entry.Stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                created.LastWriteTime = entry.LastWrite;
                using var stream = created.Open();
                stream.Write(data);
            }
        }

        return output.ToArray();
    }

    private static byte[] Serialize(XDocument xml)
    {
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.None };
        using (var writer = XmlWriter.Create(stream, settings))
        {
            xml.Save(writer);
        }

        return stream.ToArray();
    }
}
