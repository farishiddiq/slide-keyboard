using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using SlideKeyboard.App.Settings;

namespace SlideKeyboard.App.Theming;

/// <summary>Keyboard color palette. Brushes are frozen so they can be shared freely by the renderer.</summary>
public sealed class Theme
{
    public required string Name { get; init; }
    public bool IsDark { get; init; }

    public required Color Background { get; init; }
    public required Color Border { get; init; }
    public required Color Key { get; init; }
    public required Color KeySpecial { get; init; }
    public required Color KeyPressed { get; init; }
    public required Color KeyShadow { get; init; }
    public required Color KeyText { get; init; }
    public required Color KeySubText { get; init; }
    public required Color Accent { get; init; }
    public required Color AccentText { get; init; }
    public required Color Trail { get; init; }
    public required Color SuggestionText { get; init; }
    public required Color SuggestionPrimary { get; init; }
    public required Color Divider { get; init; }
    public required Color Hover { get; init; }

    public static Theme Light { get; } = new()
    {
        Name = "Light",
        Background = Rgb(0xE8, 0xEC, 0xF1),
        Border = Rgb(0xD5, 0xDB, 0xE3),
        Key = Rgb(0xFF, 0xFF, 0xFF),
        KeySpecial = Rgb(0xD2, 0xD9, 0xE2),
        KeyPressed = Rgb(0xC0, 0xCA, 0xD6),
        KeyShadow = Color.FromArgb(0x30, 0x2A, 0x36, 0x48),
        KeyText = Rgb(0x1A, 0x1E, 0x24),
        KeySubText = Rgb(0x6A, 0x73, 0x80),
        Accent = Rgb(0x2F, 0x6B, 0xEB),
        AccentText = Rgb(0xFF, 0xFF, 0xFF),
        Trail = Rgb(0x2F, 0x6B, 0xEB),
        SuggestionText = Rgb(0x3B, 0x43, 0x4E),
        SuggestionPrimary = Rgb(0x10, 0x14, 0x19),
        Divider = Rgb(0xC6, 0xCE, 0xD8),
        Hover = Color.FromArgb(0x1A, 0x20, 0x2C, 0x3C),
    };

    public static Theme Dark { get; } = new()
    {
        Name = "Dark",
        IsDark = true,
        Background = Rgb(0x1C, 0x1F, 0x24),
        Border = Rgb(0x2C, 0x30, 0x37),
        Key = Rgb(0x3A, 0x3F, 0x47),
        KeySpecial = Rgb(0x2A, 0x2E, 0x35),
        KeyPressed = Rgb(0x53, 0x59, 0x63),
        KeyShadow = Color.FromArgb(0x70, 0x00, 0x00, 0x00),
        KeyText = Rgb(0xEE, 0xF1, 0xF5),
        KeySubText = Rgb(0x9A, 0xA3, 0xAE),
        Accent = Rgb(0x7C, 0xA5, 0xFF),
        AccentText = Rgb(0x0E, 0x14, 0x20),
        Trail = Rgb(0x8C, 0xB2, 0xFF),
        SuggestionText = Rgb(0xC8, 0xCF, 0xD8),
        SuggestionPrimary = Rgb(0xFF, 0xFF, 0xFF),
        Divider = Rgb(0x3A, 0x3F, 0x47),
        Hover = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF),
    };

    static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>Resolves the active theme and publishes it as application resources for XAML.</summary>
public static class ThemeService
{
    public static Theme Current { get; private set; } = Theme.Light;

    public static event EventHandler? SystemThemeChanged;

    static ThemeService()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General)
                Application.Current?.Dispatcher.BeginInvoke(() => SystemThemeChanged?.Invoke(null, EventArgs.Empty));
        };
    }

    public static Theme Apply(ThemeMode mode)
    {
        Current = mode switch
        {
            ThemeMode.Light => Theme.Light,
            ThemeMode.Dark => Theme.Dark,
            _ => SystemUsesLightTheme() ? Theme.Light : Theme.Dark,
        };

        var r = Application.Current.Resources;
        var t = Current;
        r["Kb.Background"] = Theme.Brush(t.Background);
        r["Kb.Border"] = Theme.Brush(t.Border);
        r["Kb.Key"] = Theme.Brush(t.Key);
        r["Kb.KeySpecial"] = Theme.Brush(t.KeySpecial);
        r["Kb.Text"] = Theme.Brush(t.KeyText);
        r["Kb.SubText"] = Theme.Brush(t.KeySubText);
        r["Kb.Accent"] = Theme.Brush(t.Accent);
        r["Kb.AccentText"] = Theme.Brush(t.AccentText);
        r["Kb.SuggestionText"] = Theme.Brush(t.SuggestionText);
        r["Kb.SuggestionPrimary"] = Theme.Brush(t.SuggestionPrimary);
        r["Kb.Divider"] = Theme.Brush(t.Divider);
        r["Kb.Hover"] = Theme.Brush(t.Hover);
        return Current;
    }

    static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch
        {
            return true;
        }
    }
}
