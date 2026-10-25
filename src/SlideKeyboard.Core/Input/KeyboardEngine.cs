using System.Collections.Concurrent;
using SlideKeyboard.Core.Dictionaries;
using SlideKeyboard.Core.Geometry;
using SlideKeyboard.Core.Layout;
using SlideKeyboard.Core.Prediction;
using SlideKeyboard.Core.Swipe;
using SlideKeyboard.Core.Text;

namespace SlideKeyboard.Core.Input;

public enum ShiftState
{
    Off,
    Once,
    Locked,
}

/// <summary>
/// UI-independent keyboard brain: owns the page/shift state, routes key presses and swipes into the
/// <see cref="TypingSession"/>, and runs swipe recognition off the UI thread.
///
/// All public methods must be called on the thread that created the engine (the UI thread). Swipe recognition runs
/// on the thread pool; its result is applied back on the creating thread, and key presses that arrive while a swipe
/// is still being recognized are queued behind it so text is always produced in input order.
/// </summary>
public sealed class KeyboardEngine
{
    const double SecondaryLanguageBias = -2.0;
    const int DoubleTapMs = 350;

    readonly ITextOutput _output;
    readonly SwipeRecognizer _recognizer;
    readonly TaskScheduler _scheduler;
    readonly object _layoutLock = new();
    readonly Dictionary<(string, KeyboardPage, bool), KeyboardLayout> _layouts = new();
    readonly ConcurrentDictionary<(string, bool), Lazy<SwipeIndex>> _indexes = new();
    readonly ConcurrentDictionary<string, SwipeKeyMap> _correctionMaps = new();
    readonly LanguageDetector _detector = new();
    readonly Dictionary<string, WordList> _baseWords = new(StringComparer.OrdinalIgnoreCase);

    Task _queue = Task.CompletedTask;
    CancellationTokenSource? _previewCts;
    long _lastShiftTap;
    bool _autoShift;

    public KeyboardEngine(LanguageManager languages, ITextOutput output, EngineSettings? settings = null, SwipeOptions? swipeOptions = null,
        UserDictionary? userWords = null)
    {
        Languages = languages;
        UserWords = userWords ?? new UserDictionary(null);
        foreach (var pack in languages.All)
        {
            _baseWords[pack.Code] = pack.Words;
            ApplyUserWords(pack);
        }
        _output = output;
        Settings = settings ?? new EngineSettings();
        _recognizer = new SwipeRecognizer(swipeOptions);
        _scheduler = SynchronizationContext.Current != null
            ? TaskScheduler.FromCurrentSynchronizationContext()
            : TaskScheduler.Default;

        Predictions = new PredictionEngine(CorrectionMap);
        Session = new TypingSession(output, Predictions, () => Languages.Active, Settings);
        Session.SuggestionsChanged += (_, _) => SuggestionsChanged?.Invoke(this, EventArgs.Empty);
        Session.WordCommitted += OnWordCommitted;
        Session.SaveWordRequested += (_, word) => AddUserWord(word);
        Languages.ActiveChanged += (_, _) =>
        {
            Page = KeyboardPage.Letters;
            Shift = ShiftState.Off;
            _autoShift = false;
            _detector.Reset();
            Session.Refresh();
            AfterInput(consumedShift: false);
            RaiseStateChanged();
            WarmUp();
        };
    }

    public LanguageManager Languages { get; }
    public EngineSettings Settings { get; }
    public PredictionEngine Predictions { get; }
    public TypingSession Session { get; }
    public SwipeRecognizer Recognizer => _recognizer;

    /// <summary>The user's saved words (personal dictionary).</summary>
    public UserDictionary UserWords { get; }

    public KeyboardPage Page { get; private set; }
    public ShiftState Shift { get; private set; }
    public LanguagePack Pack => Languages.Active;
    public KeyboardLayout Layout => GetLayout(Pack, Page, Settings.NumberRow);

    /// <summary>Height (in rows) of the letters page, used to size the keyboard consistently across pages.</summary>
    public double LettersHeight => GetLayout(Pack, KeyboardPage.Letters, Settings.NumberRow).Height;

    public IReadOnlyList<Suggestion> Suggestions => Session.Suggestions;

    /// <summary>Layout, page, shift or language changed: the keys need to be redrawn.</summary>
    public event EventHandler? StateChanged;

    public event EventHandler? SuggestionsChanged;

    // ---------------------------------------------------------------- layouts and indexes

    public KeyboardLayout GetLayout(LanguagePack pack, KeyboardPage page, bool numberRow)
    {
        lock (_layoutLock)
        {
            var key = (pack.Code, page, numberRow);
            if (!_layouts.TryGetValue(key, out var layout))
                _layouts[key] = layout = LayoutBuilder.Build(pack.Layout, page, numberRow);
            return layout;
        }
    }

