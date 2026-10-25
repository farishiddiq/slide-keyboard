using System.Globalization;
using System.Text;
using SlideKeyboard.Core.Text;

namespace SlideKeyboard.Core.Dictionaries;

public sealed class WordEntry
{
    internal WordEntry(string word, string key, int rank, double logPrior)
    {
        Word = word;
        Key = key;
        Rank = rank;
        LogPrior = logPrior;
    }

    /// <summary>Display form, e.g. "I", "Jakarta", "إسلام".</summary>
    public string Word { get; }

    /// <summary>Normalized lookup key (see <see cref="TextUtil.NormalizeKey"/>).</summary>
    public string Key { get; }

    public int Rank { get; }

    /// <summary>log P(word) relative to the most frequent word (0 for the top word, negative below).</summary>
    public double LogPrior { get; }

    public override string ToString() => Word;
}

/// <summary>
/// Frequency-ranked word list. File format: one word per line, most frequent first, optionally followed by a
/// whitespace-separated count. Lines starting with # are comments.
/// </summary>
public sealed class WordList
{
    const double RankSmoothing = 2.0;
    const double MinLogPrior = -14;

    readonly WordEntry[] _byRank;
    readonly WordEntry[] _sorted;
    readonly Dictionary<string, WordEntry> _byKey;

    WordList(List<WordEntry> entries)
    {
        _byRank = entries.ToArray();
        _sorted = entries.OrderBy(e => e.Key, StringComparer.Ordinal).ThenBy(e => e.Rank).ToArray();
        _byKey = new Dictionary<string, WordEntry>(StringComparer.Ordinal);
        foreach (var e in _byRank) _byKey.TryAdd(e.Key, e);
    }

    public static WordList Empty { get; } = new(new List<WordEntry>());

    public int Count => _byRank.Length;

    /// <summary>Entries ordered by frequency rank.</summary>
    public IReadOnlyList<WordEntry> Entries => _byRank;

    public static WordList Parse(IEnumerable<string> lines)
    {
        var raw = new List<(string Word, double? Count)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var trimmed = line.Trim().TrimStart('﻿');
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string word = parts[0].Normalize(NormalizationForm.FormC);
            double? count = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var c) && c > 0 ? c : null;
            if (seen.Add(word)) raw.Add((word, count));
        }

        bool hasCounts = raw.Count > 0 && raw.All(r => r.Count.HasValue);
        if (hasCounts) raw = raw.OrderByDescending(r => r.Count!.Value).ToList();
        double max = hasCounts ? raw[0].Count!.Value : 1;

        var entries = new List<WordEntry>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            double logPrior = hasCounts
                ? Math.Log(raw[i].Count!.Value / max)
                : Math.Log((1 + RankSmoothing) / (i + 1 + RankSmoothing));
            entries.Add(new WordEntry(raw[i].Word, TextUtil.NormalizeKey(raw[i].Word), i, Math.Max(logPrior, MinLogPrior)));
        }
        return new WordList(entries);
    }

    /// <summary>
    /// A copy of this list with extra words (e.g. the user's personal dictionary) appended at the given prior.
    /// Words already in the list are left as they are.
    /// </summary>
    public WordList WithAdded(IEnumerable<string> words, double logPrior)
    {
        var entries = new List<WordEntry>(_byRank);
        var keys = new HashSet<string>(_byKey.Keys, StringComparer.Ordinal);
        foreach (var raw in words)
        {
            string word = raw.Trim().Normalize(NormalizationForm.FormC);
            string key = TextUtil.NormalizeKey(word);
            if (key.Length == 0 || !keys.Add(key)) continue;
            entries.Add(new WordEntry(word, key, entries.Count, logPrior));
        }
        return entries.Count == _byRank.Length ? this : new WordList(entries);
    }

    public WordEntry? Lookup(string word) =>
        _byKey.TryGetValue(TextUtil.NormalizeKey(word), out var entry) ? entry : null;

    public bool Contains(string word) => Lookup(word) != null;

    /// <summary>All entries whose normalized key starts with <paramref name="normalizedPrefix"/>.</summary>
    public IEnumerable<WordEntry> WithPrefix(string normalizedPrefix)
    {
        int lo = 0, hi = _sorted.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (string.CompareOrdinal(_sorted[mid].Key, normalizedPrefix) < 0) lo = mid + 1;
            else hi = mid;
        }
        for (int i = lo; i < _sorted.Length && _sorted[i].Key.StartsWith(normalizedPrefix, StringComparison.Ordinal); i++)
            yield return _sorted[i];
    }
}
