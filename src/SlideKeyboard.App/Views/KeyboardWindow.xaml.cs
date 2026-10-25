using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using SlideKeyboard.App.Controls;
using SlideKeyboard.App.Services;
using SlideKeyboard.App.Settings;
using SlideKeyboard.App.Theming;
using SlideKeyboard.Core.Geometry;
using SlideKeyboard.Core.Input;
using SlideKeyboard.Core.Prediction;
using static SlideKeyboard.App.Interop.NativeMethods;

namespace SlideKeyboard.App.Views;

/// <summary>
/// The floating keyboard. The window is WS_EX_NOACTIVATE, so clicking or swiping on it never steals focus from the
/// application being typed into. It hosts the suggestion bar, the key surface and the bottom toolbar, and handles
/// moving, resizing and the global hotkeys.
/// </summary>
public partial class KeyboardWindow : Window
{
    const double ChromeMargin = 0;
    const double BodyPaddingX = 16 + 2;
    const double BodyPaddingY = 4 + 2 + 4;
    const int HotkeyLanguage = 1;
    const int HotkeyToggle = 2;

    static readonly Dictionary<KeyboardMode, (double Width, double Row)> BaseSize = new()
    {
        [KeyboardMode.Full] = (860, 54),
        [KeyboardMode.Compact] = (500, 44),
    };

    readonly KeyboardEngine _engine;
    readonly AppSettings _settings;
    readonly FeedbackService _feedback;
    readonly List<Button> _suggestionButtons = new();

    IntPtr _hwnd;
    bool _moving;
    Point _moveStartScreen;
    RECT _moveStartRect;
    bool _previewRunning;
    bool _previewDirty;
    long _lastPreview;
    string? _previewWord;

    internal KeyboardWindow(KeyboardEngine engine, AppSettings settings, FeedbackService feedback)
    {
        _engine = engine;
        _settings = settings;
        _feedback = feedback;
        InitializeComponent();

        Surface.KeyTapped += (_, e) => _engine.PressKey(e.Key, e.Alternate);
        Surface.KeyPressedDown += (_, _) => _feedback.KeyPress(_settings.Sound, _settings.Haptics);
        Surface.SwipeStarted += (_, _) =>
        {
            _previewWord = null;
            RenderSuggestions();
        };
        Surface.SwipeMoved += (_, gesture) => RequestPreview(gesture);
        Surface.SwipeCompleted += OnSwipeCompleted;
        Surface.CursorStep += (_, direction) => _engine.MoveCursor(direction);

        _engine.StateChanged += (_, _) => SyncFromEngine();
        _engine.SuggestionsChanged += (_, _) => RenderSuggestions();

        HookDrag(DragGrip);
        HookDrag(DragArea);
    }

    public event EventHandler? SettingsRequested;
    public event EventHandler? MinimizeRequested;
    public event EventHandler? ToggleRequested;

    /// <summary>The user changed a setting from the keyboard itself (mode, theme, size, position).</summary>
    public event EventHandler? SettingsChanged;

    public bool LanguageHotkeyRegistered { get; private set; }

