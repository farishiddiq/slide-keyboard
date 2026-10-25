using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlideKeyboard.App.Settings;

public enum ThemeMode { System, Light, Dark }

public enum KeyboardMode { Full, Compact }

public enum KeyboardPosition { BottomCenter, BottomLeft, BottomRight, Remember }

/// <summary>User preferences. Contains no typed text.</summary>
public sealed class AppSettings
{
    public string Language { get; set; } = "en";
    public List<string> EnabledLanguages { get; set; } = new() { "id", "en", "ar" };

    public KeyboardMode Mode { get; set; } = KeyboardMode.Full;
    public double KeyboardScale { get; set; } = 1.0;
    public double KeyHeightScale { get; set; } = 1.0;
    public double Opacity { get; set; } = 1.0;
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    public bool SwipeTyping { get; set; } = true;
    public bool SwipeTrail { get; set; } = true;
    public bool KeyPopup { get; set; } = true;
    public bool Suggestions { get; set; } = true;
    public int SuggestionCount { get; set; } = 3;
    public bool AutoCorrect { get; set; } = true;
    public bool AutoCapitalize { get; set; } = true;
    public bool AutoDetectLanguage { get; set; } = true;
    public bool DoubleSpacePeriod { get; set; } = true;
    public bool NumberRow { get; set; } = true;

    public bool Sound { get; set; }
    public bool Haptics { get; set; } = true;

    public bool AlwaysOnTop { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool LanguageHotkey { get; set; } = true;

    /// <summary>Troubleshooting: log swipes (and the words they produced) to gestures.log. Contains typed words.</summary>
    public bool RecordSwipes { get; set; }
    public SlideKeyboard.App.Injection.InjectionMode TypingMethod { get; set; } = SlideKeyboard.App.Injection.InjectionMode.Automatic;

    public KeyboardPosition Position { get; set; } = KeyboardPosition.BottomCenter;

    /// <summary>Saved window position in physical pixels (used with <see cref="KeyboardPosition.Remember"/>).</summary>
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

public static class SettingsStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlideKeyboard");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    /// <summary>User-installed or updated language packs; they override the bundled ones with the same code.</summary>
    public static string UserDictionariesDirectory => Path.Combine(Directory, "dictionaries");

    /// <summary>Personal dictionary: words the user saved, one file per language.</summary>
    public static string UserWordsDirectory => Path.Combine(Directory, "userwords");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Could not read settings", ex);
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Could not save settings", ex);
        }
    }
}

/// <summary>Records application errors (never typed text) to %APPDATA%\SlideKeyboard\error.log.</summary>
public static class ErrorLog
{
    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            System.IO.Directory.CreateDirectory(SettingsStore.Directory);
            File.AppendAllText(Path.Combine(SettingsStore.Directory, "error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{(ex != null ? Environment.NewLine + ex : "")}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never take the keyboard down.
        }
    }
}
