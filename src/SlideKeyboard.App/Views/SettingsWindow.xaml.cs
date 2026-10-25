using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SlideKeyboard.App.Settings;
using SlideKeyboard.Core.Dictionaries;
using SlideKeyboard.Core.Input;

namespace SlideKeyboard.App.Views;

public partial class SettingsWindow : Window
{
    sealed record Option(string Label, object Value);

    readonly AppSettings _settings;
    readonly LanguageManager _languages;
    readonly KeyboardEngine _engine;
    readonly Action _changed;
    readonly Action _exit;
    bool _loading = true;

    internal SettingsWindow(AppSettings settings, KeyboardEngine engine, bool hapticsAvailable, bool hotkeyRegistered, Action changed, Action exit)
    {
        var languages = engine.Languages;
        _settings = settings;
        _engine = engine;
        _languages = languages;
        _changed = changed;
        _exit = exit;
        InitializeComponent();

        ThemeCombo.ItemsSource = new[]
        {
            new Option("Follow Windows", ThemeMode.System),
            new Option("Light", ThemeMode.Light),
            new Option("Dark", ThemeMode.Dark),
        };
        ModeCombo.ItemsSource = new[]
        {
            new Option("Full keyboard", KeyboardMode.Full),
            new Option("Compact", KeyboardMode.Compact),
        };
        PositionCombo.ItemsSource = new[]
        {
            new Option("Bottom center", KeyboardPosition.BottomCenter),
            new Option("Bottom left", KeyboardPosition.BottomLeft),
            new Option("Bottom right", KeyboardPosition.BottomRight),
            new Option("Where I last moved it", KeyboardPosition.Remember),
        };
        SuggestionCountCombo.ItemsSource = new[] { new Option("3", 3), new Option("5", 5) };
        TypingMethodCombo.ItemsSource = new[]
        {
            new Option("Automatic (recommended)", Injection.InjectionMode.Automatic),
            new Option("Simulated keystrokes", Injection.InjectionMode.SendInput),
        };

        LanguageCombo.ItemsSource = languages.All.Select(p => new Option($"{p.NativeName} ({p.ShortLabel})", p.Code)).ToList();
        LanguageCombo.SelectedValue = languages.Active.Code;
        BuildEnabledLanguages();

        UserWordsLanguageCombo.ItemsSource = languages.All.Select(p => new Option($"{p.NativeName} ({p.ShortLabel})", p.Code)).ToList();
        UserWordsLanguageCombo.SelectedValue = languages.Active.Code;
        RefreshUserWords();

        HapticsNote.Visibility = hapticsAvailable ? Visibility.Collapsed : Visibility.Visible;
        HapticsCheck.IsEnabled = hapticsAvailable;
        HotkeyWarning.Visibility = settings.LanguageHotkey && !hotkeyRegistered ? Visibility.Visible : Visibility.Collapsed;

        DataContext = settings;
        _loading = false;
    }

    void BuildEnabledLanguages()
    {
        EnabledLanguagesPanel.Children.Clear();
        foreach (var pack in _languages.All)
        {
            var box = new CheckBox
            {
                Content = $"{pack.ShortLabel}  {pack.Name}",
                Tag = pack.Code,
                Margin = new Thickness(0, 6, 18, 6),
                IsChecked = _settings.EnabledLanguages.Contains(pack.Code, StringComparer.OrdinalIgnoreCase),
            };
            box.Checked += OnEnabledLanguageChanged;
            box.Unchecked += OnEnabledLanguageChanged;
            EnabledLanguagesPanel.Children.Add(box);
        }
    }

    void OnEnabledLanguageChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var boxes = EnabledLanguagesPanel.Children.OfType<CheckBox>().ToList();
        var codes = boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
        if (codes.Count == 0 && sender is CheckBox box)
        {
            box.IsChecked = true; // at least one language must stay enabled
            return;
        }
        // Keep the user's existing cycle order and append newly enabled languages.
        var ordered = _settings.EnabledLanguages.Where(c => codes.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        ordered.AddRange(codes.Where(c => !ordered.Contains(c, StringComparer.OrdinalIgnoreCase)));
        _settings.EnabledLanguages = ordered;
        _changed();
    }

    void OnLanguageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedValue is not string code) return;
        if (!_settings.EnabledLanguages.Contains(code, StringComparer.OrdinalIgnoreCase))
        {
            _settings.EnabledLanguages = new List<string>(_settings.EnabledLanguages) { code };
            _loading = true;
            BuildEnabledLanguages();
            _loading = false;
        }
        _settings.Language = code;
        _changed();
    }

    void OnSourceUpdated(object? sender, DataTransferEventArgs e)
    {
        if (!_loading) _changed();
    }

    void OnDictionariesClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsStore.UserDictionariesDirectory);
        var bundled = Path.Combine(AppContext.BaseDirectory, "dictionaries");
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{bundled}\"") { UseShellExecute = true });
        MessageBox.Show(this,
            "Bundled language packs are in the folder that just opened.\n\n" +
            "To add or update a language without touching the app, copy a pack folder (manifest.json, layout.json, " +
            $"words.txt, bigrams.txt) into:\n{SettingsStore.UserDictionariesDirectory}\n\nThen restart the keyboard.",
            "Language packs", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ------------------------------------------------------------------ personal dictionary

    string UserWordsLanguage => UserWordsLanguageCombo.SelectedValue as string ?? _languages.Active.Code;

    void RefreshUserWords()
    {
        var words = _engine.UserWords.Words(UserWordsLanguage).OrderBy(w => w, StringComparer.CurrentCultureIgnoreCase).ToList();
        UserWordsList.ItemsSource = words;
        UserWordsEmpty.Visibility = words.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnUserWordsLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) RefreshUserWords();
    }

    void OnAddUserWord(object sender, RoutedEventArgs e)
    {
        var word = NewWordBox.Text.Trim();
        if (!UserDictionary.IsValidWord(word))
        {
            UserWordsStatus.Text = "Enter a single word (letters only, no spaces).";
            return;
        }
        bool added = _engine.AddUserWord(word, UserWordsLanguage);
        UserWordsStatus.Text = added ? $"Saved \u201C{word}\u201D." : $"\u201C{word}\u201D is already saved.";
        NewWordBox.Clear();
        RefreshUserWords();
    }

    void OnNewWordKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) OnAddUserWord(sender, e);
    }

    void OnRemoveUserWord(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string word } && _engine.RemoveUserWord(word, UserWordsLanguage))
        {
            UserWordsStatus.Text = $"Removed \u201C{word}\u201D.";
            RefreshUserWords();
        }
    }

    void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    void OnExitClick(object sender, RoutedEventArgs e)
    {
        Close();
        _exit();
    }
}
