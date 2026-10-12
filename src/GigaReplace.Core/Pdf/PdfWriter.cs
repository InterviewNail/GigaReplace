using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using GigaReplace.Core.Text;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace GigaReplace.Core.Pdf;

/// <summary>
/// Writes approved PDF changes as an incremental update: the original bytes stay as they are and
/// new versions of the changed objects (content streams, the Info dictionary, the XMP packet) are
/// appended with a new cross-reference section.
/// </summary>
internal static class PdfWriter
{
    private static readonly string[] StreamKeysToDrop = ["Filter", "DecodeParms", "Length", "DL", "F", "FFilter", "FDecodeParms"];

    public static byte[] Apply(PdfContent pdf, IEnumerable<ChangeItem> items)
    {
        var changes = items.Where(i => i.WillChange).OrderBy(i => i.Offset).ToList();
        var byShow = new Dictionary<PdfShow, List<(int First, int Last, int[] Codes, double? Width)>>();
        var byProperty = new Dictionary<PdfProperty, List<TextEdit>>();
        foreach (var item in changes)
        {
            if (pdf.CannotWrite(item.Offset, item.Length, item.FinalText) is { } reason)
            {
                throw new InvalidOperationException($"“{item.OriginalText}” → “{item.FinalText}”: {reason}");
            }

            if (pdf.KindAt(item.Offset) == PdfCharKind.Property)
            {
                var (property, offset) = pdf.PropertyAt(item.Offset);
                Add(byProperty, property, new TextEdit(offset, item.Length, item.FinalText));
            }
            else
            {
                // The new text goes in the first piece, sized to the whole match; later pieces only lose their glyphs.
                var segments = pdf.Segments(item.Offset, item.Length, item.FinalText, out var codes)!;
                double width = segments.Sum(s => s.Width);
                for (int k = 0; k < segments.Count; k++)
                {
                    var segment = segments[k];
                    Add(byShow, segment.Show, k == 0 ? (segment.First, segment.Last, codes!, width) : (segment.First, segment.Last, [], (double?)null));
                }
            }
        }

        var update = new IncrementalUpdate(pdf);

        foreach (var stream in byShow.GroupBy(kv => kv.Key.Stream))
        {
            var splices = stream
                .Select(kv => (kv.Key.Op.Start, kv.Key.Op.End, Text: " " + Rewrite(kv.Key, kv.Value) + " "))
                .OrderBy(s => s.Start)
                .ToList();
            var data = new MemoryStream();
            int pos = 0;
            foreach (var (start, end, text) in splices)
            {
                data.Write(stream.Key.Data, pos, start - pos);
                data.Write(Encoding.ASCII.GetBytes(text));
                pos = end;
            }

            data.Write(stream.Key.Data, pos, stream.Key.Data.Length - pos);
            update.Replace(stream.Key.Reference, StreamObject(stream.Key.Token.StreamDictionary, data.ToArray(), compress: true));
        }

        var infoEdits = byProperty.Where(kv => kv.Key.InfoKey is not null).ToDictionary(kv => kv.Key.InfoKey!, kv => TextEdits.Apply(kv.Key.Value, kv.Value));
        if (infoEdits.Count > 0 && pdf.InfoDictionary is { } info)
        {
            var body = InfoObject(info, infoEdits);
            if (pdf.Trailer.Info is IndirectReferenceToken infoRef)
            {
                update.Replace(infoRef.Data, body);
            }
            else
            {
                update.InfoReference = update.Add(body);
            }
        }

        var xmpEdits = byProperty.Where(kv => kv.Key.XmpStart >= 0)
            .SelectMany(kv => kv.Value.Select(e => new TextEdit(kv.Key.XmpStart + e.Start, e.Length, SecurityElement.Escape(e.Replacement))))
            .ToList();
        if (xmpEdits.Count > 0 && pdf.Xmp is { } xmp)
        {
            var xml = TextEdits.Apply(xmp.Xml, xmpEdits);
            var bytes = Encoding.UTF8.GetBytes(xml);
            if (xmp.Bom)
            {
                bytes = [0xEF, 0xBB, 0xBF, .. bytes];
            }

            update.Replace(xmp.Reference, StreamObject(xmp.Token.StreamDictionary, bytes, compress: false));
        }

        return update.Write();
    }

