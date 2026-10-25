using System.Globalization;

namespace SlideKeyboard.Core.Layout;

/// <summary>
/// Language-pack layout description (layout.json). Rows list the letter keys in visual left-to-right order,
/// separated by spaces. Function keys are added by <see cref="LayoutBuilder"/>.
/// </summary>
public sealed class LayoutSpec
{
    public List<string> Rows { get; set; } = new();
    public bool Rtl { get; set; }
    public bool HasShift { get; set; } = true;
    public string Comma { get; set; } = ",";
    public string Period { get; set; } = ".";
    public string Digits { get; set; } = "1 2 3 4 5 6 7 8 9 0";
    public string SymbolsLabel { get; set; } = "?123";
    public string LettersLabel { get; set; } = "ABC";

    /// <summary>Second and third row of the first symbols page.</summary>
    public List<string>? SymbolRows { get; set; }

    /// <summary>The three rows of the second symbols page.</summary>
    public List<string>? MoreSymbolRows { get; set; }

    /// <summary>Long-press alternates per key, space separated.</summary>
    public Dictionary<string, string> LongPress { get; set; } = new();

    /// <summary>Maps characters that have no key of their own to the key a swipe should pass over (e.g. "أ" to "ا").</summary>
    public Dictionary<string, string> Aliases { get; set; } = new();

    public static IReadOnlyList<string> SplitKeys(string row)
    {
        if (row.Contains(' ')) return row.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var keys = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(row);
        while (e.MoveNext()) keys.Add(e.GetTextElement());
        return keys;
    }

    public IReadOnlyList<string> AlternatesFor(string key) =>
        LongPress.TryGetValue(key, out var alternates) ? SplitKeys(alternates) : Array.Empty<string>();

    public string LetterSignature => string.Join("|", Rows);

    public static LayoutSpec DefaultLatin() => new()
    {
        Rows = new() { "q w e r t y u i o p", "a s d f g h j k l", "z x c v b n m" },
    };
}
