using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SlideKeyboard.App.Settings;
using Windows.Devices.Haptics;
using static SlideKeyboard.App.Interop.NativeMethods;

namespace SlideKeyboard.App.Services;

/// <summary>Key click sound and haptic feedback.</summary>
internal sealed class FeedbackService : IDisposable
{
    readonly byte[] _click = BuildClick();
    GCHandle _clickHandle;
    SimpleHapticsController? _haptics;
    SimpleHapticsControllerFeedback? _hapticClick;

    public FeedbackService() => _clickHandle = GCHandle.Alloc(_click, GCHandleType.Pinned);

    /// <summary>True when Windows reports a vibration device (some tablets); most desktops have none.</summary>
    public bool HapticsAvailable => _haptics != null && _hapticClick != null;

    public async Task InitializeAsync()
    {
        try
        {
            var access = await VibrationDevice.RequestAccessAsync();
            if (access != VibrationAccessStatus.Allowed) return;
            var device = await VibrationDevice.GetDefaultAsync();
            if (device == null) return;
            _haptics = device.SimpleHapticsController;
            _hapticClick = _haptics.SupportedFeedback.FirstOrDefault(f => f.Waveform == KnownSimpleHapticsControllerWaveforms.Click)
                           ?? _haptics.SupportedFeedback.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _haptics = null;
            ErrorLog.Write("Haptics unavailable", ex);
        }
    }

    public void KeyPress(bool sound, bool haptics)
    {
        if (sound) PlaySound(_clickHandle.AddrOfPinnedObject(), IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        if (haptics && _haptics != null && _hapticClick != null)
        {
            try { _haptics.SendHapticFeedback(_hapticClick); }
            catch { /* device went away */ }
        }
    }

    /// <summary>A short, soft 16-bit mono click synthesized in memory (no sound files to ship).</summary>
    static byte[] BuildClick()
    {
        const int rate = 44100;
        int samples = rate * 22 / 1000;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + samples * 2);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(samples * 2);
        var random = new Random(7);
        for (int i = 0; i < samples; i++)
        {
            double t = i / (double)rate;
            double envelope = Math.Exp(-t * 260);
            double tone = Math.Sin(2 * Math.PI * 1900 * t) * 0.55 + Math.Sin(2 * Math.PI * 900 * t) * 0.3;
            double noise = (random.NextDouble() * 2 - 1) * 0.15;
            w.Write((short)(Math.Clamp((tone + noise) * envelope, -1, 1) * 9000));
        }
        return ms.ToArray();
    }

    public void Dispose()
    {
        if (_clickHandle.IsAllocated) _clickHandle.Free();
    }
}
