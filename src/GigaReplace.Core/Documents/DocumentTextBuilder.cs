using System.Text;

namespace GigaReplace.Core.Documents;

internal enum DocumentCharKind : byte
{
    /// <summary>Headings and labels the text view adds; never matched.</summary>
    Label,

    /// <summary>Document text that can be mapped back to where it's stored.</summary>
    Text,

    /// <summary>A tab or line break inside a paragraph.</summary>
    Gap,

    /// <summary>Part of a document property value.</summary>
    Property,

    /// <summary>Text that can be found but not mapped back (report only).</summary>
    Fixed,
}

/// <summary>
/// Builds the searchable text of a document. Each character records its kind, its owner (the
/// piece of the file it came from, such as one Word text element) and its offset in that owner.
/// </summary>
internal sealed class DocumentTextBuilder
{
    private readonly StringBuilder _sb = new();
    private readonly List<DocumentCharKind> _kinds = [];
    private readonly List<int> _owners = [];
    private readonly List<int> _offsets = [];
    private readonly List<(int Start, string Place, int Page)> _sections = [];
    private string? _text;

    public string Text => _text ??= _sb.ToString();

    public int Length => _sb.Length;

    public void Section(string place, int page) => _sections.Add((_sb.Length, place, page));

    /// <summary>Removes the last section if nothing but its heading was added.</summary>
    public void DropEmptySection()
    {
        if (_sections.Count == 0)
        {
            return;
        }

        int start = _sections[^1].Start;
        if (Enumerable.Range(start, _sb.Length - start).All(i => _kinds[i] == DocumentCharKind.Label))
        {
            _sb.Length = start;
            _kinds.RemoveRange(start, _kinds.Count - start);
            _owners.RemoveRange(start, _owners.Count - start);
            _offsets.RemoveRange(start, _offsets.Count - start);
            _sections.RemoveAt(_sections.Count - 1);
            _text = null;
        }
    }

    public void Label(string text) => Add(text, DocumentCharKind.Label, -1);

    /// <summary>Adds text from one owner; character k gets offset <paramref name="firstOffset"/> + k.</summary>
    public void Add(string text, DocumentCharKind kind, int owner, int firstOffset = 0)
    {
        for (int k = 0; k < text.Length; k++)
        {
            char c = text[k];
            if (kind is DocumentCharKind.Text or DocumentCharKind.Property && c is '\n' or '\r' or '\t' or '\v' or '\f')
            {
                c = ' ';
            }

            _sb.Append(c);
            _kinds.Add(kind);
            _owners.Add(owner);
            _offsets.Add(firstOffset + k);
        }

        _text = null;
    }

    public DocumentCharKind KindAt(int index) => _kinds[index];

    public int OwnerAt(int index) => _owners[index];

    public int OffsetAt(int index) => _offsets[index];

    public bool IsSearchable(int start, int length)
    {
        for (int i = start; i < start + length; i++)
        {
            if (_kinds[i] == DocumentCharKind.Label)
            {
                return false;
            }
        }

        return length > 0;
    }

    public bool AllOfKind(int start, int length, DocumentCharKind kind)
    {
        for (int i = start; i < start + length; i++)
        {
            if (_kinds[i] != kind)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The match split by owner: (owner, offset in owner, count), in order.</summary>
    public IEnumerable<(int Owner, int Offset, int Count)> Pieces(int start, int length)
    {
        int i = start, end = start + length;
        while (i < end)
        {
            int owner = _owners[i], offset = _offsets[i], count = 1;
            while (i + count < end && _owners[i + count] == owner && _offsets[i + count] == offset + count)
            {
                count++;
            }

            yield return (owner, offset, count);
            i += count;
        }
    }

    public (string Place, int Page) SectionAt(int offset)
    {
        var found = _sections.LastOrDefault(s => s.Start <= offset);
        return found.Place is null ? ("", 0) : (found.Place, found.Page);
    }
}
