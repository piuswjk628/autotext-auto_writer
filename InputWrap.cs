using System.Text;

namespace AutoInput;

static class InputWrap
{
    internal static string Next(Rune rune, ref int column, int width)
    {
        if (rune.Value == '\n') { column = 0; return "\n"; }
        bool insert = width > 0 && column >= width;
        if (insert) column = 0;
        column++;
        return (insert ? "\n" : "") + rune.ToString();
    }

    internal static int ColumnAt(string prefix, int width)
    {
        int column = 0;
        foreach (var rune in prefix.EnumerateRunes()) Next(rune, ref column, width);
        return column;
    }
}
