using System;
using SlideKeyboard.App.Interop;
using static SlideKeyboard.App.Interop.NativeMethods;

namespace SlideKeyboard.App.Services;

/// <summary>
/// Raises <see cref="FocusChanged"/> when another window or control receives focus, so the typing session can
/// forget the text it typed (its context no longer applies). Only focus events are observed, never keystrokes.
/// </summary>
internal sealed class FocusWatcher : IDisposable
{
    readonly WinEventDelegate _callback;
    readonly IntPtr _foregroundHook;
    readonly IntPtr _focusHook;
    (IntPtr Hwnd, int Object, int Child) _last;

    public FocusWatcher()
    {
        _callback = OnEvent;
        const uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0, flags);
        _focusHook = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS, IntPtr.Zero, _callback, 0, 0, flags);
    }

    public event EventHandler? FocusChanged;

    void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero) return;
        var current = (hwnd, idObject, idChild);
        if (current == _last) return;
        _last = current;
        FocusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        if (_focusHook != IntPtr.Zero) UnhookWinEvent(_focusHook);
    }
}