    // ------------------------------------------------------------------ window setup

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        MakeFloatingToolWindow(_hwnd);
        SetWindowOpacity(_hwnd, _settings.Opacity);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        RegisterHotkeys();
        ApplyPosition();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            case WM_HOTKEY:
                handled = true;
                if (wParam.ToInt32() == HotkeyLanguage) _engine.CycleLanguage();
                else if (wParam.ToInt32() == HotkeyToggle) ToggleRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
        return IntPtr.Zero;
    }

    public void RegisterHotkeys()
    {
        if (_hwnd == IntPtr.Zero) return;
        UnregisterHotKey(_hwnd, HotkeyLanguage);
        UnregisterHotKey(_hwnd, HotkeyToggle);
        LanguageHotkeyRegistered = _settings.LanguageHotkey &&
                                   RegisterHotKey(_hwnd, HotkeyLanguage, MOD_CONTROL | MOD_NOREPEAT, VK_SPACE);
        RegisterHotKey(_hwnd, HotkeyToggle, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_K);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HotkeyLanguage);
            UnregisterHotKey(_hwnd, HotkeyToggle);
        }
        base.OnClosed(e);
    }

    // ------------------------------------------------------------------ settings and state

    /// <summary>Re-applies every visual setting (theme, size, opacity, behaviour flags).</summary>
    public void ApplySettings()
    {
        var theme = ThemeService.Current;
        Surface.Theme = theme;
        Surface.ShowTrail = _settings.SwipeTrail;
        Surface.ShowKeyPopup = _settings.KeyPopup;
        Surface.SwipeEnabled = _settings.SwipeTyping;
        if (_hwnd != IntPtr.Zero) SetWindowOpacity(_hwnd, _settings.Opacity);
        Topmost = _settings.AlwaysOnTop;
        ModeIcon.Text = _settings.Mode == KeyboardMode.Full ? "" : "";
        ThemeIcon.Text = theme.IsDark ? "" : "";
        ApplySize();
        if (_settings.Position != KeyboardPosition.Remember) ApplyPosition();
        SyncFromEngine();
    }

    void ApplySize()
    {
        var (baseWidth, baseRow) = BaseSize[_settings.Mode];
        double scale = Math.Clamp(_settings.KeyboardScale, 0.5, 1.8);
        double keyScale = Math.Clamp(_settings.KeyHeightScale, 0.6, 1.6);
        double suggestion = Math.Round((_settings.Mode == KeyboardMode.Full ? 42 : 36) * Math.Sqrt(scale));
        double toolbar = Math.Round((_settings.Mode == KeyboardMode.Full ? 36 : 32) * Math.Sqrt(scale));
        SuggestionRow.Height = new GridLength(suggestion);
        ToolbarRow.Height = new GridLength(toolbar);

        double surfaceHeight = _engine.LettersHeight * baseRow * scale * keyScale;
        Width = Math.Round(baseWidth * scale + BodyPaddingX + 2 * ChromeMargin);
        Height = Math.Round(surfaceHeight + suggestion + toolbar + BodyPaddingY + 2 * ChromeMargin);
    }

    public void ApplyPosition()
    {
        if (_settings.Position == KeyboardPosition.Remember && _settings.WindowX is int x && _settings.WindowY is int y && _hwnd != IntPtr.Zero)
        {
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x + 40, y + 40)).WorkingArea;
            GetWindowRect(_hwnd, out var rect);
            int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
            x = Math.Clamp(x, screen.Left - w / 2, screen.Right - w / 2);
            y = Math.Clamp(y, screen.Top, screen.Bottom - h / 3);
            SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            return;
        }

        var area = SystemParameters.WorkArea;
        Left = _settings.Position switch
        {
            KeyboardPosition.BottomLeft => area.Left + 6,
            KeyboardPosition.BottomRight => area.Right - Width - 6,
            _ => area.Left + (area.Width - Width) / 2,
        };
        Top = area.Bottom - Height - 6;
    }

    void SyncFromEngine()
    {
        var pack = _engine.Pack;
        Surface.Layout = _engine.Layout;
        Surface.Shift = _engine.Shift;
        Surface.HasCase = pack.HasCase;
        Surface.SpaceLabel = pack.NativeName;
        UpdateLanguageIndicator();
        RenderSuggestions();
    }

    void UpdateLanguageIndicator()
    {
        LanguagePanel.Children.Clear();
        var enabled = _engine.Languages.Enabled;
        for (int i = 0; i < enabled.Count; i++)
        {
            if (i > 0)
                LanguagePanel.Children.Add(new TextBlock
                {
                    Text = "|",
                    Margin = new Thickness(6, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("Kb.Divider"),
                });
            bool active = enabled[i] == _engine.Pack;
            LanguagePanel.Children.Add(new TextBlock
            {
                Text = enabled[i].ShortLabel,
                FontSize = 13,
                FontWeight = active ? FontWeights.Bold : FontWeights.Normal,
                Foreground = (Brush)FindResource(active ? "Kb.Accent" : "Kb.SubText"),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
    }

    // ------------------------------------------------------------------ suggestions

    void EnsureSuggestionSlots(int count)
    {
        if (_suggestionButtons.Count == count) return;
        SuggestionBar.Children.Clear();
        SuggestionBar.ColumnDefinitions.Clear();
        _suggestionButtons.Clear();
        for (int i = 0; i < count; i++)
        {
            SuggestionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var button = new Button { Style = (Style)FindResource("SuggestionButton") };
            button.Click += OnSuggestionClick;
            Grid.SetColumn(button, i);
            SuggestionBar.Children.Add(button);
            _suggestionButtons.Add(button);
            if (i > 0)
            {
                var divider = new Rectangle
                {
                    Width = 1,
                    Height = 18,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                };
                divider.SetResourceReference(Shape.FillProperty, "Kb.Divider");
                Grid.SetColumn(divider, i);
                SuggestionBar.Children.Add(divider);
            }
        }
    }

    void RenderSuggestions()
    {
        int slots = _settings.SuggestionCount >= 5 ? 5 : 3;
        EnsureSuggestionSlots(slots);
        SuggestionBar.FlowDirection = _engine.Pack.Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        // The primary suggestion goes in the middle, the next ones alternate outwards.
        int[] order = slots == 5 ? new[] { 2, 1, 3, 0, 4 } : new[] { 1, 0, 2 };
        var items = new Suggestion?[slots];
        bool preview = Surface.IsSwiping;
        if (preview)
        {
            if (_previewWord != null) items[order[0]] = new Suggestion(_previewWord, SuggestionKind.SwipeAlternative, true);
        }
        else
        {
            var list = _engine.Suggestions;
            for (int i = 0; i < list.Count && i < slots; i++) items[order[i]] = list[i];
        }

        for (int i = 0; i < slots; i++)
        {
            var button = _suggestionButtons[i];
            var item = items[i];
            button.Tag = item;
            button.IsHitTestVisible = item != null && !preview;
            button.Content = item == null ? null : new TextBlock
            {
                Text = item.Kind switch
                {
                    SuggestionKind.Literal => $"“{item.Text}”",
                    SuggestionKind.AddToDictionary => $"+ Save “{item.Text}”",
                    _ => item.Text,
                },
                ToolTip = item.Kind == SuggestionKind.AddToDictionary ? "Add this word to your personal dictionary" : null,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = item.IsPrimary ? FontWeights.SemiBold : FontWeights.Normal,
            };
            button.SetResourceReference(ForegroundProperty,
                preview ? "Kb.Accent" : item?.IsPrimary == true ? "Kb.SuggestionPrimary" : "Kb.SuggestionText");
        }
    }

    void OnSuggestionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Suggestion suggestion }) return;
        if (DebugGestureLog && suggestion.Kind == SuggestionKind.SwipeAlternative && !suggestion.IsPrimary)
            AppendGestureLog($"picked={suggestion.Text}{Environment.NewLine}");
        _engine.PickSuggestion(suggestion);
    }

    void RequestPreview(SwipeGestureEventArgs gesture)
    {
        long now = Environment.TickCount64;
        if (_previewRunning || now - _lastPreview < 45)
        {
            _previewDirty = true;
            return;
        }
        RunPreview(gesture.Points.ToArray(), gesture.Times.ToArray());
    }

    async void RunPreview(Vec2[] points, long[] times)
    {
        _previewRunning = true;
        _previewDirty = false;
        _lastPreview = Environment.TickCount64;
        try
        {
            var word = await _engine.PreviewSwipeAsync(points, times);
            if (Surface.IsSwiping && word != null && word != _previewWord)
            {
                _previewWord = word;
                RenderSuggestions();
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Swipe preview failed", ex);
        }
        finally
        {
            _previewRunning = false;
        }
        if (_previewDirty && Surface.IsSwiping)
        {
            var snapshot = Surface.SwipeSnapshot;
            RunPreview(snapshot.Points.ToArray(), snapshot.Times.ToArray());
        }
    }

    async void OnSwipeCompleted(object? sender, SwipeGestureEventArgs gesture)
    {
        _previewWord = null;
        try
        {
            await _engine.CommitSwipeAsync(gesture.Points, gesture.Times);
            if (DebugGestureLog) WriteGestureLog(gesture);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Swipe commit failed", ex);
        }
        RenderSuggestions();
    }

    /// <summary>
    /// Troubleshooting aid, off unless enabled in Settings (or with SLIDEKEYBOARD_DEBUG_GESTURES=1): appends each
    /// swipe's points, the resulting suggestions and any correction the user picked to
    /// %APPDATA%\SlideKeyboard\gestures.log so recognition can be tuned on real gestures.
    /// </summary>
    bool DebugGestureLog => _settings.RecordSwipes || Environment.GetEnvironmentVariable("SLIDEKEYBOARD_DEBUG_GESTURES") == "1";

    static void AppendGestureLog(string text)
    {
        try
        {
            System.IO.Directory.CreateDirectory(SettingsStore.Directory);
            System.IO.File.AppendAllText(System.IO.Path.Combine(SettingsStore.Directory, "gestures.log"), text);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Could not write gesture log", ex);
        }
    }

    void WriteGestureLog(SwipeGestureEventArgs gesture)
    {
        try
        {
            var points = string.Join(";", gesture.Points.Select((p, i) =>
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.X:F3},{p.Y:F3},{gesture.Times[i] - gesture.Times[0]}")));
            var words = string.Join("|", _engine.Suggestions.Select(s => s.Text));
            System.IO.File.AppendAllText(System.IO.Path.Combine(SettingsStore.Directory, "gestures.log"),
                $"{_engine.Pack.Code} numberRow={_engine.Settings.NumberRow} size={Surface.ActualWidth:F0}x{Surface.ActualHeight:F0} words={words}{Environment.NewLine}{points}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Could not write gesture log", ex);
        }
    }

    // ------------------------------------------------------------------ toolbar

    void OnLanguageClick(object sender, RoutedEventArgs e) => _engine.CycleLanguage();

    void OnModeClick(object sender, RoutedEventArgs e)
    {
        _settings.Mode = _settings.Mode == KeyboardMode.Full ? KeyboardMode.Compact : KeyboardMode.Full;
        KeepBottomEdge(() => SettingsChanged?.Invoke(this, EventArgs.Empty));
    }

    void OnThemeClick(object sender, RoutedEventArgs e)
    {
        _settings.Theme = ThemeService.Current.IsDark ? ThemeMode.Light : ThemeMode.Dark;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    void OnMinimizeClick(object sender, RoutedEventArgs e) => MinimizeRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Runs a size change while keeping a remembered window anchored at its bottom edge.</summary>
    void KeepBottomEdge(Action change)
    {
        if (_settings.Position != KeyboardPosition.Remember || _hwnd == IntPtr.Zero)
        {
            change();
            return;
        }
        GetWindowRect(_hwnd, out var before);
        change();
        UpdateLayout();
        GetWindowRect(_hwnd, out var after);
        int dy = before.Bottom - after.Bottom;
        SetWindowPos(_hwnd, IntPtr.Zero, after.Left, after.Top + dy, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        SaveWindowPosition();
    }

    // ------------------------------------------------------------------ move and resize

    void HookDrag(FrameworkElement element)
    {
        element.MouseLeftButtonDown += (_, e) =>
        {
            if (e.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Touch) return;
            if (!element.CaptureMouse()) return;
            BeginMove(element.PointToScreen(e.GetPosition(element)));
            e.Handled = true;
        };
        element.MouseMove += (_, e) =>
        {
            if (_moving && element.IsMouseCaptured) ContinueMove(element.PointToScreen(e.GetPosition(element)));
        };
        element.MouseLeftButtonUp += (_, e) =>
        {
            if (!_moving) return;
            element.ReleaseMouseCapture();
            e.Handled = true;
        };
        element.LostMouseCapture += (_, _) => EndMove();
        element.TouchDown += (_, e) =>
        {
            if (!e.TouchDevice.Capture(element)) return;
            BeginMove(element.PointToScreen(e.GetTouchPoint(element).Position));
            e.Handled = true;
        };
        element.TouchMove += (_, e) =>
        {
            if (_moving && e.TouchDevice.Captured == element) ContinueMove(element.PointToScreen(e.GetTouchPoint(element).Position));
            e.Handled = true;
        };
        element.TouchUp += (_, e) =>
        {
            element.ReleaseTouchCapture(e.TouchDevice);
            EndMove();
            e.Handled = true;
        };
    }

    void BeginMove(Point screen)
    {
        _moving = true;
        _moveStartScreen = screen;
        GetWindowRect(_hwnd, out _moveStartRect);
    }

    void ContinueMove(Point screen)
    {
        int dx = (int)Math.Round(screen.X - _moveStartScreen.X);
        int dy = (int)Math.Round(screen.Y - _moveStartScreen.Y);
        SetWindowPos(_hwnd, IntPtr.Zero, _moveStartRect.Left + dx, _moveStartRect.Top + dy, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    void EndMove()
    {
        if (!_moving) return;
        _moving = false;
        SaveWindowPosition();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    void SaveWindowPosition()
    {
        GetWindowRect(_hwnd, out var rect);
        _settings.WindowX = rect.Left;
        _settings.WindowY = rect.Top;
        _settings.Position = KeyboardPosition.Remember;
    }

    void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        var (baseWidth, baseRow) = BaseSize[_settings.Mode];
        double newWidth = Width + e.HorizontalChange;
        double newHeight = Height + e.VerticalChange;

        double scale = Math.Clamp((newWidth - BodyPaddingX - 2 * ChromeMargin) / baseWidth, 0.5, 1.8);
        double fixedHeight = SuggestionRow.Height.Value + ToolbarRow.Height.Value + BodyPaddingY + 2 * ChromeMargin;
        double keyScale = Math.Clamp((newHeight - fixedHeight) / (_engine.LettersHeight * baseRow * scale), 0.6, 1.6);

        _settings.KeyboardScale = scale;
        _settings.KeyHeightScale = keyScale;
        ApplySize();
    }

    void OnResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        SaveWindowPosition();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Screen rectangle of the window in physical pixels.</summary>
    internal RECT ScreenRect
    {
        get
        {
            GetWindowRect(_hwnd, out var rect);
            return rect;
        }
    }
}
