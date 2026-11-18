using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GigaReplace.Core.Documents;

/// <summary>
/// An old-style Word document (.doc, Word 97–2003). Its text is read from the piece table so
/// matches can be listed, but nothing is changed: the binary format can't be rewritten safely.
/// </summary>
public sealed class WordBinaryContent : IDocumentContent
{
    private const string ReadOnlyReason =
        "Old-style Word documents (.doc) can't be changed safely. Edit it by hand, or open it in Word, save it as .docx and scan again.";

    private readonly DocumentTextBuilder _text = new();

    static WordBinaryContent() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private WordBinaryContent(byte[] bytes) => Hash = SHA256.HashData(bytes);

    public string KindName => "Word 97–2003 document";

    public string Text => _text.Text;

    public byte[] Hash { get; }

    public IReadOnlyList<string> Notes { get; } = [];

    public static WordBinaryContent Load(byte[] bytes)
    {
        var file = new CompoundFile(bytes);
        var word = file.Stream("WordDocument") ?? throw new InvalidDataException("It isn't a Word document (no WordDocument stream).");
        if (word.Length < 0x01AA || BinaryPrimitives.ReadUInt16LittleEndian(word) != 0xA5EC)
        {
            throw new InvalidDataException("It isn't a Word 97–2003 document.");
        }

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(0x0A));
        if ((flags & 0x0100) != 0)
        {
            throw new InvalidDataException("It's password-protected.");
        }

        var table = file.Stream((flags & 0x0200) != 0 ? "1Table" : "0Table") ?? throw new InvalidDataException("Its table stream is missing.");
        int fcClx = BinaryPrimitives.ReadInt32LittleEndian(word.AsSpan(0x01A2));
        int lcbClx = BinaryPrimitives.ReadInt32LittleEndian(word.AsSpan(0x01A6));

        var content = new WordBinaryContent(bytes);
        content.Build(PieceText(word, table, fcClx, lcbClx));
        return content;
    }

    /// <summary>The document's characters, in order, from the piece table in the Clx.</summary>
    private static string PieceText(byte[] word, byte[] table, int fcClx, int lcbClx)
    {
        if (fcClx < 0 || lcbClx <= 0 || fcClx + lcbClx > table.Length)
        {
            throw new InvalidDataException("Its text table is damaged.");
        }

        int pos = fcClx, end = fcClx + lcbClx;
        while (pos < end && table[pos] == 0x01)
        {
            // Prc: formatting we don't need.
            pos += 3 + BinaryPrimitives.ReadInt16LittleEndian(table.AsSpan(pos + 1));
        }

        if (pos >= end || table[pos] != 0x02)
        {
            throw new InvalidDataException("Its text table is damaged.");
        }

        int lcb = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(pos + 1));
        int plc = pos + 5;
        int count = (lcb - 4) / 12;
        var cp1252 = Encoding.GetEncoding(1252);
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            int cpStart = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(plc + i * 4));
            int cpEnd = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(plc + (i + 1) * 4));
            uint fc = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(plc + (count + 1) * 4 + i * 8 + 2));
            int chars = Math.Max(0, cpEnd - cpStart);
            bool compressed = (fc & 0x40000000) != 0;
            int offset = compressed ? (int)((fc & ~0x40000000u) / 2) : (int)fc;
            int bytes = compressed ? chars : chars * 2;
            if (offset < 0 || offset + bytes > word.Length)
            {
                continue;
            }

            sb.Append(compressed ? cp1252.GetString(word, offset, bytes) : Encoding.Unicode.GetString(word, offset, bytes));
        }

        return sb.ToString();
    }

    /// <summary>Paragraphs as lines; field codes dropped (their results kept).</summary>
    private void Build(string raw)
    {
        _text.Section("body", 0);
        _text.Label("Body\n");
        var line = new StringBuilder();
        int fieldDepth = 0;
        var inCode = new Stack<bool>();

        void EndLine()
        {
            if (line.Length > 0)
            {
                _text.Add(line.ToString(), DocumentCharKind.Fixed, 0);
                _text.Label("\n");
                line.Clear();
            }
        }

        foreach (char c in raw)
        {
            switch (c)
            {
                case '\u0013':
                    fieldDepth++;
                    inCode.Push(true);
                    continue;
                case '\u0014':
                    if (inCode.Count > 0)
                    {
                        inCode.Pop();
                        inCode.Push(false);
                    }

                    continue;
                case '\u0015':
                    if (inCode.Count > 0)
                    {
                        inCode.Pop();
                        fieldDepth--;
                    }

                    continue;
            }

            if (inCode.Contains(true))
            {
                continue;
            }

            switch (c)
            {
                case '\r' or '\u0007' or '\u000C':
                    EndLine();
                    break;
                case '\u000B' or '\t':
                    line.Append(' ');
                    break;
                case '\u001E':
                    line.Append('-');
                    break;
                case < ' ':
                    break;
                default:
                    line.Append(c);
                    break;
            }
        }

        EndLine();
        _text.DropEmptySection();
    }

    public bool IsSearchable(int start, int length) => _text.IsSearchable(start, length);

    public DocumentMatchInfo Describe(int start, int length, string replacement, ReplaceProfile profile) =>
        new(MatchContext.DocumentText, "body", "body", ReadOnlyReason, [new RiskFinding(RiskCode.PdfEditByHand, ReadOnlyReason)]);

    public string? CannotWrite(int start, int length, string replacement) => ReadOnlyReason;

    public byte[] Apply(IEnumerable<ChangeItem> items) =>
        items.Any(i => i.WillChange) ? throw new InvalidOperationException(ReadOnlyReason) : throw new InvalidOperationException("Nothing to change.");
}

