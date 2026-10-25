using System.Text;
using SlideKeyboard.Core.Dictionaries;
using SlideKeyboard.Core.Prediction;
using SlideKeyboard.Core.Swipe;
using SlideKeyboard.Core.Text;

namespace SlideKeyboard.Core.Input;

public enum LastAction
{
    None,
    Character,
    Space,
    Enter,
    Backspace,
    Swipe,
    Pick,
    AutoCorrect,
    UndoCorrection,
}

public sealed record CommittedWord(string Word, bool FromSwipe);

/// <summary>
/// Turns key presses, swipes and suggestion picks into text edits: spacing around swiped words, auto-correction
/// (undoable with backspace), replacing the current word with a suggestion, and the suggestion list itself.
///
/// The session cannot read the target application's text, so it remembers only what it typed itself: a short
/// in-memory tail (at most <see cref="TailCapacity"/> characters) that is discarded whenever focus changes.
/// Nothing is ever written to disk.
/// </summary>
public sealed class TypingSession
{
    public const int TailCapacity = 96;

    /// <summary>Punctuation that attaches to the preceding word (no space before it).</summary>
    const string ClosingPunctuation = ".,!?;:)]}»…%،؛؟";
    const string SentenceEnd = ".!?…؟";
    const string OpeningCharacters = "([{«\"'“‘";

    readonly ITextOutput _output;
    readonly PredictionEngine _predictions;
    readonly Func<LanguagePack> _pack;
    readonly EngineSettings _settings;
    readonly StringBuilder _tail = new();
    readonly HashSet<string> _noCorrect = new(StringComparer.Ordinal);

    bool _pendingSpace;
    bool _swipeHadLeadingSpace;
    string? _lastSwipeWord;
    IReadOnlyList<SwipeCandidate> _swipeCandidates = Array.Empty<SwipeCandidate>();
    (string Original, string Corrected, string Trailer)? _undo;
    IReadOnlyList<Suggestion> _suggestions = Array.Empty<Suggestion>();
    string? _offerToSave;

    public TypingSession(ITextOutput output, PredictionEngine predictions, Func<LanguagePack> pack, EngineSettings settings)
    {
        _output = output;
        _predictions = predictions;
        _pack = pack;
        _settings = settings;
    }

    public LastAction LastAction { get; private set; }
    public IReadOnlyList<Suggestion> Suggestions => _suggestions;

    /// <summary>A space will be inserted before the next word (after a swiped or picked word).</summary>
    public bool PendingSpace => _pendingSpace;

    public event EventHandler? SuggestionsChanged;
    public event EventHandler<CommittedWord>? WordCommitted;

    /// <summary>The user tapped 'save' on a word the dictionary does not know.</summary>
    public event EventHandler<string>? SaveWordRequested;

    /// <summary>The word currently being typed with taps (empty after a swipe, space or punctuation).</summary>
    public string ComposingWord => _pendingSpace ? "" : TrailingWord();

    /// <summary>The word the next word will follow, for context-aware prediction.</summary>
    public string? ContextWord => ComposingWord is { Length: > 0 } composing ? composing : PreviousWord();

    public bool AtSentenceStart
    {
        get
        {
            if (_tail.Length == 0) return false;
            int i = _tail.Length - 1;
            if (_tail[i] == '\n') return true;
            if (!_pendingSpace)
            {
                if (_tail[i] != ' ') return false;
                i--;
            }
            return i >= 0 && SentenceEnd.Contains(_tail[i]);
        }
    }

    LanguagePack Pack => _pack();

    // ---------------------------------------------------------------- input

    public void TypeText(string text)
    {
        if (text.Length == 0) return;
        _undo = null;
        _offerToSave = null;

        if (text.Length == 1 && ClosingPunctuation.Contains(text[0]))
        {
            string word = ComposingWord;
            if (word.Length > 0 && TryAutoCorrect(word, text))
            {
                Refresh();
                return;
            }
            bool keepPendingSpace = _pendingSpace;
            _pendingSpace = false;
            Emit(text);
            _pendingSpace = keepPendingSpace && text[0] is not (')' or ']' or '}' or '»' or '%');
            if (word.Length > 0) OnWordCommitted(word, fromSwipe: false);
        }
        else
        {
            FlushPendingSpace();
            Emit(text);
        }
        LastAction = LastAction.Character;
        Refresh();
    }

    public void Space()
    {
        _offerToSave = null;
        string word = ComposingWord;
        if (word.Length > 0 && TryAutoCorrect(word, " "))
        {
            Refresh();
            return;
        }

        _undo = null;
        if (_settings.DoubleSpacePeriod && LastAction == LastAction.Space && EndsWithWordThenSpace())
        {
            DeleteBack(1);
            Emit(". ");
            _pendingSpace = false;
            LastAction = LastAction.Character;
            Refresh();
            return;
        }

        _pendingSpace = false;
        Emit(" ");
        if (word.Length > 0) OnWordCommitted(word, fromSwipe: false);
        LastAction = LastAction.Space;
        Refresh();
    }

