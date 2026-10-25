using System.Collections.Concurrent;
using SlideKeyboard.Core.Dictionaries;
using SlideKeyboard.Core.Swipe;
using SlideKeyboard.Core.Text;

namespace SlideKeyboard.Core.Prediction;

public enum SuggestionKind
{
    /// <summary>Exactly what the user typed (shown in quotes when it is not a dictionary word).</summary>
    Literal,
    Completion,
    Correction,
    SwipeAlternative,
    NextWord,

    /// <summary>Offer to save the word the user just kept to the personal dictionary.</summary>
    AddToDictionary,
}

public sealed record Suggestion(string Text, SuggestionKind Kind, bool IsPrimary);

public sealed record Prediction(WordEntry Entry, double Score);

/// <summary>
/// Word completion, typo correction and next-word prediction. Runs entirely locally; the only state it keeps is an
/// in-memory set of words used in this session (never written to disk).
/// </summary>
public sealed class PredictionEngine
{
    const double SessionBoostValue = 1.0;
    const int MaxSessionWords = 400;

    readonly Func<LanguagePack, SwipeKeyMap?> _keyMaps;
    readonly ConcurrentDictionary<string, byte> _sessionWords = new(StringComparer.Ordinal);

    public PredictionEngine(Func<LanguagePack, SwipeKeyMap?>? keyMaps = null) => _keyMaps = keyMaps ?? (_ => null);

    public void NoteUsed(LanguagePack pack, string word)
    {
        if (_sessionWords.Count > MaxSessionWords) _sessionWords.Clear();
        _sessionWords[pack.Code + "|" + TextUtil.NormalizeKey(word)] = 0;
    }

    public void ClearSession() => _sessionWords.Clear();

    public double SessionBoost(LanguagePack pack, WordEntry entry) =>
        _sessionWords.ContainsKey(pack.Code + "|" + entry.Key) ? SessionBoostValue : 0;

    public IReadOnlyList<Prediction> Complete(LanguagePack pack, string prefix, string? previousWord, int max)
    {
        string key = TextUtil.NormalizeKey(prefix);
        if (key.Length == 0 || max <= 0) return Array.Empty<Prediction>();
        string? previous = previousWord == null ? null : TextUtil.NormalizeKey(previousWord);

        var top = new List<Prediction>(max + 1);
        foreach (var entry in pack.Words.WithPrefix(key))
        {
            double score = entry.LogPrior + pack.Bigrams.Bonus(previous, entry.Key) + SessionBoost(pack, entry);
            if (entry.Key.Length == key.Length) score += 0.5;
            InsertTop(top, new Prediction(entry, score), max);
        }
        return top;
    }

    /// <summary>
    /// Best correction for a word the user finished typing, or null when it should be left alone. Words that exist
    /// in the dictionary are only corrected for capitalization/diacritics ("i" -> "I", "cafe" -> "café").
    /// </summary>
    public Prediction? Correct(LanguagePack pack, string typed, string? previousWord)
    {
        string key = TextUtil.NormalizeKey(typed);
        if (key.Length == 0 || !key.Any(char.IsLetter) || key.Any(char.IsDigit)) return null;

        var exact = pack.Words.Lookup(typed);
        if (exact != null)
            return exact.Word != typed && !TextUtil.HasUpper(typed) ? new Prediction(exact, 0) : null;

        if (TextUtil.TextElementCount(key) < 2) return null;

        string? previous = previousWord == null ? null : TextUtil.NormalizeKey(previousWord);
        double maxCost = key.Length <= 3 ? 1.0 : key.Length <= 6 ? 1.6 : 2.2;
        var keys = _keyMaps(pack);

        Prediction? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var entry in pack.Words.Entries)
        {
            if (Math.Abs(entry.Key.Length - key.Length) * 0.5 > maxCost) continue;
            double cost = EditDistance.Weighted(key, entry.Key, keys, maxCost);
            if (cost > maxCost) continue;
            double score = -3.0 * cost + entry.LogPrior + pack.Bigrams.Bonus(previous, entry.Key) + SessionBoost(pack, entry);
            if (score > bestScore)
            {
                bestScore = score;
                best = new Prediction(entry, score);
            }
        }
        return best;
    }

    /// <summary>Likely next words after <paramref name="previousWord"/> (or common sentence starters).</summary>
    public IReadOnlyList<Prediction> Next(LanguagePack pack, string? previousWord, int max)
    {
        var results = new List<Prediction>(max + 1);
        if (max <= 0) return results;
        string? previous = previousWord == null ? null : TextUtil.NormalizeKey(previousWord);

        foreach (var (next, bonus) in pack.Bigrams.Following(previous))
            if (pack.Words.Lookup(next) is { } entry)
                InsertTop(results, new Prediction(entry, bonus + entry.LogPrior + SessionBoost(pack, entry)), max);

        for (int i = 0; results.Count < max && i < pack.Words.Count && i < max * 4; i++)
        {
            var entry = pack.Words.Entries[i];
            if (results.All(r => r.Entry.Key != entry.Key))
                results.Add(new Prediction(entry, entry.LogPrior - 10));
        }
        return results;
    }

    static void InsertTop(List<Prediction> list, Prediction item, int max)
    {
        int i = list.Count;
        while (i > 0 && list[i - 1].Score < item.Score) i--;
        if (i >= max) return;
        list.Insert(i, item);
        if (list.Count > max) list.RemoveAt(list.Count - 1);
    }
}