    public SwipeIndex GetSwipeIndex(LanguagePack pack, bool numberRow) =>
        _indexes.GetOrAdd((pack.Code, numberRow), _ => new Lazy<SwipeIndex>(
            () => SwipeIndex.Build(pack, new SwipeKeyMap(GetLayout(pack, KeyboardPage.Letters, numberRow), pack.Layout)),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    SwipeKeyMap CorrectionMap(LanguagePack pack) =>
        _correctionMaps.GetOrAdd(pack.Code, _ => new SwipeKeyMap(GetLayout(pack, KeyboardPage.Letters, false), pack.Layout));

    /// <summary>Builds swipe indexes for the enabled languages in the background.</summary>
    public Task WarmUp()
    {
        var packs = Languages.Enabled.ToList();
        bool numberRow = Settings.NumberRow;
        return Task.Run(() =>
        {
            foreach (var pack in packs)
            {
                GetSwipeIndex(pack, numberRow);
                CorrectionMap(pack);
            }
        });
    }

    // ---------------------------------------------------------------- personal dictionary

    /// <summary>Saves a word to the personal dictionary of <paramref name="code"/> (default: active language).</summary>
    public bool AddUserWord(string word, string? code = null)
    {
        var pack = Languages.Find(code) ?? Pack;
        if (!UserWords.Add(pack.Code, word)) return false;
        ApplyUserWords(pack);
        Session.Refresh();
        WarmUp();
        return true;
    }

    public bool RemoveUserWord(string word, string code)
    {
        var pack = Languages.Find(code);
        if (pack == null || !UserWords.Remove(pack.Code, word)) return false;
        ApplyUserWords(pack);
        Session.Refresh();
        WarmUp();
        return true;
    }

    /// <summary>Merges the personal words into the pack and drops its swipe indexes so they get rebuilt.</summary>
    void ApplyUserWords(LanguagePack pack)
    {
        var baseList = _baseWords[pack.Code];
        var words = UserWords.Words(pack.Code);
        // Saved words rank like a fairly common word, so they are offered readily but don't override everyday words.
        double prior = baseList.Count > 1500 ? baseList.Entries[1500].LogPrior : -5;
        pack.Words = words.Count == 0 ? baseList : baseList.WithAdded(words, prior);
        foreach (var key in _indexes.Keys.Where(k => string.Equals(k.Item1, pack.Code, StringComparison.OrdinalIgnoreCase)).ToList())
            _indexes.TryRemove(key, out _);
    }

    /// <summary>Call after changing <see cref="Settings"/>: refreshes suggestions and layout.</summary>
    public void SettingsChanged()
    {
        Session.Refresh();
        RaiseStateChanged();
        WarmUp();
    }

    // ---------------------------------------------------------------- input

    public void PressKey(KeyDef key, string? alternate = null) => Run(() => HandleKey(key, alternate));

    public void PickSuggestion(Suggestion suggestion) => Run(() =>
    {
        Session.PickSuggestion(suggestion);
        AfterInput(consumedShift: true);
    });

    public void MoveCursor(int direction) => Run(() =>
    {
        _output.SendKey(direction < 0 ? SpecialKey.Left : SpecialKey.Right);
        Session.ResetContext();
        AfterInput(consumedShift: false);
    });

    public void ResetContext() => Run(() =>
    {
        Session.ResetContext();
        AfterInput(consumedShift: false);
    });

    public void CycleLanguage() => Run(Languages.CycleNext);

    public void SetLanguage(string code) => Run(() => Languages.SetActive(code));

    /// <summary>Recognizes a finished swipe in the background and inserts the best word.</summary>
    public Task CommitSwipeAsync(IReadOnlyList<Vec2> points, IReadOnlyList<long>? timestamps = null)
    {
        _previewCts?.Cancel();
        var request = CaptureSwipeRequest(points, timestamps);
        var recognition = Task.Run(() => Recognize(request, 6, CancellationToken.None));

        var queued = _queue;
        var commit = Task.WhenAll(queued, recognition).ContinueWith(_ =>
        {
            if (recognition.Status != TaskStatus.RanToCompletion || recognition.Result.Count == 0) return;
            var shift = Shift;
            Session.CommitSwipe(recognition.Result, word => ApplyShift(word, shift, request.Pack));
            AfterInput(consumedShift: true);
        }, CancellationToken.None, TaskContinuationOptions.None, _scheduler);
        _queue = commit;
        return commit;
    }

    /// <summary>Best word for a swipe still in progress (latest call wins; earlier ones return null).</summary>
    public async Task<string?> PreviewSwipeAsync(IReadOnlyList<Vec2> points, IReadOnlyList<long>? timestamps = null)
    {
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        var request = CaptureSwipeRequest(points, timestamps);
        var shift = Shift;
        try
        {
            var result = await Task.Run(() => Recognize(request, 1, cts.Token), cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || result.Count == 0) return null;
            return ApplyShift(result[0].Word, shift, request.Pack);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    sealed record SwipeRequest(Vec2[] Points, long[]? Times, LanguagePack Pack, IReadOnlyList<LanguagePack> Secondary, bool NumberRow, string? PreviousWord);

    SwipeRequest CaptureSwipeRequest(IReadOnlyList<Vec2> points, IReadOnlyList<long>? timestamps)
    {
        var pack = Pack;
        var secondary = Settings.AutoDetectLanguage
            ? Languages.Enabled.Where(p => p != pack && p.Layout.LetterSignature == pack.Layout.LetterSignature).ToList()
            : new List<LanguagePack>();
        long[]? times = timestamps != null && timestamps.Count == points.Count ? timestamps.ToArray() : null;
        return new SwipeRequest(points.ToArray(), times, pack, secondary, Settings.NumberRow, Session.ContextWord);
    }

    IReadOnlyList<SwipeCandidate> Recognize(SwipeRequest request, int max, CancellationToken token)
    {
        var sources = new List<SwipeSource> { new(GetSwipeIndex(request.Pack, request.NumberRow), 0) };
        foreach (var other in request.Secondary)
            sources.Add(new SwipeSource(GetSwipeIndex(other, request.NumberRow), SecondaryLanguageBias));
        return _recognizer.Recognize(request.Points, sources, request.PreviousWord, max, Predictions.SessionBoost, token, request.Times);
    }

    void HandleKey(KeyDef key, string? alternate)
    {
        switch (key.Kind)
        {
            case KeyKind.Character:
                string text = alternate ?? key.Text;
                if (Shift != ShiftState.Off && Pack.HasCase) text = text.ToUpper(Pack.Culture);
                Session.TypeText(text);
                AfterInput(consumedShift: true);
                break;
            case KeyKind.Space:
                Session.Space();
                AfterInput(consumedShift: false);
                break;
            case KeyKind.Enter:
                Session.Enter();
                AfterInput(consumedShift: false);
                break;
            case KeyKind.Backspace:
                Session.Backspace();
                AfterInput(consumedShift: false);
                break;
            case KeyKind.Shift:
                ToggleShift();
                break;
            case KeyKind.Symbols:
                SetPage(KeyboardPage.Symbols);
                break;
            case KeyKind.SymbolsMore:
                SetPage(KeyboardPage.SymbolsMore);
                break;
            case KeyKind.Letters:
                SetPage(KeyboardPage.Letters);
                break;
            case KeyKind.Language:
                Languages.CycleNext();
                break;
        }
    }

    void SetPage(KeyboardPage page)
    {
        if (Page == page) return;
        Page = page;
        RaiseStateChanged();
    }

    void ToggleShift()
    {
        long now = Environment.TickCount64;
        _autoShift = false;
        Shift = Shift switch
        {
            ShiftState.Off => ShiftState.Once,
            ShiftState.Once => now - _lastShiftTap < DoubleTapMs ? ShiftState.Locked : ShiftState.Off,
            _ => ShiftState.Off,
        };
        _lastShiftTap = now;
        RaiseStateChanged();
    }

    void AfterInput(bool consumedShift)
    {
        var before = Shift;
        if (consumedShift && Shift == ShiftState.Once)
        {
            Shift = ShiftState.Off;
            _autoShift = false;
        }
        if (Shift != ShiftState.Locked && Pack.HasCase)
        {
            if (Settings.AutoCapitalize && Session.AtSentenceStart)
            {
                if (Shift == ShiftState.Off)
                {
                    Shift = ShiftState.Once;
                    _autoShift = true;
                }
            }
            else if (_autoShift && Shift == ShiftState.Once)
            {
                Shift = ShiftState.Off;
                _autoShift = false;
            }
        }
        if (Shift != before) RaiseStateChanged();
    }

    static string ApplyShift(string word, ShiftState shift, LanguagePack pack)
    {
        if (!pack.HasCase) return word;
        return shift switch
        {
            ShiftState.Locked => word.ToUpper(pack.Culture),
            ShiftState.Once => TextUtil.CapitalizeFirst(word, pack.Culture),
            _ => word,
        };
    }

    void OnWordCommitted(object? sender, CommittedWord committed)
    {
        if (!Settings.AutoDetectLanguage) return;
        var active = Pack;
        var candidates = Languages.Enabled.Where(p => p.Layout.LetterSignature == active.Layout.LetterSignature).ToList();
        var detected = _detector.Observe(committed.Word, active, candidates);
        if (detected != null)
            Task.Factory.StartNew(() => Languages.SetActive(detected.Code), CancellationToken.None, TaskCreationOptions.None, _scheduler);
    }

    /// <summary>Runs an input action now, or after pending swipe commits so text stays in order.</summary>
    void Run(Action action)
    {
        if (_queue.IsCompleted)
        {
            action();
            return;
        }
        _queue = _queue.ContinueWith(_ => action(), CancellationToken.None, TaskContinuationOptions.None, _scheduler);
    }

    void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
