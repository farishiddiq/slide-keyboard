// Builds dictionaries/<lang>/words.txt from FrequencyWords lists plus the hand-written starter.txt.
//
// Frequency lists: https://github.com/hermitdave/FrequencyWords (content/2018/<lang>/<lang>_full.txt or _50k.txt),
// derived from OpenSubtitles 2018, licensed CC BY-SA 4.0. Download them, then run:
//
//   dotnet run --project tools/DictionaryBuilder -- <folder with the lists> [dictionaries folder]
//
// Per language:
//  1. keep tokens made only of that language's letters, drop a short list of offensive words;
//  2. the most frequent words form the core vocabulary;
//  3. rarer words are added only when they are a core word with regular affixes (Indonesian me-/di-/ber-...
//     -kan/-an/-nya, English -s/-ed/-ing..., Arabic ال/و/ب... ه/ها/ات...), which keeps inflected forms and drops
//     the subtitle noise (names, typos, foreign words) that makes swipes ambiguous;
//  4. Indonesian drops words that are clearly English (they stay reachable through the English pack);
//  5. starter.txt is merged: its capitalization wins ("I", "Jakarta") and words missing from the frequency list
//     (English contractions, which the source splits) get a count matching their starter rank.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SlideKeyboard.Core.Text;

var source = args.Length > 0 ? args[0] : throw new ArgumentException("usage: DictionaryBuilder <source folder> [dictionaries folder]");
var root = args.Length > 1 ? args[1] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "dictionaries"));

var configs = new[]
{
    new LangConfig("en", @"^[a-z]+('[a-z]+)?$", Core: 40000, Total: 90000, MinTailCount: 3,
        Singles: new[] { "a", "i" },
        Block: new[] { "fuck", "fucking", "fucked", "fucker", "fuckin", "motherfucker", "motherfuckers", "shit", "shitty", "bullshit", "bitch", "bitches", "cunt", "cock", "dick", "dicks", "pussy", "whore", "slut", "asshole", "assholes", "nigger", "nigga", "niggas", "faggot", "fag", "retard", "retarded" }),
    new LangConfig("id", @"^[a-z]+(-[a-z]+)?$", Core: 40000, Total: 110000, MinTailCount: 3,
        Singles: Array.Empty<string>(),
        Block: new[] { "kontol", "memek", "ngentot", "jancuk", "pepek", "titit", "ngewe" }),
    new LangConfig("ar", @"^[ء-ي]{2,}$", Core: 50000, Total: 130000, MinTailCount: 3,
        Singles: Array.Empty<string>(), Block: Array.Empty<string>()),
};

