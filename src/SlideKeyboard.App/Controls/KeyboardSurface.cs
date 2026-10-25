using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SlideKeyboard.App.Theming;
using SlideKeyboard.Core.Geometry;
using SlideKeyboard.Core.Input;
using SlideKeyboard.Core.Layout;

namespace SlideKeyboard.App.Controls;

public sealed class KeyTapEventArgs : EventArgs
{
    public KeyTapEventArgs(KeyDef key, string? alternate)
    {
        Key = key;
        Alternate = alternate;
    }

    public KeyDef Key { get; }
    public string? Alternate { get; }
}

/// <summary>A swipe gesture in keyboard units with per-point timestamps (milliseconds).</summary>
public sealed class SwipeGestureEventArgs : EventArgs
{
    public SwipeGestureEventArgs(IReadOnlyList<Vec2> points, IReadOnlyList<long> times)
    {
        Points = points;
        Times = times;
    }

    public IReadOnlyList<Vec2> Points { get; }
    public IReadOnlyList<long> Times { get; }
}

/// <summary>
/// Draws the keys and handles pointer input (mouse, touch, pen) for one keyboard page.
///
/// Rendering uses two retained visuals: the key layer is redrawn only when the layout, size, theme or shift state
/// changes; the overlay (pressed keys, swipe trail, popups) is redrawn per frame only while something animates,
/// so the control costs nothing while idle.
///
/// Gestures: a press that moves more than <see cref="SwipeThreshold"/> key widths from a letter becomes a swipe;
/// holding still opens the long-press alternates; dragging sideways on the space bar moves the caret.
/// </summary>
public sealed class KeyboardSurface : FrameworkElement
{
    enum GestureState { None, Pressed, Swiping, LongPress, SpaceCursor, Repeating }

    enum KeyLook { Normal, Pressed, Active }

    const int MousePointerId = -1;
    const double SwipeThreshold = 0.42;
    const double SlideThreshold = 0.35;
    const double CursorStepKeys = 0.45;
    const int LongPressMs = 500;
    const int RepeatDelayMs = 400;
    const int RepeatIntervalMs = 55;
    const double TrailLifetimeMs = 480;
    const double ReleaseFadeMs = 220;
    const double TouchedFadeMs = 280;
    const int PenLevels = 16;

    static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
    static readonly FontFamily SymbolFont = new("Segoe UI Symbol");
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    readonly DrawingVisual _keysVisual = new();
    readonly DrawingVisual _overlayVisual = new();
    readonly VisualCollection _children;
    readonly DispatcherTimer _longPressTimer;
    readonly DispatcherTimer _repeatTimer;
    readonly List<(Point P, long T)> _trail = new();
    readonly List<Vec2> _unitPoints = new();
    readonly List<long> _unitTimes = new();
    readonly Dictionary<KeyDef, long> _touched = new();
    readonly Dictionary<(string, double, FontFamily, Color, bool), FormattedText> _textCache = new();

    KeyboardLayout? _layout;
    Theme _theme = Theme.Light;
    ShiftState _shift;
    bool _hasCase = true;
    string _spaceLabel = "";
    Pen[] _trailPens = Array.Empty<Pen>();
    Brush _keyBrush = Brushes.White, _specialBrush = Brushes.LightGray, _pressedBrush = Brushes.Gray,
          _shadowBrush = Brushes.Transparent, _accentBrush = Brushes.Blue, _highlightBrush = Brushes.Transparent;
    Pen _activePen = new(Brushes.Blue, 1.5);

    GestureState _state;
    int _pointerId;
    KeyDef? _downKey;
    KeyDef? _hoverKey;
    KeyDef? _swipeKey;
    Point _downPos;
    long _releaseTime;
    int _altIndex;
    Rect _altRect;
    double _altCellWidth;
    double _cursorAnchorX;
    bool _renderingHooked;
    double _kx = 1, _ky = 1;

    public KeyboardSurface()
    {
        _children = new VisualCollection(this) { _keysVisual, _overlayVisual };
        _longPressTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(LongPressMs) };
        _longPressTimer.Tick += OnLongPress;
        _repeatTimer = new DispatcherTimer(DispatcherPriority.Input);
        _repeatTimer.Tick += OnRepeat;