    private static void Add<TKey, TValue>(Dictionary<TKey, List<TValue>> map, TKey key, TValue value) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        list.Add(value);
    }

    /// <summary>
    /// The operators that replace one text-showing operator. Each replaced run is drawn with its
    /// horizontal scaling adjusted so it takes exactly the original width, so the rest of the line
    /// stays where it was.
    /// </summary>
    private static string Rewrite(PdfShow show, List<(int First, int Last, int[] Codes, double? Width)> edits)
    {
        var sb = new StringBuilder();
        var ops = show.Op.Operands;
        if (show.Op.Operator == "'")
        {
            sb.Append("T* ");
        }
        else if (show.Op.Operator == "\"")
        {
            sb.Append(F(Num(ops, 3))).Append(" Tw ").Append(F(Num(ops, 2))).Append(" Tc T* ");
        }

        var pending = new List<PdfPart>();
        int i = 0;
        foreach (var (first, last, codes, width) in edits.OrderBy(e => e.First))
        {
            if (first < i)
            {
                continue;
            }

            pending.AddRange(show.Parts.Skip(i).Take(first - i));
            Flush(sb, show, pending);

            double before = width ?? show.Advance(show.Parts.Skip(first).Take(last - first + 1));
            double after = show.Advance(codes.Select(c => new PdfPart(c, 0)));
            if (codes.Length == 0 || after <= 0 || show.Size == 0)
            {
                // Nothing to draw: just move on by the old width.
                if (show.Size != 0)
                {
                    sb.Append('[').Append(F(-before * 1000 / show.Size)).Append("] TJ ");
                }
            }
            else
            {
                double scale = show.HorizontalScale * 100;
                double fitted = Math.Clamp(scale * before / after, 1, 1000);
                sb.Append(F(fitted)).Append(" Tz [<").Append(Convert.ToHexString(show.Font.Bytes(codes))).Append(">] TJ ")
                  .Append(F(scale)).Append(" Tz ");
            }

            i = last + 1;
        }

        pending.AddRange(show.Parts.Skip(i));
        Flush(sb, show, pending);
        return sb.ToString().TrimEnd();
    }

    private static void Flush(StringBuilder sb, PdfShow show, List<PdfPart> parts)
    {
        if (parts.Count == 0)
        {
            return;
        }

        sb.Append('[');
        var glyphs = new List<int>();
        foreach (var part in parts)
        {
            if (part.IsKern)
            {
                FlushGlyphs();
                sb.Append(F(part.Kern)).Append(' ');
            }
            else
            {
                glyphs.Add(part.Code);
            }
        }

        FlushGlyphs();
        sb.Append("] TJ ");
        parts.Clear();

        void FlushGlyphs()
        {
            if (glyphs.Count > 0)
            {
                sb.Append('<').Append(Convert.ToHexString(show.Font.Bytes(glyphs))).Append("> ");
                glyphs.Clear();
            }
        }
    }

    private static string InfoObject(DictionaryToken info, Dictionary<string, string> values)
    {
        var sb = new StringBuilder("<<");
        foreach (var (key, token) in info.Data)
        {
            if (key == "ModDate")
            {
                continue;
            }

            sb.Append(" /").Append(PdfSerializer.Name(key)).Append(' ');
            sb.Append(values.TryGetValue(key, out var value) ? PdfText.Encode(value) : PdfSerializer.Write(token));
        }

        var now = DateTimeOffset.Now;
        var offset = now.Offset;
        var zone = offset == TimeSpan.Zero ? "Z" : $"{(offset < TimeSpan.Zero ? '-' : '+')}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'";
        sb.Append(" /ModDate (D:").Append(now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)).Append(zone).Append(") >>");
        return sb.ToString();
    }

    private static byte[] StreamObject(DictionaryToken dictionary, byte[] data, bool compress)
    {
        if (compress)
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(data);
            }

            data = output.ToArray();
        }

        var sb = new StringBuilder("<<");
        foreach (var (key, token) in dictionary.Data)
        {
            if (!StreamKeysToDrop.Contains(key))
            {
                sb.Append(" /").Append(PdfSerializer.Name(key)).Append(' ').Append(PdfSerializer.Write(token));
            }
        }

        if (compress)
        {
            sb.Append(" /Filter /FlateDecode");
        }

        sb.Append(" /Length ").Append(data.Length).Append(" >>\nstream\n");
        return [.. Encoding.ASCII.GetBytes(sb.ToString()), .. data, .. "\nendstream"u8];
    }

    private static double Num(List<PdfOperand> a, int fromEnd) => a.Count >= fromEnd && a[^fromEnd] is PdfNumber n ? n.Value : 0;

    internal static string F(double value)
    {
        var text = Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    /// <summary>Collects replaced and added objects, then appends them with a cross-reference section.</summary>
    private sealed class IncrementalUpdate(PdfContent pdf)
    {
        private readonly SortedDictionary<long, (int Generation, byte[] Body)> _objects = [];
        private long _next = Math.Max(pdf.Trailer.Size, 1);

        public IndirectReference? InfoReference { get; set; }

        public void Replace(IndirectReference reference, string body) => Replace(reference, Encoding.ASCII.GetBytes(body));

        public void Replace(IndirectReference reference, byte[] body) => _objects[reference.ObjectNumber] = (reference.Generation, body);

        public IndirectReference Add(string body)
        {
            var reference = new IndirectReference(_next++, 0);
            Replace(reference, body);
            return reference;
        }

        public byte[] Write()
        {
            var original = pdf.Bytes;
            var output = new MemoryStream();
            output.Write(original);
            if (original.Length > 0 && original[^1] != '\n' && original[^1] != '\r')
            {
                output.WriteByte((byte)'\n');
            }

            var offsets = new SortedDictionary<long, (long Offset, int Generation)>();
            foreach (var (number, (generation, body)) in _objects)
            {
                offsets[number] = (output.Position, generation);
                output.Write(Encoding.ASCII.GetBytes($"{number} {generation} obj\n"));
                output.Write(body);
                output.Write("\nendobj\n"u8);
            }

            long previous = LastStartXref(original);
            var trailer = new StringBuilder();
            trailer.Append(" /Root ").Append(Ref(pdf.Trailer.Root));
            if (InfoReference is { } newInfo)
            {
                trailer.Append(" /Info ").Append(Ref(newInfo));
            }
            else if (pdf.Trailer.Info is { } info)
            {
                trailer.Append(" /Info ").Append(PdfSerializer.Write(info));
            }

            if (pdf.Trailer.Identifier is { Count: > 0 } id)
            {
                trailer.Append(" /ID [").Append(string.Join(" ", id.Select(PdfSerializer.Write))).Append(']');
            }

            trailer.Append(" /Prev ").Append(previous);

            long xrefOffset;
            if (!original.AsSpan((int)Math.Min(previous, original.Length)).TrimStart(" \t\r\n"u8).StartsWith("xref"u8))
            {
                // The original uses a cross-reference stream, so this update does too.
                long self = _next++;
                xrefOffset = output.Position;
                offsets[self] = (xrefOffset, 0);
                var entries = new MemoryStream();
                foreach (var (_, (offset, generation)) in offsets)
                {
                    entries.WriteByte(1);
                    entries.Write([(byte)(offset >> 24), (byte)(offset >> 16), (byte)(offset >> 8), (byte)offset]);
                    entries.Write([(byte)(generation >> 8), (byte)generation]);
                }

                var index = string.Join(" ", offsets.Keys.Select(n => $"{n} 1"));
                var head = $"{self} 0 obj\n<< /Type /XRef /Size {_next} /W [1 4 2] /Index [{index}]{trailer} /Length {entries.Length} >>\nstream\n";
                output.Write(Encoding.ASCII.GetBytes(head));
                output.Write(entries.ToArray());
                output.Write("\nendstream\nendobj\n"u8);
            }
            else
            {
                xrefOffset = output.Position;
                var xref = new StringBuilder("xref\n");
                foreach (var (number, (offset, generation)) in offsets)
                {
                    xref.Append(number).Append(" 1\n").Append(offset.ToString("D10")).Append(' ').Append(generation.ToString("D5")).Append(" n\r\n");
                }

                xref.Append("trailer\n<< /Size ").Append(_next).Append(trailer).Append(" >>\n");
                output.Write(Encoding.ASCII.GetBytes(xref.ToString()));
            }

            output.Write(Encoding.ASCII.GetBytes($"startxref\n{xrefOffset}\n%%EOF\n"));
            return output.ToArray();
        }

        private static string Ref(IndirectReference r) => $"{r.ObjectNumber} {r.Generation} R";

        private static long LastStartXref(byte[] bytes)
        {
            var marker = "startxref"u8;
            int at = bytes.AsSpan().LastIndexOf(marker);
            if (at < 0)
            {
                throw new InvalidOperationException("The PDF has no cross-reference position (startxref).");
            }

            int pos = at + marker.Length;
            while (pos < bytes.Length && PdfContentLexer.IsSpace(bytes[pos]))
            {
                pos++;
            }

            long value = 0;
            while (pos < bytes.Length && bytes[pos] >= '0' && bytes[pos] <= '9')
            {
                value = value * 10 + (bytes[pos++] - '0');
            }

            return value;
        }
    }
}

