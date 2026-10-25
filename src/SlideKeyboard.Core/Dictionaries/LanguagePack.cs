using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SlideKeyboard.Core.Layout;

namespace SlideKeyboard.Core.Dictionaries;

/// <summary>manifest.json of a language pack folder.</summary>
public sealed class PackManifest
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string NativeName { get; set; } = "";
    public string ShortLabel { get; set; } = "";
    public string Culture { get; set; } = "";
    public bool Rtl { get; set; }
    public string Version { get; set; } = "1.0.0";
    public string WordsFile { get; set; } = "words.txt";
    public string BigramsFile { get; set; } = "bigrams.txt";
    public string LayoutFile { get; set; } = "layout.json";
}

/// <summary>Everything the keyboard needs for one language: layout, vocabulary and context model.</summary>
public sealed class LanguagePack
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string NativeName { get; init; }
    public required string ShortLabel { get; init; }
    public string Version { get; init; } = "1.0.0";
    public required LayoutSpec Layout { get; init; }
    /// <summary>Vocabulary, including the user's personal words once <see cref="Input.KeyboardEngine"/> merges them.</summary>
    public required WordList Words { get; set; }
    public required BigramTable Bigrams { get; init; }
    public CultureInfo Culture { get; init; } = CultureInfo.InvariantCulture;
    public string? SourceDirectory { get; init; }

    public bool Rtl => Layout.Rtl;
    public bool HasCase => Layout.HasShift;

    public override string ToString() => $"{Code} ({Words.Count} words)";
}

public static class LanguagePackLoader
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static LanguagePack Load(string directory)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        var manifest = JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(manifestPath), JsonOptions)
                       ?? throw new InvalidDataException($"Invalid manifest: {manifestPath}");
        if (string.IsNullOrWhiteSpace(manifest.Code)) manifest.Code = Path.GetFileName(directory);

        var layoutPath = Path.Combine(directory, manifest.LayoutFile);
        var layout = File.Exists(layoutPath)
            ? JsonSerializer.Deserialize<LayoutSpec>(File.ReadAllText(layoutPath), JsonOptions) ?? LayoutSpec.DefaultLatin()
            : LayoutSpec.DefaultLatin();
        if (manifest.Rtl) layout.Rtl = true;

        var wordsPath = Path.Combine(directory, manifest.WordsFile);
        var words = File.Exists(wordsPath) ? WordList.Parse(File.ReadLines(wordsPath)) : WordList.Empty;

        var bigramsPath = Path.Combine(directory, manifest.BigramsFile);
        var bigrams = File.Exists(bigramsPath) ? BigramTable.Parse(File.ReadLines(bigramsPath)) : new BigramTable();

        CultureInfo culture;
        try { culture = string.IsNullOrWhiteSpace(manifest.Culture) ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(manifest.Culture); }
        catch (CultureNotFoundException) { culture = CultureInfo.InvariantCulture; }

        return new LanguagePack
        {
            Code = manifest.Code.ToLowerInvariant(),
            Name = string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Code : manifest.Name,
            NativeName = string.IsNullOrWhiteSpace(manifest.NativeName) ? manifest.Name : manifest.NativeName,
            ShortLabel = string.IsNullOrWhiteSpace(manifest.ShortLabel) ? manifest.Code.ToUpperInvariant() : manifest.ShortLabel,
            Version = manifest.Version,
            Layout = layout,
            Words = words,
            Bigrams = bigrams,
            Culture = culture,
            SourceDirectory = directory,
        };
    }

    /// <summary>
    /// Loads every pack folder (one containing manifest.json) under each root. A pack in a later root replaces a
    /// pack with the same code from an earlier root, so user-installed packs override the bundled ones.
    /// </summary>
    public static IReadOnlyList<LanguagePack> LoadAll(IEnumerable<string> roots, Action<string, Exception>? onError = null)
    {
        var byCode = new Dictionary<string, LanguagePack>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(Path.Combine(dir, "manifest.json"))) continue;
                try
                {
                    var pack = Load(dir);
                    if (!byCode.ContainsKey(pack.Code)) order.Add(pack.Code);
                    byCode[pack.Code] = pack;
                }
                catch (Exception ex)
                {
                    onError?.Invoke(dir, ex);
                }
            }
        }
        return order.Select(c => byCode[c]).ToList();
    }
}