/// <summary>Reads streams from a Compound File Binary container (the format of .doc, .xls, .msg, …).</summary>
internal sealed class CompoundFile
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private readonly byte[] _data;
    private readonly int _sectorSize;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly byte[] _miniStream;
    private readonly int _miniCutoff;
    private readonly List<(string Name, int Type, uint Start, long Size)> _entries = [];

    public CompoundFile(byte[] data)
    {
        _data = data;
        if (data.Length < 512 || BinaryPrimitives.ReadUInt64LittleEndian(data) != 0xE11AB1A1E011CFD0)
        {
            throw new InvalidDataException("It isn't a Word 97–2003 document (or it's damaged).");
        }

        _sectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x1E));
        int fatSectors = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x2C));
        uint firstDirectory = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x30));
        _miniCutoff = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x38));
        uint firstMiniFat = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x3C));
        uint firstDifat = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x44));

        // The FAT's own sectors: 109 in the header, the rest in a DIFAT chain.
        var fatSectorIds = new List<uint>();
        for (int i = 0; i < 109 && fatSectorIds.Count < fatSectors; i++)
        {
            fatSectorIds.Add(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x4C + i * 4)));
        }

        for (uint difat = firstDifat, guard = 0; difat < EndOfChain && fatSectorIds.Count < fatSectors && guard < 10_000; guard++)
        {
            var sector = Sector(difat);
            int perSector = _sectorSize / 4 - 1;
            for (int i = 0; i < perSector && fatSectorIds.Count < fatSectors; i++)
            {
                fatSectorIds.Add(BinaryPrimitives.ReadUInt32LittleEndian(sector[(i * 4)..]));
            }

            difat = BinaryPrimitives.ReadUInt32LittleEndian(sector[(perSector * 4)..]);
        }

        _fat = fatSectorIds.SelectMany(id => Words(Sector(id))).ToArray();
        _miniFat = Words(Chain(firstMiniFat, _fat, Sector, _sectorSize)).ToArray();

        var directory = Chain(firstDirectory, _fat, Sector, _sectorSize);
        for (int offset = 0; offset + 128 <= directory.Length; offset += 128)
        {
            var entry = directory.AsSpan(offset, 128);
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[0x40..]);
            var name = Encoding.Unicode.GetString(entry[..Math.Clamp(nameLength - 2, 0, 64)]);
            _entries.Add((name, entry[0x42], BinaryPrimitives.ReadUInt32LittleEndian(entry[0x74..]), (long)BinaryPrimitives.ReadUInt32LittleEndian(entry[0x78..])));
        }

        var root = _entries.FirstOrDefault(e => e.Type == 5);
        _miniStream = root.Name is null ? [] : Chain(root.Start, _fat, Sector, _sectorSize);
    }

    public byte[]? Stream(string name)
    {
        var entry = _entries.FirstOrDefault(e => e.Type == 2 && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (entry.Name is null)
        {
            return null;
        }

        var bytes = entry.Size < _miniCutoff
            ? Chain(entry.Start, _miniFat, MiniSector, 64)
            : Chain(entry.Start, _fat, Sector, _sectorSize);
        return bytes.Length > entry.Size ? bytes[..(int)entry.Size] : bytes;
    }

    private ReadOnlySpan<byte> Sector(uint id)
    {
        long offset = (long)(id + 1) * _sectorSize;
        return offset + _sectorSize <= _data.Length ? _data.AsSpan((int)offset, _sectorSize) : ReadOnlySpan<byte>.Empty;
    }

    private ReadOnlySpan<byte> MiniSector(uint id)
    {
        long offset = (long)id * 64;
        return offset + 64 <= _miniStream.Length ? _miniStream.AsSpan((int)offset, 64) : ReadOnlySpan<byte>.Empty;
    }

    private delegate ReadOnlySpan<byte> SectorReader(uint id);

    private static byte[] Chain(uint start, uint[] fat, SectorReader read, int size)
    {
        var result = new List<byte>();
        for (uint id = start, guard = 0; id < EndOfChain && id < fat.Length && guard < 1_000_000; id = fat[id], guard++)
        {
            result.AddRange(read(id).ToArray());
        }

        return [.. result];
    }

    private static IEnumerable<uint> Words(ReadOnlySpan<byte> bytes)
    {
        var words = new uint[bytes.Length / 4];
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i * 4)..]);
        }

        return words;
    }
}
