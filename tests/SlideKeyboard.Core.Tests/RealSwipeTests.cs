using System.Globalization;
using SlideKeyboard.Core.Geometry;
using SlideKeyboard.Core.Swipe;
using Xunit.Abstractions;

namespace SlideKeyboard.Core.Tests;

/// <summary>
/// Swipes recorded with a real mouse on the app (Settings > Record swipes), labeled with the word the user meant.
/// Synthetic gestures are too clean to catch everything; these keep recognition honest on human input.
/// </summary>
public class RealSwipeTests
{
    readonly ITestOutputHelper _log;
    readonly SwipeRecognizer _recognizer = new();

    public RealSwipeTests(ITestOutputHelper log) => _log = log;

    public static IEnumerable<object[]> Cases() =>
        Load("real-swipes-id.txt").Select((c, i) => new object[] { i, c.Expected });

    static List<(string Expected, Vec2[] Points, long[] Times)> Load(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", file);
        var list = new List<(string, Vec2[], long[])>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith('#') || !line.Contains('|')) continue;
            var parts = line.Split('|');
            var samples = parts[1].Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Split(','))
                .Select(f => (new Vec2(double.Parse(f[0], CultureInfo.InvariantCulture), double.Parse(f[1], CultureInfo.InvariantCulture)),
                              long.Parse(f[2], CultureInfo.InvariantCulture)))
                .ToList();
            list.Add((parts[0], samples.Select(s => s.Item1).ToArray(), samples.Select(s => s.Item2).ToArray()));
        }
        return list;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Recognizes_recorded_indonesian_swipe(int index, string expected)
    {
        var (_, points, times) = Load("real-swipes-id.txt")[index];
        var swipeIndex = Packs.Index(Packs.Get("id"), numberRow: true);
        var result = _recognizer.Recognize(points, new[] { new SwipeSource(swipeIndex, 0) }, null, 5, timestamps: times);
        foreach (var candidate in result.Take(3).Select(r => r.Word).Append(expected.TrimStart('~')).Distinct())
            _log.WriteLine(_recognizer.Explain(points, times, swipeIndex, candidate) ?? candidate + ": n/a");
        _log.WriteLine("ranked: " + string.Join(" | ", result.Select(r => $"{r.Word} {r.Score:F2}")));
        if (expected.StartsWith('~')) Assert.Contains(expected[1..], result.Take(3).Select(r => r.Word));
        else Assert.Equal(expected, result.FirstOrDefault()?.Word);
    }
}
