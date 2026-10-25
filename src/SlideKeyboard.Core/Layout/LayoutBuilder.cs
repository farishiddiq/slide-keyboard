using SlideKeyboard.Core.Geometry;

namespace SlideKeyboard.Core.Layout;

/// <summary>Turns a <see cref="LayoutSpec"/> into positioned keyboard pages.</summary>
public static class LayoutBuilder
{
    public const double NumberRowHeight = 0.8;

    static readonly string[] DefaultSymbolRows = { "@ # $ _ & - + ( ) /", "* \" ' : ; ! ?" };
    static readonly string[] DefaultMoreSymbolRows = { "~ ` | • √ π ÷ × ¶ ∆", "£ ¥ $ € ¢ ^ ° = { }", "\\ © ® ™ % [ ]" };

    public static KeyboardLayout Build(LayoutSpec spec, KeyboardPage page, bool numberRow) => page switch
    {
        KeyboardPage.Letters => BuildLetters(spec, numberRow),
        KeyboardPage.Symbols => BuildSymbols(spec, more: false),
        _ => BuildSymbols(spec, more: true),
    };

    static KeyboardLayout BuildLetters(LayoutSpec spec, bool numberRow)
    {
        var rows = spec.Rows.Select(LayoutSpec.SplitKeys).Where(r => r.Count > 0).ToList();
        if (rows.Count == 0) throw new InvalidOperationException("Layout has no letter rows.");
        var digits = LayoutSpec.SplitKeys(spec.Digits);

        int last = rows.Count - 1;
        double lastRowFunctions = spec.HasShift ? 3.0 : 1.5;
        double width = Math.Max(rows.Max(r => r.Count), rows[last].Count + lastRowFunctions);

        var keys = new List<KeyDef>();
        double y = 0;

        if (numberRow && digits.Count > 0)
        {
            double w = width / digits.Count;
            for (int i = 0; i < digits.Count; i++)
                keys.Add(CharKey(spec, digits[i], new RectU(i * w, y, w, NumberRowHeight), isLetter: false));
            y += NumberRowHeight;
        }

        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            int n = row.Count;
            double lettersX;

            if (r < last)
            {
                lettersX = (width - n) / 2;
            }
            else if (spec.HasShift)
            {
                double fw = (width - n) / 2;
                keys.Add(FunctionKey(spec.Rtl ? KeyKind.Backspace : KeyKind.Shift, new RectU(0, y, fw, 1)));
                keys.Add(FunctionKey(spec.Rtl ? KeyKind.Shift : KeyKind.Backspace, new RectU(width - fw, y, fw, 1)));
                lettersX = fw;
            }
            else
            {
                double fw = Math.Min(1.5, width - n);
                if (spec.Rtl)
                {
                    keys.Add(FunctionKey(KeyKind.Backspace, new RectU(0, y, fw, 1)));
                    lettersX = fw;
                }
                else
                {
                    keys.Add(FunctionKey(KeyKind.Backspace, new RectU(width - fw, y, fw, 1)));
                    lettersX = width - fw - n;
                }
            }

            for (int i = 0; i < n; i++)
            {
                string? digit = !numberRow && r == 0 && i < digits.Count ? digits[i] : null;
                keys.Add(CharKey(spec, row[i], new RectU(lettersX + i, y, 1, 1), isLetter: IsLetterKey(row[i]), extraAlternate: digit));
            }
            y += 1;
        }

