using SlideKeyboard.Core.Dictionaries;

namespace SlideKeyboard.Core.Input;

/// <summary>
/// Switches between languages that share a layout (English / Indonesian) when the user keeps committing words
/// that only exist in the other language.
/// </summary>
public sealed class LanguageDetector
{
    const int Window = 3;
    readonly Queue<string?> _votes = new();

    public LanguagePack? Observe(string word, LanguagePack active, IReadOnlyList<LanguagePack> candidates)
    {
        if (candidates.Count < 2) return null;

        LanguagePack? owner = null;
        int owners = 0;
        foreach (var pack in candidates)
        {
            if (!pack.Words.Contains(word)) continue;
            owner = pack;
            owners++;
        }

        _votes.Enqueue(owners == 1 ? owner!.Code : null);
        while (_votes.Count > Window) _votes.Dequeue();

        if (_votes.Any(v => v == active.Code)) return null;
        foreach (var pack in candidates)
        {
            if (pack == active) continue;
            if (_votes.Count(v => v == pack.Code) >= 2)
            {
                _votes.Clear();
                return pack;
            }
        }
        return null;
    }

    public void Reset() => _votes.Clear();
}