    public void Enter()
    {
        _offerToSave = null;
        string word = ComposingWord;
        _undo = null;
        _pendingSpace = false;
        _output.SendKey(SpecialKey.Enter);
        Append("\n");
        if (word.Length > 0) OnWordCommitted(word, fromSwipe: false);
        LastAction = LastAction.Enter;
        Refresh();
    }

    public void Backspace()
    {
        _offerToSave = null;
        if (LastAction == LastAction.AutoCorrect && _undo is { } undo)
        {
            // Revert the auto-correction: "teh " + backspace -> "teh"
            DeleteBack(TextUtil.TextElementCount(undo.Corrected) + TextUtil.TextElementCount(undo.Trailer));
            Emit(undo.Original);
            _noCorrect.Add(undo.Original);
            _undo = null;
            LastAction = LastAction.UndoCorrection;
            Refresh();
            return;
        }
        _undo = null;

        if (LastAction == LastAction.Swipe && _lastSwipeWord is { } swiped)
        {
            // A swiped word is deleted as a whole, together with the space inserted before it.
            DeleteBack(TextUtil.TextElementCount(swiped) + (_swipeHadLeadingSpace ? 1 : 0));
            _pendingSpace = _swipeHadLeadingSpace;
            _lastSwipeWord = null;
            LastAction = LastAction.Backspace;
            Refresh();
            return;
        }

        _pendingSpace = false;
        DeleteBack(1);
        LastAction = LastAction.Backspace;
        Refresh();
    }

    /// <summary>Inserts the best swipe candidate, adding a space before it when it follows other text.</summary>
    public void CommitSwipe(IReadOnlyList<SwipeCandidate> candidates, Func<string, string> applyCase)
    {
        if (candidates.Count == 0) return;
        _undo = null;
        _offerToSave = null;

        string composing = ComposingWord;
        string word = applyCase(candidates[0].Word);
        bool leadingSpace = _pendingSpace ||
                            (_tail.Length > 0 && !char.IsWhiteSpace(_tail[^1]) && !OpeningCharacters.Contains(_tail[^1]));
        _pendingSpace = false;
        if (composing.Length > 0) OnWordCommitted(composing, fromSwipe: false);
        if (leadingSpace) Emit(" ");
        Emit(word);

        _swipeHadLeadingSpace = leadingSpace;
        _lastSwipeWord = word;
        _swipeCandidates = candidates;
        _pendingSpace = true;
        LastAction = LastAction.Swipe;
        OnWordCommitted(word, fromSwipe: true);
        Refresh();
    }

    public void PickSuggestion(Suggestion suggestion)
    {
        _undo = null;
        if (suggestion.Kind == SuggestionKind.AddToDictionary)
        {
            _offerToSave = null;
            SaveWordRequested?.Invoke(this, suggestion.Text);
            Refresh();
            return;
        }
        _offerToSave = null;
        bool replacingSwipe = LastAction == LastAction.Swipe && suggestion.Kind == SuggestionKind.SwipeAlternative;
        string replaced = suggestion.Kind == SuggestionKind.NextWord ? "" : TrailingWord();
        string text = suggestion.Text;

        if (suggestion.Kind == SuggestionKind.Literal)
        {
            _noCorrect.Add(replaced);
            text = replaced;
            // Keeping a word the dictionary doesn't know: offer to save it (like "touch again to save").
            if (Pack.Words.Lookup(replaced) == null && Dictionaries.UserDictionary.IsValidWord(replaced))
                _offerToSave = replaced;
        }

        if (replaced.Length > 0)
        {
            if (text != replaced)
            {
                DeleteBack(TextUtil.TextElementCount(replaced));
                Emit(text);
            }
        }
        else
        {
            if (_pendingSpace || (_tail.Length > 0 && !char.IsWhiteSpace(_tail[^1]) && !OpeningCharacters.Contains(_tail[^1])))
                Emit(" ");
            Emit(text);
        }
        _pendingSpace = true;

        if (replacingSwipe)
        {
            _lastSwipeWord = text;
        }
        else
        {
            LastAction = LastAction.Pick;
            _lastSwipeWord = null;
        }
        OnWordCommitted(text, fromSwipe: replacingSwipe);
        Refresh();
    }

    /// <summary>Forget everything about the text around the caret (focus moved, caret moved, etc.).</summary>
    public void ResetContext()
    {
        _tail.Clear();
        _offerToSave = null;
        _pendingSpace = false;
        _swipeHadLeadingSpace = false;
        _lastSwipeWord = null;
        _swipeCandidates = Array.Empty<SwipeCandidate>();
        _undo = null;
        LastAction = LastAction.None;
        Refresh();
    }

    // ---------------------------------------------------------------- suggestions

    public void Refresh()
    {
        _suggestions = _settings.Suggestions ? BuildSuggestions() : Array.Empty<Suggestion>();
        SuggestionsChanged?.Invoke(this, EventArgs.Empty);
    }