        AddBottomRow(keys, spec, width, y, KeyKind.Symbols, spec.SymbolsLabel);
        y += 1;
        return new KeyboardLayout(KeyboardPage.Letters, keys, width, y, spec.Rtl);
    }

    static KeyboardLayout BuildSymbols(LayoutSpec spec, bool more)
    {
        IReadOnlyList<string> row0, row1, row2;
        if (more)
        {
            var src = spec.MoreSymbolRows is { Count: >= 3 } m ? m : DefaultMoreSymbolRows.ToList();
            row0 = LayoutSpec.SplitKeys(src[0]);
            row1 = LayoutSpec.SplitKeys(src[1]);
            row2 = LayoutSpec.SplitKeys(src[2]);
        }
        else
        {
            var src = spec.SymbolRows is { Count: >= 2 } s ? s : DefaultSymbolRows.ToList();
            row0 = LayoutSpec.SplitKeys(spec.Digits);
            row1 = LayoutSpec.SplitKeys(src[0]);
            row2 = LayoutSpec.SplitKeys(src[1]);
        }

        double width = Math.Max(Math.Max(row0.Count, row1.Count), Math.Max(row2.Count + 3, 10));
        var keys = new List<KeyDef>();

        AddCenteredRow(keys, spec, row0, width, 0);
        AddCenteredRow(keys, spec, row1, width, 1);

        double fw = (width - row2.Count) / 2;
        var pageKind = more ? KeyKind.Symbols : KeyKind.SymbolsMore;
        string pageLabel = more ? spec.SymbolsLabel : "=\\<";
        keys.Add(FunctionKey(spec.Rtl ? KeyKind.Backspace : pageKind, new RectU(0, 2, fw, 1), spec.Rtl ? "" : pageLabel));
        keys.Add(FunctionKey(spec.Rtl ? pageKind : KeyKind.Backspace, new RectU(width - fw, 2, fw, 1), spec.Rtl ? pageLabel : ""));
        for (int i = 0; i < row2.Count; i++)
            keys.Add(CharKey(spec, row2[i], new RectU(fw + i, 2, 1, 1), isLetter: false));

        AddBottomRow(keys, spec, width, 3, KeyKind.Letters, spec.LettersLabel);
        return new KeyboardLayout(more ? KeyboardPage.SymbolsMore : KeyboardPage.Symbols, keys, width, 4, spec.Rtl);
    }

    static void AddCenteredRow(List<KeyDef> keys, LayoutSpec spec, IReadOnlyList<string> row, double width, double y)
    {
        double x = (width - row.Count) / 2;
        for (int i = 0; i < row.Count; i++)
            keys.Add(CharKey(spec, row[i], new RectU(x + i, y, 1, 1), isLetter: false));
    }

    static void AddBottomRow(List<KeyDef> keys, LayoutSpec spec, double width, double y, KeyKind pageKind, string pageLabel)
    {
        double space = width - 6;
        keys.Add(FunctionKey(pageKind, new RectU(0, y, 1.5, 1), pageLabel));
        keys.Add(FunctionKey(KeyKind.Language, new RectU(1.5, y, 1, 1)));
        keys.Add(CharKey(spec, spec.Comma, new RectU(2.5, y, 1, 1), isLetter: false));
        keys.Add(FunctionKey(KeyKind.Space, new RectU(3.5, y, space, 1)));
        keys.Add(CharKey(spec, spec.Period, new RectU(3.5 + space, y, 1, 1), isLetter: false));
        keys.Add(FunctionKey(KeyKind.Enter, new RectU(width - 1.5, y, 1.5, 1)));
    }

    static bool IsLetterKey(string text) => text.Length > 0 && char.IsLetter(text[0]);

    static KeyDef CharKey(LayoutSpec spec, string text, RectU bounds, bool isLetter, string? extraAlternate = null)
    {
        var alternates = new List<string>();
        if (extraAlternate != null) alternates.Add(extraAlternate);
        foreach (var a in spec.AlternatesFor(text))
            if (!alternates.Contains(a) && a != text) alternates.Add(a);

        return new KeyDef
        {
            Kind = KeyKind.Character,
            Text = text,
            Label = text,
            Bounds = bounds,
            Alternates = alternates,
            Hint = extraAlternate,
            IsLetter = isLetter,
        };
    }

    static KeyDef FunctionKey(KeyKind kind, RectU bounds, string label = "") =>
        new() { Kind = kind, Label = label, Bounds = bounds };
}