var relativeByLang = new Dictionary<string, Dictionary<string, double>>();
foreach (var cfg in configs)
{
    var file = new[] { "_full.txt", "_50k.txt" }.Select(s => Path.Combine(source, cfg.Code + s)).FirstOrDefault(File.Exists);
    if (file == null)
    {
        Console.WriteLine($"{cfg.Code}: no source list, skipped");
        continue;
    }
    var dir = Path.Combine(root, cfg.Code);
    var pattern = new Regex(cfg.Pattern, RegexOptions.Compiled);
    var block = cfg.Block.ToHashSet();

    // 1. clean tokens, in frequency order
    var counts = new Dictionary<string, double>(StringComparer.Ordinal);
    var order = new List<string>();
    foreach (var line in File.ReadLines(file, Encoding.UTF8))
    {
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var count)) continue;
        var word = Normalize(parts[0], cfg.Code);
        if (!pattern.IsMatch(word) || block.Contains(word)) continue;
        if (word.Length == 1 && !cfg.Singles.Contains(word)) continue;
        if (counts.TryGetValue(word, out var existing)) { counts[word] = existing + count; continue; }
        counts[word] = count;
        order.Add(word);
    }

    double total = counts.Values.Sum();
    var relative = counts.ToDictionary(kv => kv.Key, kv => kv.Value / total, StringComparer.Ordinal);
    relativeByLang[cfg.Code] = relative;

    var starter = ReadStarter(Path.Combine(dir, "starter.txt"), cfg.Code);
    var starterKeys = starter.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

    // 2 + 3. core vocabulary, then affixed forms of core words
    var core = order.Take(cfg.Core).ToList();
    var coreSet = core.Concat(starterKeys).ToHashSet(StringComparer.Ordinal);
    var kept = new List<string>(core);
    int derived = 0;
    foreach (var word in order.Skip(cfg.Core))
    {
        if (kept.Count >= cfg.Total) break;
        if (counts[word] < cfg.MinTailCount) break;
        if (!Morphology.IsDerived(word, cfg.Code, coreSet)) continue;
        kept.Add(word);
        derived++;
    }

    // 4. Indonesian: drop English words that leaked in from subtitles
    int foreign = 0;
    if (cfg.Code == "id" && relativeByLang.TryGetValue("en", out var english))
    {
        bool IsEnglish(string w) => english.TryGetValue(w, out var en) && en > 3 * relative.GetValueOrDefault(w) && !starterKeys.Contains(w);
        // also "brown-brown": reduplicated English words
        foreign = kept.RemoveAll(w => IsEnglish(w) || (w.Contains('-') && IsEnglish(w[..w.IndexOf('-')])));
    }

    // 5. merge the starter list
    var sortedCounts = kept.Select(w => counts[w]).OrderByDescending(c => c).ToList();
    var display = new Dictionary<string, string>(StringComparer.Ordinal);
    var keptSet = kept.ToHashSet(StringComparer.Ordinal);
    for (int i = 0; i < starter.Count; i++)
    {
        var (word, key) = starter[i];
        display.TryAdd(key, word);
        if (keptSet.Add(key))
        {
            int at = Math.Min((i + 1) * 3, sortedCounts.Count - 1);
            counts[key] = Math.Max(1, Math.Round(sortedCounts[at] * 0.5));
            kept.Add(key);
        }
    }

    var output = new List<string>
    {
        "# Generated by tools/DictionaryBuilder - do not edit by hand; edit starter.txt instead.",
        "# Frequencies: FrequencyWords by Hermit Dave (OpenSubtitles 2018), CC BY-SA 4.0,",
        "# https://github.com/hermitdave/FrequencyWords, filtered and merged with starter.txt.",
    };
    output.AddRange(kept.OrderByDescending(w => counts[w]).ThenBy(w => w, StringComparer.Ordinal)
        .Select(w => $"{(display.TryGetValue(w, out var d) ? d : w)} {(long)counts[w]}"));
    File.WriteAllLines(Path.Combine(dir, "words.txt"), output, new UTF8Encoding(false));
    Console.WriteLine($"{cfg.Code}: {kept.Count} words ({core.Count} core + {derived} affixed forms, {foreign} English removed) from {Path.GetFileName(file)}");
}

static string Normalize(string word, string code)
{
    word = word.Normalize(NormalizationForm.FormC).Trim();
    if (code == "ar") return Regex.Replace(word, "[ً-ْٰـ]", "");
    return word.ToLowerInvariant().Replace('’', '\'');
}

static List<(string Word, string Key)> ReadStarter(string path, string code)
{
    var list = new List<(string, string)>();
    if (!File.Exists(path)) return list;
    foreach (var line in File.ReadLines(path, Encoding.UTF8))
    {
        var t = line.Trim().TrimStart('﻿');
        if (t.Length == 0 || t.StartsWith('#')) continue;
        var word = t.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Normalize(NormalizationForm.FormC);
        list.Add((word, Normalize(word, code)));
    }
    return list;
}

record LangConfig(string Code, string Pattern, int Core, int Total, double MinTailCount, string[] Singles, string[] Block);