        Focusable = false;
        SnapsToDevicePixels = true;
        Stylus.SetIsPressAndHoldEnabled(this, false);
        Stylus.SetIsFlicksEnabled(this, false);
        Stylus.SetIsTapFeedbackEnabled(this, false);
        Stylus.SetIsTouchFeedbackEnabled(this, false);
        UpdateThemeResources();
    }

    /// <summary>Raised when a key is activated (tap, long-press alternate, backspace repeat).</summary>
    public event EventHandler<KeyTapEventArgs>? KeyTapped;

    /// <summary>Raised when a pointer goes down on a key (for sound/haptic feedback).</summary>
    public event EventHandler<KeyDef>? KeyPressedDown;

    public event EventHandler? SwipeStarted;

    /// <summary>Gesture so far, in keyboard units. The lists are live; copy them if you keep them.</summary>
    public event EventHandler<SwipeGestureEventArgs>? SwipeMoved;

    public event EventHandler<SwipeGestureEventArgs>? SwipeCompleted;

    public event EventHandler<int>? CursorStep;

    public bool ShowTrail { get; set; } = true;
    public bool ShowKeyPopup { get; set; } = true;
    public bool SwipeEnabled { get; set; } = true;

    public bool IsSwiping => _state == GestureState.Swiping;

    public SwipeGestureEventArgs SwipeSnapshot => new(_unitPoints.ToArray(), _unitTimes.ToArray());

    public KeyboardLayout? Layout
    {
        get => _layout;
        set
        {
            if (ReferenceEquals(_layout, value)) return;
            CancelGesture();
            _layout = value;
            RedrawKeys();
        }
    }

    public Theme Theme
    {
        get => _theme;
        set
        {
            if (ReferenceEquals(_theme, value)) return;
            _theme = value;
            UpdateThemeResources();
            RedrawKeys();
        }
    }

    public ShiftState Shift
    {
        get => _shift;
        set
        {
            if (_shift == value) return;
            _shift = value;
            RedrawKeys();
        }
    }

    public bool HasCase
    {
        get => _hasCase;
        set
        {
            if (_hasCase == value) return;
            _hasCase = value;
            RedrawKeys();
        }
    }

    public string SpaceLabel
    {
        get => _spaceLabel;
        set
        {
            if (_spaceLabel == value) return;
            _spaceLabel = value;
            RedrawKeys();
        }
    }

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index) => _children[index];

    protected override HitTestResult? HitTestCore(PointHitTestParameters hit) =>
        new Rect(RenderSize).Contains(hit.HitPoint) ? new PointHitTestResult(this, hit.HitPoint) : null;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _textCache.Clear();
        UpdateTrailPens();
        RedrawKeys();
    }

    // ------------------------------------------------------------------ geometry

    void UpdateScale()
    {
        if (_layout == null) return;
        _kx = ActualWidth / _layout.Width;
        _ky = ActualHeight / _layout.Height;
    }

    Vec2 ToUnits(Point p) => new(p.X / _kx, p.Y / _ky);

    Rect KeyRect(KeyDef key)
    {
        var b = key.Bounds;
        double gx = Math.Clamp(_kx * 0.045, 1.5, 4);
        double gy = Math.Clamp(_ky * 0.075, 2, 5);
        return new Rect(b.X * _kx + gx, b.Y * _ky + gy, Math.Max(0, b.Width * _kx - 2 * gx), Math.Max(0, b.Height * _ky - 2 * gy));
    }

    // ------------------------------------------------------------------ rendering

    void UpdateThemeResources()
    {
        _keyBrush = Theme.Brush(_theme.Key);
        _specialBrush = Theme.Brush(_theme.KeySpecial);
        _pressedBrush = Theme.Brush(_theme.KeyPressed);
        _shadowBrush = Theme.Brush(_theme.KeyShadow);
        _accentBrush = Theme.Brush(_theme.Accent);
        _highlightBrush = Theme.Brush(Color.FromArgb(0x55, _theme.Accent.R, _theme.Accent.G, _theme.Accent.B));
        _activePen = new Pen(_accentBrush, 1.6);
        _activePen.Freeze();
        _textCache.Clear();
        UpdateTrailPens();
    }

    void UpdateTrailPens()
    {
        double width = Math.Clamp((_layout == null ? 60 : ActualWidth / _layout.Width) * 0.11, 4, 9);
        _trailPens = new Pen[PenLevels];
        for (int i = 0; i < PenLevels; i++)
        {
            double level = (i + 1) / (double)PenLevels;
            var color = Color.FromArgb((byte)(235 * level), _theme.Trail.R, _theme.Trail.G, _theme.Trail.B);
            var pen = new Pen(Theme.Brush(color), width * (0.35 + 0.65 * level))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();
            _trailPens[i] = pen;
        }
    }

    void RedrawKeys()
    {
        using (var dc = _keysVisual.RenderOpen())
        {
            if (_layout == null || ActualWidth <= 0 || ActualHeight <= 0) return;
            UpdateScale();
            foreach (var key in _layout.Keys) DrawKey(dc, key, KeyRect(key), KeyLook.Normal);
        }
        RedrawOverlay();
    }

    void DrawKey(DrawingContext dc, KeyDef key, Rect r, KeyLook look)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        double radius = Math.Clamp(Math.Min(r.Width, r.Height) * 0.2, 3, 11);

        bool accent = key.Kind == KeyKind.Enter || (key.Kind == KeyKind.Shift && _shift == ShiftState.Locked);
        Brush fill = look != KeyLook.Normal ? _pressedBrush
            : accent ? _accentBrush
            : key.Kind == KeyKind.Character || key.Kind == KeyKind.Space ? _keyBrush
            : _specialBrush;
        Color text = accent && look == KeyLook.Normal ? _theme.AccentText : _theme.KeyText;

        dc.DrawRoundedRectangle(_shadowBrush, null, new Rect(r.X, r.Y + 1.3, r.Width, r.Height), radius, radius);
        dc.DrawRoundedRectangle(fill, look == KeyLook.Active ? _activePen : null, r, radius, radius);
        DrawLabel(dc, key, r, text, look == KeyLook.Active ? 1.15 : 1);
    }

    void DrawLabel(DrawingContext dc, KeyDef key, Rect r, Color color, double scale)
    {
        double unit = Math.Min(_ky, _kx * 1.25);
        switch (key.Kind)
        {
            case KeyKind.Character:
            {
                string label = key.Label;
                if (key.IsLetter && _hasCase && _shift != ShiftState.Off) label = label.ToUpperInvariant();
                double size = (key.IsLetter ? 0.42 : 0.38) * unit * scale;
                if (key.Bounds.Height < 1) size *= 0.85;
                DrawCentered(dc, label, size, TextFont, color, r, bold: false);
                if (key.Hint != null && scale == 1)
                {
                    var hint = GetText(key.Hint, Math.Max(7, 0.2 * unit), TextFont, _theme.KeySubText, false);
                    dc.DrawText(hint, new Point(r.Right - hint.Width - r.Width * 0.12, r.Top + r.Height * 0.06));
                }
                break;
            }
            case KeyKind.Shift:
                DrawCentered(dc, _shift == ShiftState.Off ? "⇧" : "⬆", 0.42 * unit, SymbolFont, color, r, false);
                if (_shift == ShiftState.Locked)
                {
                    var bar = new Rect(r.X + r.Width / 2 - unit * 0.14, r.Y + r.Height * 0.76, unit * 0.28, Math.Max(1.5, unit * 0.04));
                    dc.DrawRectangle(Theme.Brush(color), null, bar);
                }
                break;
            case KeyKind.Backspace:
                if (_layout?.Rtl == true)
                {
                    dc.PushTransform(new ScaleTransform(-1, 1, r.X + r.Width / 2, 0));
                    DrawCentered(dc, "", 0.36 * unit, IconFont, color, r, false);
                    dc.Pop();
                }
                else
                {
                    DrawCentered(dc, "", 0.36 * unit, IconFont, color, r, false);
                }
                break;
            case KeyKind.Enter:
                DrawCentered(dc, "", 0.36 * unit, IconFont, color, r, false);
                break;
            case KeyKind.Language:
                DrawCentered(dc, "", 0.34 * unit, IconFont, color, r, false);
                break;
            case KeyKind.Space:
                if (_spaceLabel.Length > 0)
                    DrawCentered(dc, _spaceLabel, 0.26 * unit, TextFont, _theme.KeySubText, r, false);
                break;
            default:
                DrawCentered(dc, key.Label, 0.29 * unit, TextFont, color, r, bold: false);
                break;
        }
    }

    void DrawCentered(DrawingContext dc, string text, double size, FontFamily font, Color color, Rect r, bool bold)
    {
        if (string.IsNullOrEmpty(text) || size < 1) return;
        var ft = GetText(text, size, font, color, bold);
        dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
    }

    FormattedText GetText(string text, double size, FontFamily font, Color color, bool bold)
    {
        size = Math.Round(size, 1);
        var key = (text, size, font, color, bold);
        if (_textCache.TryGetValue(key, out var cached)) return cached;
        if (_textCache.Count > 600) _textCache.Clear();
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(font, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, Theme.Brush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _textCache[key] = ft;
        return ft;
    }

    void RedrawOverlay()
    {
        using var dc = _overlayVisual.RenderOpen();
        if (_layout == null || ActualWidth <= 0) return;
        long now = Environment.TickCount64;

        // Keys the swipe passed over glow briefly.
        foreach (var (key, time) in _touched)
        {
            double age = now - time;
            if (key == _swipeKey && _state == GestureState.Swiping) continue;
            if (age > TouchedFadeMs) continue;
            var r = KeyRect(key);
            double radius = Math.Clamp(Math.Min(r.Width, r.Height) * 0.2, 3, 11);
            dc.PushOpacity(1 - age / TouchedFadeMs);
            dc.DrawRoundedRectangle(_highlightBrush, null, r, radius, radius);
            dc.Pop();
        }

        switch (_state)
        {
            case GestureState.Pressed:
            case GestureState.Repeating:
            {
                var key = _hoverKey ?? _downKey;
                if (key == null) break;
                DrawKey(dc, key, KeyRect(key), KeyLook.Pressed);
                if (ShowKeyPopup && _state == GestureState.Pressed && key.Kind == KeyKind.Character)
                    DrawPreviewBalloon(dc, key);
                break;
            }
            case GestureState.SpaceCursor:
                if (_downKey != null) DrawKey(dc, _downKey, KeyRect(_downKey), KeyLook.Pressed);
                break;
            case GestureState.LongPress:
                if (_downKey != null)
                {
                    DrawKey(dc, _downKey, KeyRect(_downKey), KeyLook.Pressed);
                    DrawAlternates(dc, _downKey);
                }
                break;
            case GestureState.Swiping:
                if (_swipeKey != null)
                {
                    // The key under the finger grows slightly.
                    var r = KeyRect(_swipeKey);
                    double gx = r.Width * 0.06, gy = r.Height * 0.06;
                    DrawKey(dc, _swipeKey, new Rect(r.X - gx, r.Y - gy, r.Width + 2 * gx, r.Height + 2 * gy), KeyLook.Active);
                }
                break;
        }

        if (ShowTrail) DrawTrail(dc, now);
    }

    void DrawPreviewBalloon(DrawingContext dc, KeyDef key)
    {
        var r = KeyRect(key);
        double w = Math.Max(r.Width * 1.2, _ky * 0.9), h = _ky * 1.05;
        var balloon = new Rect(r.X + r.Width / 2 - w / 2, r.Top - h - 3, w, h);
        double radius = Math.Clamp(w * 0.18, 4, 12);
        dc.DrawRoundedRectangle(_shadowBrush, null, new Rect(balloon.X, balloon.Y + 2, balloon.Width, balloon.Height), radius, radius);
        dc.DrawRoundedRectangle(_keyBrush, null, balloon, radius, radius);
        string label = key.IsLetter && _hasCase && _shift != ShiftState.Off ? key.Label.ToUpperInvariant() : key.Label;
        DrawCentered(dc, label, 0.58 * Math.Min(_ky, _kx * 1.25), TextFont, _theme.KeyText, balloon, bold: false);
    }

    void DrawAlternates(DrawingContext dc, KeyDef key)
    {
        double radius = Math.Clamp(_altRect.Height * 0.2, 4, 12);
        dc.DrawRoundedRectangle(_shadowBrush, null, new Rect(_altRect.X, _altRect.Y + 2, _altRect.Width, _altRect.Height), radius, radius);
        dc.DrawRoundedRectangle(_keyBrush, null, _altRect, radius, radius);
        double pad = 4;
        for (int i = 0; i < key.Alternates.Count; i++)
        {
            var cell = new Rect(_altRect.X + pad + i * _altCellWidth, _altRect.Y + pad, _altCellWidth, _altRect.Height - 2 * pad);
            bool selected = i == _altIndex;
            if (selected) dc.DrawRoundedRectangle(_accentBrush, null, cell, radius * 0.8, radius * 0.8);
            string text = _hasCase && _shift != ShiftState.Off ? key.Alternates[i].ToUpperInvariant() : key.Alternates[i];
            DrawCentered(dc, text, 0.42 * Math.Min(_ky, _kx * 1.25), TextFont, selected ? _theme.AccentText : _theme.KeyText, cell, false);
        }
    }

    void DrawTrail(DrawingContext dc, long now)
    {
        if (_trail.Count < 2 || _trailPens.Length == 0) return;
        double release = _state == GestureState.Swiping ? 1 : Math.Max(0, 1 - (now - _releaseTime) / ReleaseFadeMs);
        if (release <= 0) return;

        // Consecutive segments with the same fade level are drawn as one polyline to avoid overlapping caps.
        int runLevel = -1;
        var run = new List<Point>();
        void Flush()
        {
            if (runLevel >= 0 && run.Count >= 2)
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(run[0], false, false);
                    ctx.PolyLineTo(run.Skip(1).ToList(), true, true);
                }
                geometry.Freeze();
                dc.DrawGeometry(null, _trailPens[runLevel], geometry);
            }
            run.Clear();
        }

        for (int i = 1; i < _trail.Count; i++)
        {
            double alpha = Math.Max(0, 1 - (now - _trail[i].T) / TrailLifetimeMs) * release;
            int level = alpha <= 0.03 ? -1 : (int)Math.Round(alpha * (PenLevels - 1));
            if (level != runLevel)
            {
                Flush();
                runLevel = level;
                if (level >= 0) run.Add(_trail[i - 1].P);
            }
            if (level >= 0) run.Add(_trail[i].P);
        }
        Flush();
    }

    bool IsAnimating(long now) =>
        _state == GestureState.Swiping ||
        (_trail.Count > 0 && now - _releaseTime < ReleaseFadeMs) ||
        _touched.Values.Any(t => now - t < TouchedFadeMs);

    void EnsureAnimating()
    {
        if (_renderingHooked) return;
        CompositionTarget.Rendering += OnRendering;
        _renderingHooked = true;
    }

    void OnRendering(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        if (!IsAnimating(now))
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
            _trail.Clear();
            _touched.Clear();
        }
        RedrawOverlay();
    }

    // ------------------------------------------------------------------ input plumbing

    static bool IsTouchPromoted(MouseEventArgs e) => e.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Touch;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (IsTouchPromoted(e)) return;
        if (_state != GestureState.None || !CaptureMouse()) return;
        _pointerId = MousePointerId;
        PointerDown(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsTouchPromoted(e) || _pointerId != MousePointerId || _state == GestureState.None) return;
        PointerMove(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsTouchPromoted(e) || _pointerId != MousePointerId || _state == GestureState.None) return;
        PointerUp(e.GetPosition(this));
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_pointerId == MousePointerId && _state != GestureState.None) CancelGesture();
    }

    protected override void OnTouchDown(TouchEventArgs e)
    {
        e.Handled = true;
        if (_state == GestureState.Pressed && _pointerId != e.TouchDevice.Id)
        {
            // Second finger while the first is still a tap: commit the first tap (fast two-thumb typing).
            CommitPendingTap();
        }
        if (_state != GestureState.None) return;
        if (!e.TouchDevice.Capture(this)) return;
        _pointerId = e.TouchDevice.Id;
        PointerDown(e.GetTouchPoint(this).Position);
    }

    protected override void OnTouchMove(TouchEventArgs e)
    {
        e.Handled = true;
        if (_pointerId == e.TouchDevice.Id && _state != GestureState.None) PointerMove(e.GetTouchPoint(this).Position);
    }

    protected override void OnTouchUp(TouchEventArgs e)
    {
        e.Handled = true;
        if (_pointerId == e.TouchDevice.Id && _state != GestureState.None) PointerUp(e.GetTouchPoint(this).Position);
        e.TouchDevice.Capture(null);
    }

    protected override void OnLostTouchCapture(TouchEventArgs e)
    {
        base.OnLostTouchCapture(e);
        if (_pointerId == e.TouchDevice.Id && _state != GestureState.None) CancelGesture();
    }

    // ------------------------------------------------------------------ gestures

    void PointerDown(Point p)
    {
        if (_layout == null) return;
        UpdateScale();
        var key = _layout.HitTest(ToUnits(p));
        if (key == null) return;

        _downKey = key;
        _hoverKey = null;
        _swipeKey = null;
        _downPos = p;
        _unitPoints.Clear();
        _unitTimes.Clear();
        _unitPoints.Add(ToUnits(p));
        _unitTimes.Add(Environment.TickCount64);
        _trail.Clear();
        _touched.Clear();
        _trail.Add((p, Environment.TickCount64));
        _state = GestureState.Pressed;
        KeyPressedDown?.Invoke(this, key);

        if (key.Kind == KeyKind.Backspace)
        {
            KeyTapped?.Invoke(this, new KeyTapEventArgs(key, null));
            _state = GestureState.Repeating;
            _repeatTimer.Interval = TimeSpan.FromMilliseconds(RepeatDelayMs);
            _repeatTimer.Start();
        }
        else if (key.Alternates.Count > 0)
        {
            _longPressTimer.Start();
        }
        RedrawOverlay();
    }

    void PointerMove(Point p)
    {
        if (_layout == null || _downKey == null) return;
        var u = ToUnits(p);
        long now = Environment.TickCount64;

        switch (_state)
        {
            case GestureState.Pressed:
            {
                AddPoint(p, u, now);
                double dx = (p.X - _downPos.X) / _kx, dy = (p.Y - _downPos.Y) / _ky;
                double distance = Math.Sqrt(dx * dx + dy * dy);

                if (_downKey.Kind == KeyKind.Space && Math.Abs(dx) > CursorStepKeys * 1.5)
                {
                    _longPressTimer.Stop();
                    _state = GestureState.SpaceCursor;
                    _cursorAnchorX = p.X;
                }
                else if (SwipeEnabled && _downKey.IsLetter && _layout.Page == KeyboardPage.Letters && distance > SwipeThreshold)
                {
                    _longPressTimer.Stop();
                    BeginSwipe(u, now);
                }
                else if (distance > SlideThreshold && (!_downKey.IsLetter || !SwipeEnabled || _layout.Page != KeyboardPage.Letters))
                {
                    // Slide-to-select on non-swipe keys: the key under the pointer at release is typed.
                    _longPressTimer.Stop();
                    var hover = _layout.HitTest(u);
                    if (hover != _hoverKey)
                    {
                        _hoverKey = hover;
                        RedrawOverlay();
                    }
                }
                break;
            }
            case GestureState.Swiping:
                AddPoint(p, u, now);
                UpdateSwipeKey(u, now);
                SwipeMoved?.Invoke(this, new SwipeGestureEventArgs(_unitPoints, _unitTimes));
                break;
            case GestureState.LongPress:
            {
                AddPoint(p, u, now);
                // Mouse users often pause on the first letter before swiping. Leaving the accent popup (or moving
                // down off the key) turns the long-press back into a swipe instead of trapping it.
                var keyRect = KeyRect(_downKey);
                bool outside = p.X < _altRect.Left - _kx * 0.5 || p.X > _altRect.Right + _kx * 0.5 ||
                               p.Y > keyRect.Bottom + _ky * 0.25 || p.Y < _altRect.Top - _ky * 0.5;
                if (outside && SwipeEnabled && _downKey.IsLetter && _layout.Page == KeyboardPage.Letters)
                {
                    BeginSwipe(u, now);
                    RedrawOverlay();
                    break;
                }
                int index = (int)Math.Floor((p.X - _altRect.X - 4) / _altCellWidth);
                index = Math.Clamp(index, 0, _downKey.Alternates.Count - 1);
                if (index != _altIndex)
                {
                    _altIndex = index;
                    RedrawOverlay();
                }
                break;
            }
            case GestureState.SpaceCursor:
            {
                double step = CursorStepKeys * _kx;
                while (p.X - _cursorAnchorX >= step)
                {
                    _cursorAnchorX += step;
                    CursorStep?.Invoke(this, 1);
                }
                while (_cursorAnchorX - p.X >= step)
                {
                    _cursorAnchorX -= step;
                    CursorStep?.Invoke(this, -1);
                }
                break;
            }
        }
    }

    void PointerUp(Point p)
    {
        _longPressTimer.Stop();
        _repeatTimer.Stop();
        var state = _state;
        var downKey = _downKey;
        _state = GestureState.None;

        switch (state)
        {
            case GestureState.Pressed:
            {
                var key = _hoverKey ?? downKey;
                if (key != null) KeyTapped?.Invoke(this, new KeyTapEventArgs(key, null));
                break;
            }
            case GestureState.Swiping:
                _unitPoints.Add(ToUnits(p));
                _unitTimes.Add(Environment.TickCount64);
                _releaseTime = Environment.TickCount64;
                SwipeCompleted?.Invoke(this, new SwipeGestureEventArgs(_unitPoints.ToArray(), _unitTimes.ToArray()));
                EnsureAnimating();
                break;
            case GestureState.LongPress:
                if (downKey != null && _altIndex >= 0 && _altIndex < downKey.Alternates.Count)
                    KeyTapped?.Invoke(this, new KeyTapEventArgs(downKey, downKey.Alternates[_altIndex]));
                break;
        }

        _downKey = null;
        _hoverKey = null;
        _swipeKey = null;
        RedrawOverlay();
    }

    void CommitPendingTap()
    {
        if (_state != GestureState.Pressed) return;
        _longPressTimer.Stop();
        var key = _hoverKey ?? _downKey;
        _state = GestureState.None;
        _downKey = _hoverKey = null;
        if (key != null) KeyTapped?.Invoke(this, new KeyTapEventArgs(key, null));
        RedrawOverlay();
    }

    void CancelGesture()
    {
        _longPressTimer.Stop();
        _repeatTimer.Stop();
        if (_state == GestureState.None) return;
        _state = GestureState.None;
        _downKey = _hoverKey = _swipeKey = null;
        _trail.Clear();
        _unitPoints.Clear();
        _unitTimes.Clear();
        RedrawOverlay();
    }

    void BeginSwipe(Vec2 u, long now)
    {
        _state = GestureState.Swiping;
        _touched[_downKey!] = now;
        UpdateSwipeKey(u, now);
        SwipeStarted?.Invoke(this, EventArgs.Empty);
        SwipeMoved?.Invoke(this, new SwipeGestureEventArgs(_unitPoints, _unitTimes));
        EnsureAnimating();
    }

    void AddPoint(Point p, Vec2 u, long now)
    {
        _unitPoints.Add(u);
        _unitTimes.Add(now);
        // Light smoothing for the drawn trail only; recognition gets the raw points.
        var last = _trail.Count > 0 ? _trail[^1].P : p;
        var smoothed = new Point(last.X + (p.X - last.X) * 0.65, last.Y + (p.Y - last.Y) * 0.65);
        if ((smoothed - last).Length >= 1.0 || _trail.Count == 0) _trail.Add((smoothed, now));
        if (_trail.Count > 600) _trail.RemoveRange(0, 100);
    }

    void UpdateSwipeKey(Vec2 u, long now)
    {
        var key = _layout?.HitTest(u);
        if (key == null || !key.IsLetter) return;
        _touched[key] = now;
        _swipeKey = key;
    }

    void OnLongPress(object? sender, EventArgs e)
    {
        _longPressTimer.Stop();
        if (_state != GestureState.Pressed || _downKey == null || _downKey.Alternates.Count == 0 || _hoverKey != null) return;

        var r = KeyRect(_downKey);
        _altCellWidth = Math.Max(r.Width, 26);
        double width = _downKey.Alternates.Count * _altCellWidth + 8;
        double height = Math.Max(r.Height, 30) + 8;
        double x = Math.Clamp(r.X + r.Width / 2 - width / 2, 0, Math.Max(0, ActualWidth - width));
        _altRect = new Rect(x, r.Top - height - 6, width, height);
        _altIndex = 0;
        _state = GestureState.LongPress;
        KeyPressedDown?.Invoke(this, _downKey);
        RedrawOverlay();
    }

    void OnRepeat(object? sender, EventArgs e)
    {
        if (_state != GestureState.Repeating || _downKey == null)
        {
            _repeatTimer.Stop();
            return;
        }
        _repeatTimer.Interval = TimeSpan.FromMilliseconds(RepeatIntervalMs);
        KeyTapped?.Invoke(this, new KeyTapEventArgs(_downKey, null));
    }
}
