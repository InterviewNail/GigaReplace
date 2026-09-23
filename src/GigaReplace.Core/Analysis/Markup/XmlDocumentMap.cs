namespace GigaReplace.Core.Analysis.Markup;

internal enum XmlSpanKind
{
    ElementName,
    AttributeName,
    AttributeValue,
    Text,
    Comment,
    CData,
    Other,
}

/// <summary>
/// A region of an XML-like document. <see cref="Element"/> is the tag the span belongs to (for
/// text: the enclosing element); <see cref="Parent"/> is that element's parent.
/// </summary>
internal readonly record struct XmlSpan(XmlSpanKind Kind, int Start, int End, string Element, string? Attribute, string Parent)
{
    public bool Contains(int offset) => offset >= Start && offset < End;
}

/// <summary>
/// A forgiving, position-preserving tokenizer for project files, XAML, resx and config files.
/// It never throws on malformed input.
/// </summary>
internal sealed class XmlDocumentMap
{
    private readonly List<XmlSpan> _spans;

    private XmlDocumentMap(List<XmlSpan> spans) => _spans = spans;

    public IReadOnlyList<XmlSpan> Spans => _spans;

    public XmlSpan? Find(int offset)
    {
        int lo = 0, hi = _spans.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_spans[mid].Start <= offset)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found >= 0 && _spans[found].Contains(offset) ? _spans[found] : null;
    }

    public static XmlDocumentMap Build(string text)
    {
        var spans = new List<XmlSpan>();
        var stack = new List<string>();
        int n = text.Length;
        int i = 0;

        string Top() => stack.Count > 0 ? stack[^1] : "";
        string Below() => stack.Count > 1 ? stack[^2] : "";

        while (i < n)
        {
            if (text[i] != '<')
            {
                int next = text.IndexOf('<', i);
                if (next < 0)
                {
                    next = n;
                }

                spans.Add(new XmlSpan(XmlSpanKind.Text, i, next, Top(), null, Below()));
                i = next;
                continue;
            }

            if (Starts(text, i, "<!--"))
            {
                int end = IndexAfter(text, "-->", i + 4);
                spans.Add(new XmlSpan(XmlSpanKind.Comment, i, end, Top(), null, Below()));
                i = end;
                continue;
            }

            if (Starts(text, i, "<![CDATA["))
            {
                int end = IndexAfter(text, "]]>", i + 9);
                spans.Add(new XmlSpan(XmlSpanKind.CData, i, end, Top(), null, Below()));
                i = end;
                continue;
            }

            if (Starts(text, i, "<?"))
            {
                int end = IndexAfter(text, "?>", i + 2);
                spans.Add(new XmlSpan(XmlSpanKind.Other, i, end, Top(), null, Below()));
                i = end;
                continue;
            }

            if (Starts(text, i, "<!"))
            {
                int end = IndexAfter(text, ">", i + 2);
                spans.Add(new XmlSpan(XmlSpanKind.Other, i, end, Top(), null, Below()));
                i = end;
                continue;
            }

            bool closing = i + 1 < n && text[i + 1] == '/';
            int p = i + (closing ? 2 : 1);
            int nameStart = p;
            while (p < n && !char.IsWhiteSpace(text[p]) && text[p] != '>' && text[p] != '/' && text[p] != '<')
            {
                p++;
            }

            string name = text[nameStart..p];
            if (closing)
            {
                int index = stack.LastIndexOf(name);
                string parent = index > 0 ? stack[index - 1] : "";
                spans.Add(new XmlSpan(XmlSpanKind.ElementName, nameStart, p, name, null, parent));
                if (index >= 0)
                {
                    stack.RemoveRange(index, stack.Count - index);
                }

                int gt = text.IndexOf('>', p);
                i = gt < 0 ? n : gt + 1;
                continue;
            }

            string owner = Top();
            spans.Add(new XmlSpan(XmlSpanKind.ElementName, nameStart, p, name, null, owner));
            bool selfClosing = false;
            while (p < n)
            {
                while (p < n && char.IsWhiteSpace(text[p]))
                {
                    p++;
                }

                if (p >= n)
                {
                    break;
                }

                if (text[p] == '>')
                {
                    p++;
                    break;
                }

                if (text[p] == '<')
                {
                    // Malformed: a new tag started before this one closed.
                    break;
                }

                if (text[p] == '/')
                {
                    if (p + 1 < n && text[p + 1] == '>')
                    {
                        selfClosing = true;
                        p += 2;
                        break;
                    }

                    p++;
                    continue;
                }

                int attributeStart = p;
                while (p < n && text[p] != '=' && !char.IsWhiteSpace(text[p]) && text[p] != '>' && text[p] != '/' && text[p] != '<')
                {
                    p++;
                }

                if (p == attributeStart)
                {
                    p++;
                    continue;
                }

                string attribute = text[attributeStart..p];
                spans.Add(new XmlSpan(XmlSpanKind.AttributeName, attributeStart, p, name, attribute, owner));
                while (p < n && char.IsWhiteSpace(text[p]))
                {
                    p++;
                }

                if (p < n && text[p] == '=')
                {
                    p++;
                    while (p < n && char.IsWhiteSpace(text[p]))
                    {
                        p++;
                    }

                    if (p < n && text[p] is '"' or '\'')
                    {
                        char quote = text[p];
                        int valueStart = p + 1;
                        int valueEnd = text.IndexOf(quote, valueStart);
                        if (valueEnd < 0)
                        {
                            valueEnd = n;
                        }

                        spans.Add(new XmlSpan(XmlSpanKind.AttributeValue, valueStart, valueEnd, name, attribute, owner));
                        p = Math.Min(n, valueEnd + 1);
                    }
                    else
                    {
                        int valueStart = p;
                        while (p < n && !char.IsWhiteSpace(text[p]) && text[p] != '>')
                        {
                            p++;
                        }

                        spans.Add(new XmlSpan(XmlSpanKind.AttributeValue, valueStart, p, name, attribute, owner));
                    }
                }
            }

            if (!selfClosing)
            {
                stack.Add(name);
            }

            i = p;
        }

        return new XmlDocumentMap(spans);
    }

    private static bool Starts(string text, int index, string value) =>
        string.CompareOrdinal(text, index, value, 0, value.Length) == 0;

    private static int IndexAfter(string text, string value, int from)
    {
        int found = text.IndexOf(value, Math.Min(from, text.Length), StringComparison.Ordinal);
        return found < 0 ? text.Length : found + value.Length;
    }

    /// <summary>True when [start, end) sits inside an MSBuild $(…), @(…) or %(…) reference.</summary>
    public static bool InMsBuildReference(string text, int start, int end)
    {
        for (int i = start - 1; i >= 1; i--)
        {
            char c = text[i];
            if (c == ')' || c == '\n' || c == '<' || c == '>' || c == '"')
            {
                return false;
            }

            if (c == '(' && text[i - 1] is '$' or '@' or '%')
            {
                int close = text.IndexOf(')', i);
                return close >= end;
            }
        }

        return false;
    }
}
