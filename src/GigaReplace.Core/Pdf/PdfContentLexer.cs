using System.Globalization;
using System.Text;

namespace GigaReplace.Core.Pdf;

internal abstract record PdfOperand;

internal sealed record PdfNumber(double Value) : PdfOperand;

internal sealed record PdfName(string Value) : PdfOperand;

/// <summary>A literal or hex string, as raw bytes.</summary>
internal sealed record PdfBytes(byte[] Value) : PdfOperand;

internal sealed record PdfArray(List<PdfOperand> Items) : PdfOperand;

/// <summary>Dictionaries, booleans and null: kept only so operand counts stay right.</summary>
internal sealed record PdfOther : PdfOperand;

/// <summary>One content-stream operator with its operands. <see cref="Start"/>..<see cref="End"/> spans both.</summary>
internal sealed record PdfOperation(string Operator, List<PdfOperand> Operands, int Start, int End);

/// <summary>
/// Splits a decoded content stream into operations, remembering where each one sits in the
/// bytes so an edited operation can be spliced back without touching anything else.
/// </summary>
internal static class PdfContentLexer
{
    public static List<PdfOperation> Parse(ReadOnlySpan<byte> data)
    {
        var ops = new List<PdfOperation>();
        var operands = new List<PdfOperand>();
        int operandStart = -1;
        int pos = 0;
        while (true)
        {
            SkipSpace(data, ref pos);
            if (pos >= data.Length)
            {
                return ops;
            }

            int tokenStart = pos;
            var operand = ReadOperand(data, ref pos, out string? keyword);
            if (operand is not null)
            {
                if (operandStart < 0)
                {
                    operandStart = tokenStart;
                }

                operands.Add(operand);
                continue;
            }

            if (keyword is null)
            {
                // A stray delimiter: drop it and whatever was pending.
                pos++;
                operands = [];
                operandStart = -1;
                continue;
            }

            int start = operandStart < 0 ? tokenStart : operandStart;
            if (keyword == "BI")
            {
                SkipInlineImage(data, ref pos);
            }

            ops.Add(new PdfOperation(keyword, operands, start, pos));
            operands = [];
            operandStart = -1;
        }
    }

    private static PdfOperand? ReadOperand(ReadOnlySpan<byte> data, ref int pos, out string? keyword)
    {
        keyword = null;
        byte b = data[pos];
        switch (b)
        {
            case (byte)'/':
                return new PdfName(ReadName(data, ref pos));
            case (byte)'(':
                return new PdfBytes(ReadLiteral(data, ref pos));
            case (byte)'<' when pos + 1 < data.Length && data[pos + 1] == '<':
                SkipDictionary(data, ref pos);
                return new PdfOther();
            case (byte)'<':
                return new PdfBytes(ReadHex(data, ref pos));
            case (byte)'[':
                {
                    pos++;
                    var items = new List<PdfOperand>();
                    while (true)
                    {
                        SkipSpace(data, ref pos);
                        if (pos >= data.Length)
                        {
                            return new PdfArray(items);
                        }

                        if (data[pos] == ']')
                        {
                            pos++;
                            return new PdfArray(items);
                        }

                        var item = ReadOperand(data, ref pos, out var inner);
                        if (item is not null)
                        {
                            items.Add(item);
                        }
                        else if (inner is null)
                        {
                            pos++;
                        }
                    }
                }

            case (byte)']' or (byte)')' or (byte)'>' or (byte)'{' or (byte)'}':
                return null;
        }

        if (b is (byte)'+' or (byte)'-' or (byte)'.' || (b >= '0' && b <= '9'))
        {
            int start = pos;
            while (pos < data.Length && !IsSpace(data[pos]) && !IsDelimiter(data[pos]))
            {
                pos++;
            }

            var text = Encoding.ASCII.GetString(data[start..pos]);
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                return new PdfNumber(value);
            }

            keyword = text;
            return null;
        }

        int wordStart = pos;
        while (pos < data.Length && !IsSpace(data[pos]) && !IsDelimiter(data[pos]))
        {
            pos++;
        }

        var word = Encoding.ASCII.GetString(data[wordStart..pos]);
        if (word is "true" or "false" or "null")
        {
            return new PdfOther();
        }

