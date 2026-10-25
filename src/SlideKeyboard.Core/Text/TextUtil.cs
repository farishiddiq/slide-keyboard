using System.Globalization;
using System.Text;

namespace SlideKeyboard.Core.Text;

public static class TextUtil
{
    /// <summary>
    /// Lookup key used for dictionary matching: lower-cased, with diacritics, Arabic tashkeel/hamza marks
    /// and tatweel removed. "Café" -> "cafe", "إسلام" -> "اسلام".
    /// </summary>
    public static string NormalizeKey(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        string decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (c == 'ـ') continue; // tatweel
            if (c == '’') { sb.Append('\''); continue; }
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
                continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>True for characters that belong inside a word (letters, digits, apostrophes, combining marks).</summary>
    public static bool IsWordChar(char c)
    {
        if (char.IsLetterOrDigit(c) || c == '\'' || c == '’' || c == 'ـ') return true;
        var category = CharUnicodeInfo.GetUnicodeCategory(c);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
    }

    public static int TextElementCount(string text) => string.IsNullOrEmpty(text) ? 0 : new StringInfo(text).LengthInTextElements;

    public static IReadOnlyList<string> TextElements(string text)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) list.Add(e.GetTextElement());
        return list;
    }

    /// <summary>Removes the last <paramref name="count"/> text elements (graphemes) from the builder.</summary>
    public static void RemoveLastTextElements(StringBuilder sb, int count)
    {
        if (count <= 0 || sb.Length == 0) return;
        int[] starts = StringInfo.ParseCombiningCharacters(sb.ToString());
        int keep = starts.Length - count;
        if (keep <= 0) sb.Clear();
        else sb.Length = starts[keep];
    }

    public static bool HasUpper(string text)
    {
        foreach (char c in text)
            if (char.IsUpper(c)) return true;
        return false;
    }

    public static bool IsAllUpper(string text)
    {
        bool any = false;
        foreach (char c in text)
        {
            if (char.IsLower(c)) return false;
            if (char.IsUpper(c)) any = true;
        }
        return any;
    }

    public static string CapitalizeFirst(string text, CultureInfo culture) =>
        text.Length == 0 ? text : char.ToUpper(text[0], culture) + text[1..];

    /// <summary>Applies the casing pattern of what the user typed to a dictionary word.</summary>
    public static string MatchCase(string word, string typed, CultureInfo culture)
    {
        if (typed.Length == 0 || word.Length == 0) return word;
        if (TextElementCount(typed) > 1 && IsAllUpper(typed)) return word.ToUpper(culture);
        if (char.IsUpper(typed[0])) return CapitalizeFirst(word, culture);
        return word;
    }
}
