using System.Globalization;
using SlideKeyboard.Core.Text;

namespace SlideKeyboard.Core.Dictionaries;

/// <summary>
/// Word-pair context model (bigrams.txt). Format per line: "previous next [weight]" where weight defaults to 1.
/// </summary>
public sealed class BigramTable
{
    public const double DefaultBonus = 2.0;

    readonly Dictionary<string, Dictionary<string, double>> _map = new(StringComparer.Ordinal);

    public int Count { get; private set; }

    public static BigramTable Parse(IEnumerable<string> lines)
    {
        var table = new BigramTable();
        foreach (var line in lines)
        {
            var trimmed = line.Trim().TrimStart('﻿');
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            double weight = parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 1.0;
            table.Add(TextUtil.NormalizeKey(parts[0]), TextUtil.NormalizeKey(parts[1]), DefaultBonus * weight);
        }
        return table;
    }

    void Add(string previous, string next, double bonus)
    {
        if (!_map.TryGetValue(previous, out var followers))
            _map[previous] = followers = new Dictionary<string, double>(StringComparer.Ordinal);
        if (!followers.ContainsKey(next)) Count++;
        followers[next] = Math.Max(followers.GetValueOrDefault(next), bonus);
    }

    /// <summary>Log-score bonus for <paramref name="key"/> following <paramref name="previousKey"/> (both normalized).</summary>
    public double Bonus(string? previousKey, string key) =>
        previousKey != null && _map.TryGetValue(previousKey, out var followers) && followers.TryGetValue(key, out var bonus) ? bonus : 0;

    public IEnumerable<KeyValuePair<string, double>> Following(string? previousKey) =>
        previousKey != null && _map.TryGetValue(previousKey, out var followers)
            ? followers
            : Enumerable.Empty<KeyValuePair<string, double>>();
}
