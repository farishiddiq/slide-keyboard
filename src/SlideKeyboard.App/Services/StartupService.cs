using System;
using Microsoft.Win32;
using SlideKeyboard.App.Settings;

namespace SlideKeyboard.App.Services;

/// <summary>"Start with Windows" via the per-user Run key (no administrator rights needed).</summary>
internal static class StartupService
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "SlideKeyboard";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(bool enable)
    {
        try
        {
            if (enable == IsEnabled()) return;
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Could not update startup registration", ex);
        }
    }
}