/// <summary>Recognizes regular inflections of known words.</summary>
static class Morphology
{
    static readonly string[] IdPrefixes = { "memper", "diper", "member", "meng", "meny", "mem", "men", "me", "di", "ter", "ber", "be", "per", "peng", "peny", "pem", "pen", "pe", "ke", "se" };
    static readonly string[] IdSuffixes = { "kannya", "annya", "inya", "kan", "an", "i", "nya", "ku", "mu", "lah", "kah", "pun", "tah" };
    static readonly string[] IdClitics = { "nya", "ku", "mu", "lah", "kah", "pun" };
    static readonly string[] EnSuffixes = { "ings", "ing", "ed", "es", "s", "er", "ers", "est", "ly", "ness", "ment", "ments", "ful", "less", "able", "'s" };
    static readonly string[] ArPrefixes = { "وال", "فال", "بال", "كال", "لل", "ال", "و", "ف", "ب", "ل", "ك", "س" };
    static readonly string[] ArSuffixes = { "هما", "كما", "تهم", "ها", "هم", "هن", "كم", "كن", "نا", "ني", "ات", "ون", "ين", "ان", "تي", "ته", "ه", "ك", "ي", "ة" };

    public static bool IsDerived(string word, string code, HashSet<string> core) => code switch
    {
        "id" => IsIndonesian(word, core),
        "en" => IsEnglish(word, core),
        "ar" => IsArabic(word, core),
        _ => false,
    };

    static bool IsIndonesian(string word, HashSet<string> core)
    {
        // Reduplication: "anak-anak", "berlari-lari"
        int dash = word.IndexOf('-');
        if (dash > 0)
        {
            string a = word[..dash], b = word[(dash + 1)..];
            return (a == b || a.EndsWith(b) || b.StartsWith(a)) && (core.Contains(a) || core.Contains(b) || IsIndonesian(a, core));
        }

        foreach (var stem in StripSuffixes(word, IdSuffixes, IdClitics))
        {
            if (stem.Length >= 3 && core.Contains(stem) && stem != word) return true;
            foreach (var prefix in IdPrefixes)
            {
                if (!stem.StartsWith(prefix) || stem.Length - prefix.Length < 3) continue;
                string rest = stem[prefix.Length..];
                foreach (var baseWord in NasalRestorations(prefix, rest))
                    if (core.Contains(baseWord)) return true;
            }
        }
        return false;
    }

    /// <summary>meny+apu -> sapu, mem+ukul -> pukul, men+ulis -> tulis, meng+ambil -> kambil? (both tried).</summary>
    static IEnumerable<string> NasalRestorations(string prefix, string rest)
    {
        yield return rest;
        switch (prefix)
        {
            case "meny" or "peny": yield return "s" + rest; break;
            case "mem" or "pem": yield return "p" + rest; break;
            case "men" or "pen": yield return "t" + rest; break;
            case "meng" or "peng": yield return "k" + rest; break;
        }
    }

    static IEnumerable<string> StripSuffixes(string word, string[] suffixes, string[] clitics)
    {
        yield return word;
        foreach (var s in suffixes)
        {
            if (!word.EndsWith(s) || word.Length - s.Length < 3) continue;
            string stem = word[..^s.Length];
            yield return stem;
            foreach (var c in clitics)
                if (stem.EndsWith(c) && stem.Length - c.Length >= 3) yield return stem[..^c.Length];
        }
    }

    static bool IsEnglish(string word, HashSet<string> core)
    {
        foreach (var s in EnSuffixes)
        {
            if (!word.EndsWith(s) || word.Length - s.Length < 3) continue;
            string stem = word[..^s.Length];
            if (core.Contains(stem) || core.Contains(stem + "e")) return true;
            if (stem.Length > 3 && stem[^1] == stem[^2] && core.Contains(stem[..^1])) return true; // running -> run
            if (stem.EndsWith('i') && core.Contains(stem[..^1] + "y")) return true;               // happily -> happy
        }
        return false;
    }

    static bool IsArabic(string word, HashSet<string> core)
    {
        foreach (var prefix in ArPrefixes.Prepend(""))
        {
            if (!word.StartsWith(prefix) || word.Length - prefix.Length < 2) continue;
            string rest = word[prefix.Length..];
            if (prefix.Length > 0 && core.Contains(rest)) return true;
            foreach (var suffix in ArSuffixes)
            {
                if (!rest.EndsWith(suffix) || rest.Length - suffix.Length < 2) continue;
                string stem = rest[..^suffix.Length];
                if (core.Contains(stem) || core.Contains(stem + "ة") || (stem.EndsWith('ت') && core.Contains(stem[..^1] + "ة")))
                    return true;
            }
        }
        return false;
    }
}