        keyword = word;
        return null;
    }

    private static string ReadName(ReadOnlySpan<byte> data, ref int pos)
    {
        pos++;
        var bytes = new List<byte>();
        while (pos < data.Length && !IsSpace(data[pos]) && !IsDelimiter(data[pos]))
        {
            if (data[pos] == '#' && pos + 2 < data.Length && IsHex(data[pos + 1]) && IsHex(data[pos + 2]))
            {
                bytes.Add((byte)(HexValue(data[pos + 1]) * 16 + HexValue(data[pos + 2])));
                pos += 3;
            }
            else
            {
                bytes.Add(data[pos++]);
            }
        }

        return Encoding.Latin1.GetString([.. bytes]);
    }

    private static byte[] ReadLiteral(ReadOnlySpan<byte> data, ref int pos)
    {
        pos++;
        var bytes = new List<byte>();
        int depth = 1;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            if (b == '\\')
            {
                if (pos >= data.Length)
                {
                    break;
                }

                byte e = data[pos++];
                switch (e)
                {
                    case (byte)'n': bytes.Add((byte)'\n'); break;
                    case (byte)'r': bytes.Add((byte)'\r'); break;
                    case (byte)'t': bytes.Add((byte)'\t'); break;
                    case (byte)'b': bytes.Add(8); break;
                    case (byte)'f': bytes.Add(12); break;
                    case (byte)'\r':
                        if (pos < data.Length && data[pos] == '\n')
                        {
                            pos++;
                        }

                        break;
                    case (byte)'\n':
                        break;
                    default:
                        if (e >= '0' && e <= '7')
                        {
                            int value = e - '0';
                            for (int k = 0; k < 2 && pos < data.Length && data[pos] >= '0' && data[pos] <= '7'; k++)
                            {
                                value = value * 8 + (data[pos++] - '0');
                            }

                            bytes.Add((byte)value);
                        }
                        else
                        {
                            bytes.Add(e);
                        }

                        break;
                }
            }
            else if (b == '(')
            {
                depth++;
                bytes.Add(b);
            }
            else if (b == ')')
            {
                if (--depth == 0)
                {
                    break;
                }

                bytes.Add(b);
            }
            else if (b == '\r')
            {
                if (pos < data.Length && data[pos] == '\n')
                {
                    pos++;
                }

                bytes.Add((byte)'\n');
            }
            else
            {
                bytes.Add(b);
            }
        }

        return [.. bytes];
    }

    private static byte[] ReadHex(ReadOnlySpan<byte> data, ref int pos)
    {
        pos++;
        var bytes = new List<byte>();
        int high = -1;
        while (pos < data.Length && data[pos] != '>')
        {
            byte b = data[pos++];
            if (!IsHex(b))
            {
                continue;
            }

            if (high < 0)
            {
                high = HexValue(b);
            }
            else
            {
                bytes.Add((byte)(high * 16 + HexValue(b)));
                high = -1;
            }
        }

        if (high >= 0)
        {
            bytes.Add((byte)(high * 16));
        }

        pos = Math.Min(pos + 1, data.Length);
        return [.. bytes];
    }

    private static void SkipDictionary(ReadOnlySpan<byte> data, ref int pos)
    {
        int depth = 0;
        while (pos < data.Length)
        {
            if (data[pos] == '(')
            {
                ReadLiteral(data, ref pos);
            }
            else if (data[pos] == '<' && pos + 1 < data.Length && data[pos + 1] == '<')
            {
                depth++;
                pos += 2;
            }
            else if (data[pos] == '>' && pos + 1 < data.Length && data[pos + 1] == '>')
            {
                pos += 2;
                if (--depth == 0)
                {
                    return;
                }
            }
            else
            {
                pos++;
            }
        }
    }

    /// <summary>Skips "… ID &lt;binary&gt; EI" after a BI operator.</summary>
    private static void SkipInlineImage(ReadOnlySpan<byte> data, ref int pos)
    {
        int id = IndexOfKeyword(data, pos, "ID"u8);
        if (id < 0)
        {
            pos = data.Length;
            return;
        }

        int search = id + 3;
        while (search < data.Length)
        {
            int ei = IndexOfKeyword(data, search, "EI"u8);
            if (ei < 0)
            {
                pos = data.Length;
                return;
            }

            if (ei + 2 >= data.Length || IsSpace(data[ei + 2]))
            {
                pos = ei + 2;
                return;
            }

            search = ei + 2;
        }

        pos = data.Length;
    }

    /// <summary>The next occurrence of <paramref name="keyword"/> preceded by whitespace.</summary>
    private static int IndexOfKeyword(ReadOnlySpan<byte> data, int from, ReadOnlySpan<byte> keyword)
    {
        for (int i = Math.Max(from, 1); i + keyword.Length <= data.Length; i++)
        {
            if (IsSpace(data[i - 1]) && data[i..].StartsWith(keyword))
            {
                return i;
            }
        }

        return -1;
    }

    private static void SkipSpace(ReadOnlySpan<byte> data, ref int pos)
    {
        while (pos < data.Length)
        {
            if (IsSpace(data[pos]))
            {
                pos++;
            }
            else if (data[pos] == '%')
            {
                while (pos < data.Length && data[pos] != '\n' && data[pos] != '\r')
                {
                    pos++;
                }
            }
            else
            {
                return;
            }
        }
    }

    public static bool IsSpace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
        or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static bool IsHex(byte b) => (b >= '0' && b <= '9') || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F');

    private static int HexValue(byte b) => b <= '9' ? b - '0' : (b | 0x20) - 'a' + 10;
}
