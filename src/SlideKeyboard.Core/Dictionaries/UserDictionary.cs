using System.Text;
using SlideKeyboard.Core.Text;

namespace SlideKeyboard.Core.Dictionaries;

/// <summary>
/// Words the user explicitly saved ("Add to dictionary"), one file per language: &lt;directory&gt;/&lt;code&gt;.txt.
/// Only words the user chose to save are stored; nothing is learned automatically.
/// </summary>
public sealed class UserDictionary
{
    public const int MaxWordLength = 48;

    readonly string? _directory;
    readonly Dictionary<string, List<string>> _words = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="directory">Folder for the word files, or null to keep the dictionary in memory only (tests).</param>
    public UserDictionary(string? directory)
    {
        _directory = directory;
        if (directory == null || !Directory.Exists(directory)) return;
        foreach (var file in Directory.GetFiles(directory, "*.txt"))
        {
            var list = new List<string>();
            foreach (var line in File.ReadLines(file))
            {
                var word = line.Trim().TrimStart('﻿');
                if (word.Length > 0 && !word.StartsWith('#') && !list.Contains(word)) list.Add(word);
            }
            _words[Path.GetFileNameWithoutExtension(file)] = list;
        }
    }

    public IReadOnlyList<string> Words(string code) =>
        _words.TryGetValue(code, out var list) ? list : Array.Empty<string>();

    /// <summary>True when the text can be saved as a word (letters, optionally with ' or -, no spaces).</summary>
    public static bool IsValidWord(string word)
    {
        word = word.Trim();
        if (word.Length == 0 || word.Length > MaxWordLength) return false;
        bool hasLetter = false;
        foreach (char c in word)
        {
            if (char.IsWhiteSpace(c)) return false;
            if (char.IsLetter(c)) hasLetter = true;
            else if (!TextUtil.IsWordChar(c) && c != '-') return false;
        }
        return hasLetter;
    }

    /// <summary>Adds a word; false when it is invalid or already saved.</summary>
    public bool Add(string code, string word)
    {
        word = word.Trim().Normalize(NormalizationForm.FormC);
        if (!IsValidWord(word)) return false;
        if (!_words.TryGetValue(code, out var list)) _words[code] = list = new List<string>();
        string key = TextUtil.NormalizeKey(word);
        if (list.Any(w => TextUtil.NormalizeKey(w) == key)) return false;
        list.Add(word);
        Save(code);
        return true;
    }

    public bool Remove(string code, string word)
    {
        if (!_words.TryGetValue(code, out var list) || !list.Remove(word)) return false;
        Save(code);
        return true;
    }

    void Save(string code)
    {
        if (_directory == null) return;
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, code + ".txt");
        string temp = path + ".tmp";
        File.WriteAllLines(temp, _words[code], new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