    IReadOnlyList<Suggestion> BuildSuggestions()
    {
        int max = Math.Clamp(_settings.SuggestionCount, 1, 5);
        var pack = Pack;
        var list = new List<Suggestion>(max);
        if (_offerToSave != null)
        {
            list.Add(new Suggestion(_offerToSave, SuggestionKind.AddToDictionary, true));
            return list;
        }

        void Add(string text, SuggestionKind kind)
        {
            if (list.Count >= max) return;
            if (list.Any(s => string.Equals(s.Text, text, StringComparison.Ordinal))) return;
            list.Add(new Suggestion(text, kind, list.Count == 0));
        }

        if (LastAction == LastAction.Swipe && _lastSwipeWord != null)
        {
            Add(_lastSwipeWord, SuggestionKind.SwipeAlternative);
            foreach (var candidate in _swipeCandidates)
                Add(TextUtil.MatchCase(candidate.Word, _lastSwipeWord, pack.Culture), SuggestionKind.SwipeAlternative);
            return list;
        }

        string word = ComposingWord;
        string? previous = PreviousWord();
        if (word.Length > 0)
        {
            var correction = _settings.AutoCorrect && !_noCorrect.Contains(word) ? _predictions.Correct(pack, word, previous) : null;
            var completions = _predictions.Complete(pack, word, previous, max + 1);
            var exact = pack.Words.Lookup(word);

            if (correction != null)
            {
                Add(TextUtil.MatchCase(correction.Entry.Word, word, pack.Culture), SuggestionKind.Correction);
                Add(word, SuggestionKind.Literal);
            }
            else if (exact != null)
            {
                Add(word, SuggestionKind.Completion);
            }
            else if (completions.Count == 0)
            {
                Add(word, SuggestionKind.Literal);
            }

            foreach (var completion in completions)
                Add(TextUtil.MatchCase(completion.Entry.Word, word, pack.Culture), SuggestionKind.Completion);
            if (exact == null) Add(word, SuggestionKind.Literal);
            return list;
        }

        if (LastAction == LastAction.None && _tail.Length == 0) return list;
        foreach (var prediction in _predictions.Next(pack, ContextWord, max))
        {
            string text = AtSentenceStart && pack.HasCase ? TextUtil.CapitalizeFirst(prediction.Entry.Word, pack.Culture) : prediction.Entry.Word;
            Add(text, SuggestionKind.NextWord);
        }
        return list;
    }

    // ---------------------------------------------------------------- helpers

    bool TryAutoCorrect(string word, string trailer)
    {
        if (!_settings.AutoCorrect || _noCorrect.Contains(word)) return false;
        if (LastAction is LastAction.Swipe or LastAction.Pick or LastAction.UndoCorrection) return false;

        var pack = Pack;
        var fix = _predictions.Correct(pack, word, PreviousWord());
        if (fix == null) return false;
        string corrected = TextUtil.MatchCase(fix.Entry.Word, word, pack.Culture);
        if (corrected == word) return false;

        DeleteBack(TextUtil.TextElementCount(word));
        Emit(corrected);
        _pendingSpace = false;
        Emit(trailer);
        _undo = (word, corrected, trailer);
        LastAction = LastAction.AutoCorrect;
        OnWordCommitted(corrected, fromSwipe: false);
        return true;
    }

    void OnWordCommitted(string word, bool fromSwipe)
    {
        _predictions.NoteUsed(Pack, word);
        WordCommitted?.Invoke(this, new CommittedWord(word, fromSwipe));
    }

    string TrailingWord()
    {
        int i = _tail.Length;
        while (i > 0 && TextUtil.IsWordChar(_tail[i - 1])) i--;
        while (i < _tail.Length && _tail[i] is '\'' or '’') i++;
        return _tail.ToString(i, _tail.Length - i);
    }

    string? PreviousWord()
    {
        string text = _tail.ToString();
        int end = text.Length;
        if (!_pendingSpace)
            while (end > 0 && TextUtil.IsWordChar(text[end - 1])) end--;

        int i = end;
        while (i > 0 && !TextUtil.IsWordChar(text[i - 1]))
        {
            if (SentenceEnd.Contains(text[i - 1]) || text[i - 1] == '\n') return null;
            i--;
        }
        int start = i;
        while (start > 0 && TextUtil.IsWordChar(text[start - 1])) start--;
        return start < i ? text[start..i] : null;
    }

    bool EndsWithWordThenSpace() =>
        _tail.Length >= 2 && _tail[^1] == ' ' && TextUtil.IsWordChar(_tail[^2]);

    void FlushPendingSpace()
    {
        if (!_pendingSpace) return;
        _pendingSpace = false;
        Emit(" ");
    }

    void Emit(string text)
    {
        if (text.Length == 0) return;
        _output.SendText(text);
        Append(text);
    }

    void Append(string text)
    {
        _tail.Append(text);
        if (_tail.Length > TailCapacity) _tail.Remove(0, _tail.Length - TailCapacity);
    }

    void DeleteBack(int count)
    {
        if (count <= 0) return;
        _output.SendBackspace(count);
        TextUtil.RemoveLastTextElements(_tail, count);
    }
}
