using SlideKeyboard.Core.Geometry;
using SlideKeyboard.Core.Swipe;
using Xunit.Abstractions;

namespace SlideKeyboard.Core.Tests;

public class SwipeRecognizerTests
{
    readonly ITestOutputHelper _log;
    readonly SwipeRecognizer _recognizer = new();

    public SwipeRecognizerTests(ITestOutputHelper log) => _log = log;

    string? Top(string code, IReadOnlyList<Vec2> gesture, string? previous = null, IReadOnlyList<long>? times = null)
    {
        var pack = Packs.Get(code);
        var result = _recognizer.Recognize(gesture, new[] { new SwipeSource(Packs.Index(pack), 0) }, previous, 5, timestamps: times);
        _log.WriteLine(string.Join(" | ", result.Select(r => $"{r.Word} {r.Score:F2}")));
        return result.FirstOrDefault()?.Word;
    }

    [Theory]
    [InlineData("en", "hello")]
    [InlineData("en", "the")]
    [InlineData("en", "going")]
    [InlineData("en", "good")]
    [InlineData("en", "world")]
    [InlineData("en", "thanks")]
    [InlineData("en", "there")]
    [InlineData("en", "people")]
    [InlineData("id", "selamat")]
    [InlineData("id", "terima")]
    [InlineData("id", "kasih")]
    [InlineData("ar", "سلام")]
    [InlineData("ar", "شكرا")]
    [InlineData("ar", "مرحبا")]
    public void Recognizes_spec_examples_from_clean_gestures(string code, string word)
    {
        var map = Packs.KeyMap(Packs.Get(code));
        var (points, times) = GestureSimulator.TimedSwipe(map, word);
        Assert.Equal(word, Top(code, points, times: times));
    }

    [Theory]
    [InlineData("en", "hello")]
    [InlineData("id", "selamat")]
    [InlineData("ar", "سلام")]
    public void Tolerates_sloppy_gestures(string code, string word)
    {
        var map = Packs.KeyMap(Packs.Get(code));
        int correct = 0;
        for (int seed = 0; seed < 20; seed++)
        {
            var (points, times) = GestureSimulator.TimedSwipe(map, word, new Random(seed), jitter: 0.3);
            if (Top(code, points, times: times) == word) correct++;
        }
        Assert.True(correct >= 18, $"{word}: {correct}/20");
    }

    [Theory]
    [InlineData("en", 0, 0.30, 0.85)]
    [InlineData("id", 0, 0.30, 0.85)]
    [InlineData("ar", 0, 0.30, 0.80)]
    [InlineData("en", 300, 0.30, 0.70)]
    [InlineData("id", 300, 0.30, 0.70)]
    [InlineData("ar", 300, 0.30, 0.60)]
    public void Accuracy_over_vocabulary(string code, int skip, double jitter, double minimumTop1)
    {
        var pack = Packs.Get(code);
        var map = Packs.KeyMap(pack);
        var index = SwipeIndex.Build(pack, map);
        var sources = new[] { new SwipeSource(index, 0) };
        var random = new Random(42);

        // skip == 0: the 300 most frequent swipeable words with at least two distinct keys.
        // otherwise: 300 words sampled from frequency ranks skip..5000 (everyday but less common words).
        var swipeable = pack.Words.Entries.Where(e => map.KeySequence(e.Word) is { Length: >= 2 });
        var words = skip == 0
            ? swipeable.Take(300).ToList()
            : swipeable.Skip(skip).Take(5000 - skip).Where((_, i) => i % ((5000 - skip) / 300) == 0).Take(300).ToList();

        int top1 = 0, top3 = 0;
        var misses = new List<string>();
        foreach (var entry in words)
        {
            var gesture = GestureSimulator.Swipe(map, entry.Word, random, jitter);
            var result = _recognizer.Recognize(gesture, sources, null, 3);
            // Words with an identical key path (good/god, it's/its) are interchangeable for a swipe.
            var expectedKeys = new string(map.KeySequence(entry.Word)!);
            bool Same(SwipeCandidate c) => new string(map.KeySequence(c.Word)!) == expectedKeys;
            if (result.Count > 0 && Same(result[0])) top1++;
            else misses.Add($"{entry.Word}->{result.FirstOrDefault()?.Word}");
            if (result.Any(Same)) top3++;
        }

        double rate1 = top1 / (double)words.Count, rate3 = top3 / (double)words.Count;
        _log.WriteLine($"{code}: top1 {rate1:P1}, top3 {rate3:P1} over {words.Count} words");
        _log.WriteLine("misses: " + string.Join(", ", misses.Take(60)));
        Assert.True(rate1 >= minimumTop1, $"top-1 accuracy {rate1:P1}");
        Assert.True(rate3 >= minimumTop1 + 0.1, $"top-3 accuracy {rate3:P1}");
    }

