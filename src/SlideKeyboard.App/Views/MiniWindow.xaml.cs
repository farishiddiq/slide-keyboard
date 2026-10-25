using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using static SlideKeyboard.App.Interop.NativeMethods;

namespace SlideKeyboard.App.Views;

/// <summary>Small floating bubble shown while the keyboard is minimized. Tap to restore, drag to move.</summary>
public partial class MiniWindow : Window
{
    const double DragThreshold = 5;

    IntPtr _hwnd;
    bool _pressed;
    bool _dragging;
    Point _startScreen;
    RECT _startRect;

    public MiniWindow()
    {
        InitializeComponent();
        Bubble.MouseLeftButtonDown += (_, e) =>
        {
            if (!Bubble.CaptureMouse()) return;
            Begin(Bubble.PointToScreen(e.GetPosition(Bubble)));
            e.Handled = true;
        };
        Bubble.MouseMove += (_, e) =>
        {
            if (_pressed) Move(Bubble.PointToScreen(e.GetPosition(Bubble)));
        };
        Bubble.MouseLeftButtonUp += (_, e) =>
        {
            Bubble.ReleaseMouseCapture();
            End();
            e.Handled = true;
        };
        Bubble.TouchDown += (_, e) =>
        {
            if (!e.TouchDevice.Capture(Bubble)) return;
            Begin(Bubble.PointToScreen(e.GetTouchPoint(Bubble).Position));
            e.Handled = true;
        };
        Bubble.TouchMove += (_, e) =>
        {
            if (_pressed) Move(Bubble.PointToScreen(e.GetTouchPoint(Bubble).Position));
            e.Handled = true;
        };
        Bubble.TouchUp += (_, e) =>
        {
            Bubble.ReleaseTouchCapture(e.TouchDevice);
            End();
            e.Handled = true;
        };
    }

    public event EventHandler? RestoreRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        MakeFloatingToolWindow(_hwnd);
        HwndSource.FromHwnd(_hwnd)?.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg != WM_MOUSEACTIVATE) return IntPtr.Zero;
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        });
    }

    /// <summary>Shows the bubble with its bottom-right corner at the given physical-pixel point.</summary>
    internal void ShowAt(int right, int bottom)
    {
        Show();
        GetWindowRect(_hwnd, out var rect);
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        SetWindowPos(_hwnd, IntPtr.Zero, right - w, bottom - h, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    void Begin(Point screen)
    {
        _pressed = true;
        _dragging = false;
        _startScreen = screen;
        GetWindowRect(_hwnd, out _startRect);
    }

    void Move(Point screen)
    {
        double dx = screen.X - _startScreen.X, dy = screen.Y - _startScreen.Y;
        if (!_dragging && Math.Sqrt(dx * dx + dy * dy) < DragThreshold) return;
        _dragging = true;
        SetWindowPos(_hwnd, IntPtr.Zero, _startRect.Left + (int)dx, _startRect.Top + (int)dy, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    void End()
    {
        if (!_pressed) return;
        _pressed = false;
        if (!_dragging) RestoreRequested?.Invoke(this, EventArgs.Empty);
    }
}