/// <summary>Writes PdfPig tokens back as PDF syntax.</summary>
internal static class PdfSerializer
{
    public static string Write(IToken token) => token switch
    {
        NameToken name => "/" + Name(name.Data),
        NumericToken number => number.HasDecimalPlaces ? PdfWriter.F(number.Double) : number.Long.ToString(CultureInfo.InvariantCulture),
        StringToken s => "<" + Convert.ToHexString(s.GetBytes()) + ">",
        HexToken hex => "<" + Convert.ToHexString(hex.Bytes) + ">",
        ArrayToken array => "[" + string.Join(" ", array.Data.Select(Write)) + "]",
        DictionaryToken dictionary => "<<" + string.Concat(dictionary.Data.Select(kv => $" /{Name(kv.Key)} {Write(kv.Value)}")) + " >>",
        IndirectReferenceToken r => $"{r.Data.ObjectNumber} {r.Data.Generation} R",
        BooleanToken b => b.Data ? "true" : "false",
        NullToken => "null",
        _ => throw new NotSupportedException($"Can't write a {token.GetType().Name} back to the PDF."),
    };

    public static string Name(string name)
    {
        var sb = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(name))
        {
            if (b > 32 && b < 127 && "()<>[]{}/%#".IndexOf((char)b) < 0)
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('#').Append(b.ToString("X2"));
            }
        }

        return sb.ToString();
    }
}
