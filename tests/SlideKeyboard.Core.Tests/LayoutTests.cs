using SlideKeyboard.Core.Layout;

namespace SlideKeyboard.Core.Tests;

public class LayoutTests
{
    [Fact]
    public void Latin_letters_page_is_qwerty_with_function_keys()
    {
        var layout = LayoutBuilder.Build(Packs.Get("en").Layout, KeyboardPage.Letters, numberRow: false);
        var letters = layout.Keys.Where(k => k.IsLetter).Select(k => k.Text).ToList();
        Assert.Equal("qwertyuiopasdfghjklzxcvbnm", string.Concat(letters));
        Assert.Equal(10, layout.Width);
        Assert.Equal(4, layout.Height);
        foreach (var kind in new[] { KeyKind.Shift, KeyKind.Backspace, KeyKind.Enter, KeyKind.Space, KeyKind.Symbols, KeyKind.Language })
            Assert.NotNull(layout.Find(kind));

        var shift = layout.Find(KeyKind.Shift)!;
        Assert.Equal(0, shift.Bounds.X);
        Assert.Equal("1", layout.Keys.First(k => k.Text == "q").Hint);
    }

    [Fact]
    public void Number_row_adds_digits_above_letters()
    {
        var layout = LayoutBuilder.Build(Packs.Get("en").Layout, KeyboardPage.Letters, numberRow: true);
        Assert.Equal(4 + LayoutBuilder.NumberRowHeight, layout.Height);
        Assert.Contains(layout.Keys, k => k.Text == "7" && k.Bounds.Y == 0);
        Assert.Null(layout.Keys.First(k => k.Text == "q").Hint);
    }

    [Fact]
    public void Arabic_layout_is_rtl_with_backspace_on_the_left()
    {
        var spec = Packs.Get("ar").Layout;
        var layout = LayoutBuilder.Build(spec, KeyboardPage.Letters, numberRow: false);
        Assert.True(layout.Rtl);
        Assert.Null(layout.Find(KeyKind.Shift));
        Assert.Equal(0, layout.Find(KeyKind.Backspace)!.Bounds.X);
        Assert.Equal(12 + 11 + 10, layout.Keys.Count(k => k.IsLetter));
        Assert.Contains(layout.Keys, k => k.Text == "،");
        var alef = layout.Keys.First(k => k.Text == "ا");
        Assert.Contains("أ", alef.Alternates);
    }

    [Fact]
    public void Symbol_pages_switch_back_to_letters()
    {
        var spec = Packs.Get("en").Layout;
        var symbols = LayoutBuilder.Build(spec, KeyboardPage.Symbols, false);
        Assert.NotNull(symbols.Find(KeyKind.Letters));
        Assert.NotNull(symbols.Find(KeyKind.SymbolsMore));
        Assert.Contains(symbols.Keys, k => k.Text == "@");
        var more = LayoutBuilder.Build(spec, KeyboardPage.SymbolsMore, false);
        Assert.NotNull(more.Find(KeyKind.Symbols));
        Assert.Contains(more.Keys, k => k.Text == "€");
    }

    [Fact]
    public void Hit_test_snaps_gaps_to_nearest_key()
    {
        var layout = LayoutBuilder.Build(Packs.Get("en").Layout, KeyboardPage.Letters, false);
        Assert.Equal("q", layout.HitTest(new(0.5, 0.5))!.Text);
        Assert.Equal("a", layout.HitTest(new(0.45, 1.5))!.Text); // left padding of row 2
    }
}
