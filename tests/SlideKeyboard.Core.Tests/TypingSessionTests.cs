using SlideKeyboard.Core.Input;
using SlideKeyboard.Core.Layout;
using SlideKeyboard.Core.Prediction;

namespace SlideKeyboard.Core.Tests;

public class TypingSessionTests
{
    readonly FakeTextField _field = new();
    readonly KeyboardEngine _engine;

    public TypingSessionTests()
    {
        var languages = Packs.Manager("en", "id", "ar");
        languages.SetActive("en");
        _engine = new KeyboardEngine(languages, _field, new EngineSettings { AutoDetectLanguage = false, NumberRow = false });
    }

    void Tap(string text)
    {
        foreach (char c in text)
        {
            if (c == ' ') Press(KeyKind.Space);
            else _engine.PressKey(_engine.Layout.Keys.First(k => k.Kind == KeyKind.Character && k.Text == c.ToString()));
        }
    }

    void Press(KeyKind kind) => _engine.PressKey(_engine.Layout.Find(kind)!);

    void Swipe(string word)
    {
        var (points, times) = GestureSimulator.TimedSwipe(Packs.KeyMap(_engine.Pack), word);
        _engine.CommitSwipeAsync(points, times).GetAwaiter().GetResult();
    }

    [Fact]
    public void Swiped_words_are_separated_by_spaces()
    {
        Swipe("hello");
        Swipe("world");
        Assert.Equal("hello world", _field.Text);
    }

    [Fact]
    public void Punctuation_attaches_to_swiped_word_and_next_word_gets_space()
    {
        Swipe("hello");
        Tap(",");
        Swipe("world");
        Assert.Equal("hello, world", _field.Text);
    }

    [Fact]
    public void Sentence_end_capitalizes_next_swiped_word()
    {
        Swipe("thanks");
        Tap(".");
        Swipe("see");
        Assert.Equal("thanks. See", _field.Text);
    }

    [Fact]
    public void Backspace_after_swipe_deletes_the_whole_word()
    {
        Swipe("hello");
        Swipe("world");
        Press(KeyKind.Backspace);
        Assert.Equal("hello", _field.Text);
        Swipe("there");
        Assert.Equal("hello there", _field.Text);
    }

    [Fact]
    public void Tapped_typo_is_autocorrected_on_space_and_backspace_undoes_it()
    {
        Tap("teh ");
        Assert.Equal("the ", _field.Text);
        Press(KeyKind.Backspace);
        Assert.Equal("teh", _field.Text);
        Tap(" ");
        Assert.Equal("teh ", _field.Text);
    }

    [Fact]
    public void Lowercase_i_becomes_capital()
    {
        Tap("i ");
        Assert.Equal("I ", _field.Text);
    }

    [Fact]
    public void Suggestions_complete_the_current_word_and_replace_it()
    {
        Tap("go");
        var suggestions = _engine.Suggestions;
        Assert.Contains(suggestions, s => s.Text == "going" || s.Text == "good");
        var going = _engine.Predictions.Complete(_engine.Pack, "goi", null, 3).First();
        _engine.PickSuggestion(new Suggestion(going.Entry.Word, SuggestionKind.Completion, true));
        Assert.Equal("going", _field.Text);
        Swipe("home");
        Assert.Equal("going home", _field.Text);
    }

    [Fact]
    public void Picking_a_swipe_alternative_replaces_the_swiped_word()
    {
        Swipe("good");
        var alternative = _engine.Suggestions.First(s => !s.IsPrimary);
        _engine.PickSuggestion(alternative);
        Assert.Equal(alternative.Text, _field.Text);
    }

    [Fact]
    public void Double_space_inserts_period()
    {
        Swipe("hello");
        Tap("  ");
        Assert.Equal("hello. ", _field.Text);
    }

    [Fact]
    public void Arabic_swipe_produces_arabic_text()
    {
        _engine.SetLanguage("ar");
        Swipe("سلام");
        Swipe("عليكم");
        Assert.Equal("سلام عليكم", _field.Text);
    }

    [Fact]
    public void Indonesian_swipe_produces_selamat()
    {
        _engine.SetLanguage("id");
        Swipe("selamat");
        Swipe("pagi");
        Assert.Equal("selamat pagi", _field.Text);
    }

    [Fact]
    public void Kept_unknown_word_can_be_saved_and_then_swiped()
    {
        _engine.SetLanguage("id");
        Assert.Null(_engine.Pack.Words.Lookup("swipekibo"));
        Tap("swipekibo");
        var literal = _engine.Suggestions.First(s => s.Kind == SuggestionKind.Literal);
        _engine.PickSuggestion(literal);

        var save = Assert.Single(_engine.Suggestions);
        Assert.Equal(SuggestionKind.AddToDictionary, save.Kind);
        _engine.PickSuggestion(save);

        Assert.Contains("swipekibo", _engine.UserWords.Words("id"));
        Assert.NotNull(_engine.Pack.Words.Lookup("swipekibo"));
        Swipe("swipekibo");
        Assert.Equal("swipekibo swipekibo", _field.Text);

        Assert.True(_engine.RemoveUserWord("swipekibo", "id"));
        Assert.Null(_engine.Pack.Words.Lookup("swipekibo"));
    }

    [Fact]
    public void Language_cycles_in_enabled_order()
    {
        var languages = Packs.Manager("id", "en", "ar");
        languages.SetActive("id");
        languages.CycleNext();
        Assert.Equal("en", languages.Active.Code);
        languages.CycleNext();
        Assert.Equal("ar", languages.Active.Code);
        languages.CycleNext();
        Assert.Equal("id", languages.Active.Code);
    }

    [Fact]
    public void Shift_capitalizes_one_letter_and_double_tap_locks()
    {
        Press(KeyKind.Shift);
        Tap("ab");
        Assert.Equal("Ab", _field.Text);
        Press(KeyKind.Shift);
        Press(KeyKind.Shift);
        Assert.Equal(ShiftState.Locked, _engine.Shift);
        Tap("cd");
        Assert.Equal("AbCD", _field.Text);
    }

    [Fact]
    public void Auto_detect_switches_to_indonesian_after_indonesian_words()
    {
        var languages = Packs.Manager("en", "id");
        languages.SetActive("en");
        var field = new FakeTextField();
        var engine = new KeyboardEngine(languages, field, new EngineSettings { AutoDetectLanguage = true, NumberRow = false });
        var map = Packs.KeyMap(Packs.Get("en"));
        foreach (var word in new[] { "selamat", "pagi" })
        {
            var (points, times) = GestureSimulator.TimedSwipe(map, word);
            engine.CommitSwipeAsync(points, times).GetAwaiter().GetResult();
        }
        SpinWait.SpinUntil(() => languages.Active.Code == "id", 2000);
        Assert.Equal("selamat pagi", field.Text);
        Assert.Equal("id", languages.Active.Code);
    }
}