    [Theory]
    [InlineData("ar", "سلام")]   // straight line: س ل ا م vs سام / سم — only the pauses tell them apart
    [InlineData("en", "there")]
    [InlineData("id", "selamat")]
    [InlineData("id", "saya")]
    public void Pauses_on_letters_disambiguate_straight_paths(string code, string word)
    {
        var pack = Packs.Get(code);
        var map = Packs.KeyMap(pack);
        var sources = new[] { new SwipeSource(Packs.Index(pack), 0) };
        int correct = 0;
        for (int seed = 0; seed < 10; seed++)
        {
            var (points, times) = GestureSimulator.TimedSwipe(map, word, new Random(seed), jitter: 0.2);
            var result = _recognizer.Recognize(points, sources, null, 3, timestamps: times);
            _log.WriteLine(string.Join(" | ", result.Select(r => $"{r.Word} {r.Score:F2}")));
            if (result.FirstOrDefault()?.Word == word) correct++;
        }
        Assert.True(correct >= 9, $"{word}: {correct}/10");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("id")]
    [InlineData("ar")]
    public void Timed_gestures_with_pauses_stay_accurate(string code)
    {
        var pack = Packs.Get(code);
        var map = Packs.KeyMap(pack);
        var sources = new[] { new SwipeSource(SwipeIndex.Build(pack, map), 0) };
        var random = new Random(7);
        var words = pack.Words.Entries.Where(e => map.KeySequence(e.Word) is { Length: >= 2 }).Take(300).ToList();
        int top1 = 0;
        foreach (var entry in words)
        {
            var (points, times) = GestureSimulator.TimedSwipe(map, entry.Word, random, jitter: 0.3);
            var result = _recognizer.Recognize(points, sources, null, 3, timestamps: times);
            var expected = new string(map.KeySequence(entry.Word)!);
            if (result.Count > 0 && new string(map.KeySequence(result[0].Word)!) == expected) top1++;
        }
        double rate = top1 / (double)words.Count;
        _log.WriteLine($"{code}: timed top1 {rate:P1}");
        Assert.True(rate >= 0.9, $"{rate:P1}");
    }

    [Fact]
    public void Context_prefers_likely_following_word()
    {
        var pack = Packs.Get("id");
        var map = Packs.KeyMap(pack);
        // "pagi" after "selamat" is a listed bigram.
        Assert.Equal("pagi", Top("id", GestureSimulator.Swipe(map, "pagi"), previous: "selamat"));
    }

    [Fact]
    public void Recognition_is_fast_enough_for_realtime_preview()
    {
        var pack = Packs.Get("en");
        var map = Packs.KeyMap(pack);
        var sources = new[] { new SwipeSource(SwipeIndex.Build(pack, map), 0) };
        var gesture = GestureSimulator.Swipe(map, "something", new Random(1), 0.25);
        _recognizer.Recognize(gesture, sources, null, 5); // warm up

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) _recognizer.Recognize(gesture, sources, null, 5);
        double ms = sw.Elapsed.TotalMilliseconds / 20;
        _log.WriteLine($"{ms:F2} ms per recognition");
        Assert.True(ms < 15, $"{ms:F2} ms");
    }
}
