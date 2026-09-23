namespace GigaReplace.Core.Text;

/// <summary>Maps offsets to lines. Handles \n, \r\n and lone \r.</summary>
public sealed class LineIndex
{
    private readonly string _text;
    private readonly List<int> _starts = [0];

    public LineIndex(string text)
    {
        _text = text;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n' || (c == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
            {
                _starts.Add(i + 1);
            }
        }
    }

    public int LineCount => _starts.Count;

    /// <summary>0-based line of an offset.</summary>
    public int LineOf(int offset)
    {
        int index = _starts.BinarySearch(offset);
        return index >= 0 ? index : ~index - 1;
    }

    public int LineStart(int line) => _starts[line];

    /// <summary>The line's text without its line break.</summary>
    public string LineText(int line)
    {
        int start = _starts[line];
        int end = line + 1 < _starts.Count ? _starts[line + 1] : _text.Length;
        while (end > start && (_text[end - 1] == '\n' || _text[end - 1] == '\r'))
        {
            end--;
        }

        return _text[start..end];
    }
}
