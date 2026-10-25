using System.Globalization;
using System.Text;
using SlideKeyboard.Core.Geometry;
using SlideKeyboard.Core.Layout;

namespace SlideKeyboard.Core.Swipe;

/// <summary>Maps letters to key centers for one letter layout, and converts words into key sequences.</summary>
public sealed class SwipeKeyMap
{
    readonly Dictionary<char, Vec2> _centers = new();
    readonly Dictionary<char, char> _aliases = new();
    readonly List<KeyValuePair<char, Vec2>> _keys;

    public SwipeKeyMap(KeyboardLayout letters, LayoutSpec spec)
    {
        foreach (var key in letters.Keys)
            if (key.IsLetter && key.Text.Length == 1)
                _centers[char.ToLowerInvariant(key.Text[0])] = key.Center;

        foreach (var (from, to) in spec.Aliases)
            if (from.Length == 1 && to.Length == 1)
                _aliases[from[0]] = to[0];

        _keys = _centers.OrderBy(kv => kv.Key).ToList();
        Signature = string.Join("|", _keys.Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key}{kv.Value.X:F2},{kv.Value.Y:F2}")));
    }

    /// <summary>Identifies the key geometry; two maps with equal signatures are interchangeable.</summary>
    public string Signature { get; }

    public IReadOnlyList<KeyValuePair<char, Vec2>> Keys => _keys;

    public bool TryGetCenter(char c, out Vec2 center)
    {
        if (TryMap(c, out char key)) return _centers.TryGetValue(key, out center);
        center = default;
        return false;
    }

    public Vec2 Center(char key) => _centers[key];

    public bool TryMap(char c, out char key)
    {
        c = char.ToLowerInvariant(c);
        if (_centers.ContainsKey(c))
        {
            key = c;
            return true;
        }
        if (_aliases.TryGetValue(c, out key) && _centers.ContainsKey(key)) return true;

        string decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        if (decomposed.Length > 0)
        {
            char baseChar = char.ToLowerInvariant(decomposed[0]);
            if (_centers.ContainsKey(baseChar))
            {
                key = baseChar;
                return true;
            }
        }
        key = default;
        return false;
    }

    static bool IsIgnorable(char c)
    {
        if (c is '\'' or '’' or '-' or 'ـ') return true;
        var category = CharUnicodeInfo.GetUnicodeCategory(c);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.Format;
    }

    /// <summary>
    /// The keys a swipe for <paramref name="word"/> passes over, with consecutive repeats collapsed
    /// ("hello" -> h e l o). Returns null when a letter has no key on this layout.
    /// </summary>
    public char[]? KeySequence(string word)
    {
        var keys = new List<char>(word.Length);
        foreach (char c in word)
        {
            if (IsIgnorable(c)) continue;
            if (!TryMap(c, out char key)) return null;
            if (keys.Count == 0 || keys[^1] != key) keys.Add(key);
        }
        return keys.Count == 0 ? null : keys.ToArray();
    }
}
