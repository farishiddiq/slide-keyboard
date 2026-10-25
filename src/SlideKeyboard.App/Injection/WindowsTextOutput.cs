using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SlideKeyboard.Core.Input;
using static SlideKeyboard.App.Interop.NativeMethods;

namespace SlideKeyboard.App.Injection;

public enum InjectionMode : int
{
    /// <summary>Post keyboard messages to the focused control; use SendInput where that cannot work.</summary>
    Automatic,

    /// <summary>Always synthesize input with SendInput (Unicode key packets).</summary>
    SendInput,
}

/// <summary>
/// Types into whichever application has keyboard focus. The keyboard window never takes focus
/// (WS_EX_NOACTIVATE), so the target application stays focused while the user clicks or swipes.
///
/// Two delivery paths:
///  - Posted messages (default): each character is posted as WM_CHAR to the focused control, and editing keys as the
///    WM_KEYDOWN / WM_CHAR / WM_KEYUP sequence a real key produces. Every message carries its own character, so a
///    busy target cannot mix characters up, and posted messages stay in order. Works with classic Win32 controls,
///    RichEdit (Notepad), Office, Chromium and Electron apps.
///  - SendInput: Unicode key packets (KEYEVENTF_UNICODE). Needed for UWP/WinUI input surfaces that ignore posted
///    messages. Windows resolves a packet's character when the target translates the message, so when a slow target
///    has several packets queued they can all come out as the last character ("aaaaaa" for "terima"); this path is
///    therefore only the fallback.
/// </summary>
internal sealed class WindowsTextOutput : ITextOutput
{
    const uint WM_KEYDOWN = 0x0100;
    const uint WM_KEYUP = 0x0101;
    const uint WM_CHAR = 0x0102;

    /// <summary>Marks injected events so they can be recognized as ours.</summary>
    static readonly IntPtr Signature = new(0x534B4244);

    /// <summary>Focus windows of modern (XAML/CoreWindow) input stacks that only accept real input.</summary>
    static readonly HashSet<string> SendInputOnlyClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows.UI.Input.InputSite.WindowClass",
        "Windows.UI.Core.CoreWindow",
        "ApplicationFrameInputSinkWindow",
        "ApplicationFrameWindow",
        "CASCADIA_HOSTING_WINDOW_CLASS",
    };

    public InjectionMode Mode { get; set; } = InjectionMode.Automatic;

    public void SendText(string text)
    {
        if (text.Length == 0) return;
        var target = PostTarget();
        if (target != IntPtr.Zero)
        {
            int sent = 0;
            foreach (char c in text)
            {
                bool ok = c switch
                {
                    '\r' => true,
                    '\n' => PostKey(target, VK_RETURN, '\r'),
                    '\t' => PostKey(target, VK_TAB, '\t'),
                    _ => PostMessage(target, WM_CHAR, (IntPtr)c, (IntPtr)1),
                };
                if (!ok) break;
                sent++;
            }
            if (sent == text.Length) return;
            text = text[sent..]; // posting failed (e.g. elevated target); try the rest with SendInput
        }
        SendInputText(text);
    }

    public void SendBackspace(int count)
    {
        if (count <= 0) return;
        var target = PostTarget();
        if (target != IntPtr.Zero)
        {
            int sent = 0;
            while (sent < count && PostKey(target, VK_BACK, '\b')) sent++;
            count -= sent;
            if (count == 0) return;
        }
        var inputs = new List<INPUT>(count * 2);
        for (int i = 0; i < count; i++) AddVirtualKey(inputs, VK_BACK);
        Send(inputs);
    }

    public void SendKey(SpecialKey key)
    {
        (ushort vk, char ch, bool extended) = key switch
        {
            SpecialKey.Enter => (VK_RETURN, '\r', false),
            SpecialKey.Tab => (VK_TAB, '\t', false),
            SpecialKey.Left => (VK_LEFT, '\0', true),
            SpecialKey.Right => (VK_RIGHT, '\0', true),
            _ => (VK_DELETE, '\0', true),
        };
        var target = PostTarget();
        if (target != IntPtr.Zero && PostKey(target, vk, ch, extended)) return;
        var inputs = new List<INPUT>(2);
        AddVirtualKey(inputs, vk, extended);
        Send(inputs);
    }

    // ------------------------------------------------------------------ posted messages

    /// <summary>The focused control of the foreground application, or zero when SendInput must be used.</summary>
    IntPtr PostTarget()
    {
        if (Mode == InjectionMode.SendInput) return IntPtr.Zero;
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return IntPtr.Zero;
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(GetWindowThreadProcessId(foreground, IntPtr.Zero), ref info) || info.hwndFocus == IntPtr.Zero)
            return IntPtr.Zero;
        var name = new StringBuilder(128);
        GetClassName(info.hwndFocus, name, name.Capacity);
        return SendInputOnlyClasses.Contains(name.ToString()) ? IntPtr.Zero : info.hwndFocus;
    }

    /// <summary>Posts the message sequence of one key press: WM_KEYDOWN, WM_CHAR (if the key types one), WM_KEYUP.</summary>
    static bool PostKey(IntPtr target, ushort vk, char ch, bool extended = false)
    {
        long scan = MapVirtualKey(vk, 0);
        long down = 1 | (scan << 16) | (extended ? 1L << 24 : 0);
        long up = down | (1L << 30) | (1L << 31);
        if (!PostMessage(target, WM_KEYDOWN, (IntPtr)vk, (IntPtr)down)) return false;
        if (ch != '\0') PostMessage(target, WM_CHAR, (IntPtr)ch, (IntPtr)down);
        PostMessage(target, WM_KEYUP, (IntPtr)vk, (IntPtr)up);
        return true;
    }

    // ------------------------------------------------------------------ SendInput

    static void SendInputText(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\r':
                    continue;
                case '\n':
                    AddVirtualKey(inputs, VK_RETURN);
                    break;
                case '\t':
                    AddVirtualKey(inputs, VK_TAB);
                    break;
                default:
                    inputs.Add(Keyboard(0, c, KEYEVENTF_UNICODE));
                    inputs.Add(Keyboard(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
                    break;
            }
        }
        Send(inputs);
    }

    static void AddVirtualKey(List<INPUT> inputs, ushort vk, bool extended = false)
    {
        ushort scan = (ushort)MapVirtualKey(vk, 0);
        uint flags = extended ? KEYEVENTF_EXTENDEDKEY : 0;
        inputs.Add(Keyboard(vk, scan, flags));
        inputs.Add(Keyboard(vk, scan, flags | KEYEVENTF_KEYUP));
    }

    static INPUT Keyboard(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = Signature },
        },
    };

    static void Send(List<INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        var array = inputs.ToArray();
        uint sent = SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());
        if (sent != array.Length)
            Debug.WriteLine($"SendInput delivered {sent}/{array.Length} events (error {Marshal.GetLastWin32Error()}). " +
                            "The target window may be elevated.");
    }

}
