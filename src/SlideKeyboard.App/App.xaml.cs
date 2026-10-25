using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SlideKeyboard.App.Injection;
using SlideKeyboard.App.Services;
using SlideKeyboard.App.Settings;
using SlideKeyboard.App.Theming;
using SlideKeyboard.App.Views;
using SlideKeyboard.Core.Dictionaries;
using SlideKeyboard.Core.Input;

namespace SlideKeyboard.App;

/// <summary>
/// Composition root: loads settings and language packs, wires the engine (prediction/swipe), the Windows text
/// injector and the windows together, and owns app-level services (tray icon, focus watcher, startup).
/// </summary>
public partial class App : Application
{
    Mutex? _singleInstance;
    EventWaitHandle? _showSignal;
    RegisteredWaitHandle? _showWait;
    AppSettings _settings = new();
    KeyboardEngine? _engine;
    KeyboardWindow? _keyboard;
    MiniWindow? _mini;
    SettingsWindow? _settingsWindow;
    TrayIcon? _tray;
    FocusWatcher? _focus;
    FeedbackService? _feedback;
    readonly WindowsTextOutput _output = new();
    DispatcherTimer? _saveTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, "SlideKeyboard.SingleInstance", out bool firstInstance);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "SlideKeyboard.ShowSignal");
        if (!firstInstance)
        {
            _showSignal.Set(); // ask the running instance to show itself
            Shutdown();
            return;
        }
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(ShowKeyboard), null, Timeout.Infinite, executeOnlyOnce: false);

        DispatcherUnhandledException += (_, args) =>
        {
            ErrorLog.Write("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };

        _settings = SettingsStore.Load();

        var roots = new[] { Path.Combine(AppContext.BaseDirectory, "dictionaries"), SettingsStore.UserDictionariesDirectory };
        var packs = LanguagePackLoader.LoadAll(roots, (dir, ex) => ErrorLog.Write($"Could not load language pack {dir}", ex));
        if (packs.Count == 0)
        {
            MessageBox.Show($"No language packs were found in:\n{roots[0]}", "Slide Keyboard", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var languages = new LanguageManager(packs);
        languages.SetEnabled(_settings.EnabledLanguages);
        languages.SetActive(_settings.Language);
        languages.ActiveChanged += (_, _) =>
        {
            _settings.Language = languages.Active.Code;
            SaveSoon();
        };

        _engine = new KeyboardEngine(languages, _output, new EngineSettings(), userWords: new UserDictionary(SettingsStore.UserWordsDirectory));
        ApplyEngineSettings();
        _engine.WarmUp();

        _feedback = new FeedbackService();
        _ = _feedback.InitializeAsync();

        ThemeService.Apply(_settings.Theme);
        ThemeService.SystemThemeChanged += (_, _) =>
        {
            if (_settings.Theme == ThemeMode.System) ApplyAll();
        };

        _keyboard = new KeyboardWindow(_engine, _settings, _feedback);
        _keyboard.SettingsRequested += (_, _) => OpenSettings();
        _keyboard.MinimizeRequested += (_, _) => Minimize();
        _keyboard.ToggleRequested += (_, _) => ToggleKeyboard();
        _keyboard.SettingsChanged += (_, _) => ApplyAll();
        _keyboard.ApplySettings();

        _mini = new MiniWindow();
        _mini.RestoreRequested += (_, _) => ShowKeyboard();

        _focus = new FocusWatcher();
        _focus.FocusChanged += (_, _) => _engine.ResetContext();

        _tray = new TrayIcon(ToggleKeyboard, OpenSettings, ExitApp);

        StartupService.Apply(_settings.StartWithWindows);

        if (e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
        {
            _keyboard.Show();
            Minimize();
        }
        else
        {
            _keyboard.Show();
        }
    }

    void ApplyEngineSettings()
    {
        if (_engine == null) return;
        _output.Mode = _settings.TypingMethod;
        var s = _engine.Settings;
        s.AutoCorrect = _settings.AutoCorrect;
        s.AutoCapitalize = _settings.AutoCapitalize;
        s.Suggestions = _settings.Suggestions;
        s.SuggestionCount = _settings.SuggestionCount;
        s.DoubleSpacePeriod = _settings.DoubleSpacePeriod;
        s.AutoDetectLanguage = _settings.AutoDetectLanguage;
        s.NumberRow = _settings.Mode == KeyboardMode.Full && _settings.NumberRow;
    }

    /// <summary>Applies the current settings everywhere and schedules a save.</summary>
    void ApplyAll()
    {
        if (_engine == null || _keyboard == null) return;
        var languages = _engine.Languages;
        if (!languages.Enabled.Select(p => p.Code).SequenceEqual(_settings.EnabledLanguages, StringComparer.OrdinalIgnoreCase))
            languages.SetEnabled(_settings.EnabledLanguages);
        if (languages.Active.Code != _settings.Language) languages.SetActive(_settings.Language);

        ApplyEngineSettings();
        _engine.SettingsChanged();
        ThemeService.Apply(_settings.Theme);
        _keyboard.ApplySettings();
        _keyboard.RegisterHotkeys();
        StartupService.Apply(_settings.StartWithWindows);
        SaveSoon();
    }

    void SaveSoon()
    {
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            SettingsStore.Save(_settings);
        }, Dispatcher);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void OpenSettings()
    {
        if (_engine == null || _keyboard == null || _feedback == null) return;
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, _engine, _feedback.HapticsAvailable,
            _keyboard.LanguageHotkeyRegistered, ApplyAll, ExitApp);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    void Minimize()
    {
        if (_keyboard == null || _mini == null) return;
        var rect = _keyboard.ScreenRect;
        _keyboard.Hide();
        _mini.ShowAt(rect.Right - 24, rect.Bottom - 8);
    }

    void ShowKeyboard()
    {
        if (_keyboard == null) return;
        _mini?.Hide();
        _keyboard.Show();
        _keyboard.Topmost = false;
        _keyboard.Topmost = _settings.AlwaysOnTop;
    }

    void ToggleKeyboard()
    {
        if (_keyboard == null) return;
        if (_keyboard.IsVisible) Minimize();
        else ShowKeyboard();
    }

    void ExitApp()
    {
        SettingsStore.Save(_settings);
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _focus?.Dispose();
        _feedback?.Dispose();
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
