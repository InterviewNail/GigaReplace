using System.Text;

namespace GigaReplace.Core.Text;

public readonly record struct TextEdit(int Start, int Length, string Replacement);

public static class TextEdits
{
    /// <summary>Applies non-overlapping edits; an edit overlapping an earlier one is ignored.</summary>
    public static string Apply(string text, IEnumerable<TextEdit> edits)
    {
        var ordered = edits.OrderBy(e => e.Start).ToList();
        if (ordered.Count == 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length + 64);
        int pos = 0;
        foreach (var edit in ordered)
        {
            if (edit.Start < pos || edit.Start + edit.Length > text.Length)
            {
                continue;
            }

            sb.Append(text, pos, edit.Start - pos).Append(edit.Replacement);
            pos = edit.Start + edit.Length;
        }

        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }

    /// <summary>The text after applying the final decisions of <paramref name="items"/> (offsets relative to <paramref name="baseOffset"/>).</summary>
    public static string ApplyDecisions(string text, IEnumerable<ChangeItem> items, int baseOffset = 0) =>
        Apply(text, items.Where(i => i.WillChange).Select(i => new TextEdit(i.Offset - baseOffset, i.Length, i.FinalText)));
}
