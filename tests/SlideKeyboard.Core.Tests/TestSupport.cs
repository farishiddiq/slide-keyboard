using System.Text;
using SlideKeyboard.Core.Dictionaries;
using SlideKeyboard.Core.Geometry;
using SlideKeyboard.Core.Input;
using SlideKeyboard.Core.Layout;
using SlideKeyboard.Core.Swipe;
using SlideKeyboard.Core.Text;

namespace SlideKeyboard.Core.Tests;

/// <summary>Loads the real language packs shipped in /dictionaries once per test run.</summary>
public static class Packs
{
    static readonly Lazy<IReadOnlyList<LanguagePack>> All = new(() =>
        LanguagePackLoader.LoadAll(new[] { Path.Combine(AppContext.BaseDirectory, "dictionaries") }));

    public static LanguagePack Get(string code) => All.Value.Single(p => p.Code == code);

    public static IReadOnlyList<LanguagePack> Every => All.Value;

    public static LanguageManager Manager(params string[] enabled)
    {
        var manager = new LanguageManager(All.Value);
        manager.SetEnabled(enabled.Length > 0 ? enabled : new[] { "id", "en", "ar" });
        return manager;
    }

    public static SwipeKeyMap KeyMap(LanguagePack pack, bool numberRow = false) =>
        new(LayoutBuilder.Build(pack.Layout, KeyboardPage.Letters, numberRow), pack.Layout);

    public static SwipeIndex Index(LanguagePack pack, bool numberRow = false) => SwipeIndex.Build(pack, KeyMap(pack, numberRow));
}

/// <summary>Simulates a text field: applies injected text and backspaces to an in-memory buffer.</summary>
public sealed class FakeTextField : ITextOutput
{
    readonly StringBuilder _text = new();

    public string Text => _text.ToString();

    public void SendText(string text) => _text.Append(text);

    public void SendBackspace(int count) => TextUtil.RemoveLastTextElements(_text, count);

    public void SendKey(SpecialKey key)
    {
        if (key == SpecialKey.Enter) _text.Append('\n');
    }
}

/// <summary>Generates human-like swipe gestures for a word.</summary>
public static class GestureSimulator
{
    /// <summary>
    /// Path through the word's keys with random offsets from each key center (users never hit centers) and
    /// corners rounded off within <paramref name="cornerRadius"/> keys, like a real finger or mouse trace.
    /// </summary>
    public static List<Vec2> Swipe(SwipeKeyMap map, string word, Random? random = null, double jitter = 0, double cornerRadius = 0.4)
    {
        var keys = map.KeySequence(word) ?? throw new ArgumentException($"'{word}' cannot be typed on this layout");
        var anchors = keys.Select(k =>
        {
            var c = map.Center(k);
            if (random == null || jitter <= 0) return c;
            return new Vec2(c.X + (random.NextDouble() * 2 - 1) * jitter, c.Y + (random.NextDouble() * 2 - 1) * jitter * 0.8);
        }).ToList();

        if (anchors.Count == 1) anchors.Add(anchors[0] + new Vec2(0.05, 0.02));
        return Densify(RoundCorners(anchors, cornerRadius), 0.06);
    }

    /// <summary>
    /// Like <see cref="Swipe"/> but with timestamps: constant speed (keys per millisecond) and a pause of
    /// <paramref name="pauseMs"/> at each interior letter, the way mouse users slow down on the letters they mean.
    /// </summary>
    public static (List<Vec2> Points, List<long> Times) TimedSwipe(SwipeKeyMap map, string word, Random? random = null,
        double jitter = 0, double pauseMs = 90, double speed = 0.012)
    {
        var keys = map.KeySequence(word) ?? throw new ArgumentException(word);
        var anchors = keys.Select(k =>
        {
            var c = map.Center(k);
            if (random == null || jitter <= 0) return c;
            return new Vec2(c.X + (random.NextDouble() * 2 - 1) * jitter, c.Y + (random.NextDouble() * 2 - 1) * jitter * 0.8);
        }).ToList();

        var points = new List<Vec2> { anchors[0] };
        var times = new List<long> { 0 };
        double t = 0;
        for (int i = 1; i < anchors.Count; i++)
        {
            var segment = Densify(new List<Vec2> { anchors[i - 1], anchors[i] }, 0.06);
            for (int k = 1; k < segment.Count; k++)
            {
                t += Vec2.Distance(segment[k - 1], segment[k]) / speed;
                points.Add(segment[k]);
                times.Add((long)t);
            }
            if (i < anchors.Count - 1 && pauseMs > 0)
            {
                // The pointer stops: the next movement event arrives late.
                t += pauseMs;
                points.Add(new Vec2(anchors[i].X + 0.03, anchors[i].Y));
                times.Add((long)t);
            }
        }
        return (points, times);
    }

    /// <summary>Replaces each interior corner with a quadratic curve starting/ending <paramref name="radius"/> away from it.</summary>
    static List<Vec2> RoundCorners(List<Vec2> points, double radius)
    {
        var result = new List<Vec2> { points[0] };
        for (int i = 1; i < points.Count - 1; i++)
        {
            Vec2 prev = points[i - 1], corner = points[i], next = points[i + 1];
            double r1 = Math.Min(radius, Vec2.Distance(prev, corner) / 3);
            double r2 = Math.Min(radius, Vec2.Distance(corner, next) / 3);
            var a = Vec2.Lerp(corner, prev, r1 / Math.Max(Vec2.Distance(prev, corner), 1e-9));
            var b = Vec2.Lerp(corner, next, r2 / Math.Max(Vec2.Distance(corner, next), 1e-9));
            result.Add(a);
            for (int s = 1; s < 6; s++)
            {
                double t = s / 6.0;
                result.Add(Vec2.Lerp(Vec2.Lerp(a, corner, t), Vec2.Lerp(corner, b, t), t));
            }
            result.Add(b);
        }
        result.Add(points[^1]);
        return result;
    }

    static List<Vec2> Densify(List<Vec2> points, double step)
    {
        var result = new List<Vec2> { points[0] };
        for (int i = 1; i < points.Count; i++)
        {
            double d = Vec2.Distance(points[i - 1], points[i]);
            int n = Math.Max(1, (int)(d / step));
            for (int k = 1; k <= n; k++) result.Add(Vec2.Lerp(points[i - 1], points[i], k / (double)n));
        }
        return result;
    }
}
