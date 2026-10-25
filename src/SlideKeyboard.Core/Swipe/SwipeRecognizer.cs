using SlideKeyboard.Core.Dictionaries;
using SlideKeyboard.Core.Geometry;
using SlideKeyboard.Core.Text;

namespace SlideKeyboard.Core.Swipe;

public sealed class SwipeOptions
{
    /// <summary>Number of points gestures and templates are resampled to.</summary>
    public int SampleCount { get; set; } = 48;

    /// <summary>Keys whose center is within this distance of the gesture start/end are considered as first/last letter.</summary>
    public double EndpointRadius { get; set; } = 1.15;

    /// <summary>Spread of the location channel (mean distance between gesture and template).</summary>
    public double SigmaLocation { get; set; } = 0.5;

    /// <summary>Spread of the per-letter "did the path pass this key" penalty.</summary>
    public double SigmaKey { get; set; } = 0.33;

    /// <summary>Passing within this distance of a key center costs nothing.</summary>
    public double KeyFreeRadius { get; set; } = 0.35;

    public double SigmaEndpoint { get; set; } = 0.6;

    /// <summary>Weight of the word frequency prior (log scale).</summary>
    public double PriorWeight { get; set; } = 0.45;

    /// <summary>Weight of the previous-word context bonus.</summary>
    public double ContextWeight { get; set; } = 0.8;

    /// <summary>Turning angle (degrees) above which a point of the gesture counts as a deliberate corner.</summary>
    public double CornerAngle { get; set; } = 65;

    /// <summary>A corner within this distance of one of the word's keys is explained by it.</summary>
    public double CornerFreeRadius { get; set; } = 0.3;

    public double SigmaCorner { get; set; } = 0.25;

    /// <summary>Most one corner or pause can cost: real hands pause and wobble off-letter too (repositioning the mouse).</summary>
    public double CornerPenaltyCap { get; set; } = 1.5;

    /// <summary>A pause counts when the pointer moves slower than this fraction of the gesture's median speed...</summary>
    public double DwellSpeedRatio { get; set; } = 0.3;

    /// <summary>...for at least this long (milliseconds).</summary>
    public double DwellMinMs { get; set; } = 70;

    /// <summary>Pauses are treated like corners of this angle (a right angle weighs 1).</summary>
    public double DwellWeightAngle { get; set; } = 135;

    public double MinLengthRatio { get; set; } = 0.45;
    public double MaxLengthRatio { get; set; } = 1.8;
    public double LengthSlack { get; set; } = 1.5;
}

public sealed record SwipeSource(SwipeIndex Index, double Bias);

public sealed record SwipeCandidate(WordEntry Entry, LanguagePack Pack, double Score)
{
    public string Word => Entry.Word;
}

/// <summary>
/// Converts a complete swipe gesture into ranked words.
///
/// Each dictionary word has an ideal path through its key centers. A word's score combines:
///  1. location channel   – mean distance between the resampled gesture and the resampled ideal path
///                          (captures path shape and direction),
///  2. key alignment      – for every letter, how close the gesture came to that key, in order (dynamic
///                          programming over gesture samples, so letters must be visited in sequence),
///  3. corners            – every sharp turn, and every pause (mouse users slow down on the letters they mean),
///                          must lie near one of the word's letters,
///     endpoints          – distance of the first/last letter from where the gesture started/ended,
///  4. frequency prior    – log word frequency from the language pack,
///  5. context            – bigram bonus given the previous word, plus a caller-supplied boost.
/// Candidates are pruned to words whose first/last keys are near the gesture endpoints and whose
/// ideal path length is compatible with the gesture length.
/// </summary>
public sealed class SwipeRecognizer
{
    const double CornerKeyMargin = 0.1;

    readonly SwipeOptions _o;

    public SwipeRecognizer(SwipeOptions? options = null) => _o = options ?? new SwipeOptions();

    public SwipeOptions Options => _o;

    public IReadOnlyList<SwipeCandidate> Recognize(
        IReadOnlyList<Vec2> rawPoints,
        IReadOnlyList<SwipeSource> sources,
        string? previousWord,
        int maxResults,
        Func<LanguagePack, WordEntry, double>? extraPrior = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<long>? timestamps = null)
    {
        var (points, times) = Clean(rawPoints, timestamps);
        if (points.Count < 2 || sources.Count == 0 || maxResults <= 0) return Array.Empty<SwipeCandidate>();

        int n = _o.SampleCount;
        var gesture = PathMath.Resample(points, n);
        double gestureLength = PathMath.Length(points);
        string? previousKey = previousWord == null ? null : TextUtil.NormalizeKey(previousWord);
        var buffer = new double[n * 2];
        var gestureCorners = FindCorners(gesture);
        if (times != null) gestureCorners.AddRange(FindDwells(points, times, gestureLength));
        double endpointDenominator = 2 * _o.SigmaEndpoint * _o.SigmaEndpoint;
        var best = new Dictionary<string, SwipeCandidate>(StringComparer.Ordinal);
        // A few extra slots so duplicate display words cannot starve the result list.
        var top = new TopScores(maxResults + 4);
        int scored = 0;

        foreach (var source in sources)
        {
            var index = source.Index;
            var pack = index.Pack;
            var starts = NearKeys(index.Map, points[0]);
            var ends = NearKeys(index.Map, points[^1]);
            var corners = WithNearestKey(gestureCorners, index.Map);

            foreach (char first in starts)
            foreach (char last in ends)
            foreach (var item in index.Get(first, last))
            {
                if ((++scored & 127) == 0) cancellationToken.ThrowIfCancellationRequested();

                double templateLength = item.Length;
                if (gestureLength < _o.MinLengthRatio * templateLength - _o.LengthSlack ||
                    gestureLength > _o.MaxLengthRatio * templateLength + _o.LengthSlack)
                    continue;

                // Cheap terms first: every shape term is <= 0, so a word that cannot reach the current
                // top-N with a perfect shape is skipped without running the alignment.
                double startDistance = Vec2.Distance(points[0], item.Points[0]);
                double endDistance = Vec2.Distance(points[^1], item.Points[^1]);
                double score = -(startDistance * startDistance + endDistance * endDistance) / endpointDenominator
                               + _o.PriorWeight * item.Entry.LogPrior
                               + _o.ContextWeight * pack.Bigrams.Bonus(previousKey, item.Entry.Key)
                               + source.Bias
                               + (extraPrior?.Invoke(pack, item.Entry) ?? 0);
                if (score <= top.Threshold) continue;

                score += ShapeScore(item, gesture, corners, buffer);
                if (score <= top.Threshold) continue;

                string word = item.Entry.Word;
                if (!best.TryGetValue(word, out var existing) || existing.Score < score)
                {
                    best[word] = new SwipeCandidate(item.Entry, pack, score);
                    top.Add(score);
                }
            }
        }

        return best.Values.OrderByDescending(c => c.Score).Take(maxResults).ToList();
    }

    /// <summary>Diagnostics: the individual score terms of one word for a gesture (null when the word is unknown).</summary>
    internal string? Explain(IReadOnlyList<Vec2> rawPoints, IReadOnlyList<long>? timestamps, SwipeIndex index, string word)
    {
        var (points, times) = Clean(rawPoints, timestamps);
        var keys = index.Map.KeySequence(word);
        if (keys == null || points.Count < 2) return null;
        var item = index.Get(keys[0], keys[^1]).FirstOrDefault(i => i.Entry.Word == word);
        if (item == null) return $"{word}: not in index for these endpoints";
        int n = _o.SampleCount;
        var gesture = PathMath.Resample(points, n);
        double length = PathMath.Length(points);
        var corners = FindCorners(gesture);
        if (times != null) corners.AddRange(FindDwells(points, times, length));
        corners = WithNearestKey(corners, index.Map);
        var template = item.Resampled ??= PathMath.Resample(item.Points, n);
        double location = 0;
        for (int i = 0; i < n; i++) location += Vec2.Distance(gesture[i], template[i]);
        location /= n;
        double ds = Vec2.Distance(points[0], item.Points[0]), de = Vec2.Distance(points[^1], item.Points[^1]);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{word}: loc {-(location * location) / (2 * _o.SigmaLocation * _o.SigmaLocation):F2} align {-AlignmentPenalty(item.Points, gesture, new double[n * 2]):F2} " +
            $"corner {-CornerPenalty(item.Points, corners):F2} ends {-(ds * ds + de * de) / (2 * _o.SigmaEndpoint * _o.SigmaEndpoint):F2} " +
            $"prior {_o.PriorWeight * item.Entry.LogPrior:F2} (rank {item.Entry.Rank}) len {length:F1}/{item.Length:F1} corners {corners.Count}");
    }

    /// <summary>Shape log-likelihood of the gesture given the word's ideal path (0 is perfect, more negative is worse).</summary>
    double ShapeScore(SwipeIndex.Item item, Vec2[] gesture, List<Corner> corners, double[] buffer)
    {
        var template = item.Resampled ??= PathMath.Resample(item.Points, _o.SampleCount);
        double location = 0;
        for (int i = 0; i < gesture.Length; i++) location += Vec2.Distance(gesture[i], template[i]);
        location /= gesture.Length;

        return -(location * location) / (2 * _o.SigmaLocation * _o.SigmaLocation)
               - AlignmentPenalty(item.Points, gesture, buffer)
               - CornerPenalty(item.Points, corners);
    }

    /// <summary>
    /// Every sharp turn in the gesture should be explained by one of the word's letters: users change direction on
    /// purpose at keys they want. Penalizes words that leave a corner unexplained ("the" for a t-h-e-r-e swipe).
    /// </summary>
    double CornerPenalty(Vec2[] keys, List<Corner> corners)
    {
        double penalty = 0;
        double denominator = 2 * _o.SigmaCorner * _o.SigmaCorner;
        foreach (var corner in corners)
        {
            double nearest = double.MaxValue;
            foreach (var key in keys)
            {
                double d = Vec2.Distance(corner.Point, key);
                if (d < nearest) nearest = d;
            }
            // Free when the corner is near one of the word's keys, or no other key is clearly closer to it.
            double excess = nearest - Math.Max(_o.CornerFreeRadius, corner.NearestKeyDistance + CornerKeyMargin);
            // Sharper turns are more deliberate: a full reversal weighs twice a right angle.
            if (excess > 0) penalty += Math.Min(_o.CornerPenaltyCap, excess * excess / denominator * (corner.Angle / 90));
        }
        return penalty;
    }

    /// <summary>Points where the resampled gesture turns sharply (local maxima of the turning angle).</summary>
    internal List<Corner> FindCorners(Vec2[] gesture)
    {
        const int window = 2;
        int n = gesture.Length;
        var angles = new double[n];
        for (int i = window; i < n - window; i++)
        {
            var a = gesture[i] - gesture[i - window];
            var b = gesture[i + window] - gesture[i];
            double la = a.Length, lb = b.Length;
            if (la < 1e-6 || lb < 1e-6) continue;
            double cos = Math.Clamp((a.X * b.X + a.Y * b.Y) / (la * lb), -1, 1);
            angles[i] = Math.Acos(cos) * 180 / Math.PI;
        }

        var corners = new List<Corner>();
        for (int i = window; i < n - window; i++)
        {
            if (angles[i] < _o.CornerAngle) continue;
            bool isPeak = true;
            for (int k = Math.Max(window, i - window); k <= Math.Min(n - window - 1, i + window); k++)
                if (angles[k] > angles[i] || (angles[k] == angles[i] && k < i)) { isPeak = false; break; }
            if (isPeak) corners.Add(new Corner(gesture[i], angles[i], 0));
        }
        return corners;
    }

    static List<Corner> WithNearestKey(List<Corner> corners, SwipeKeyMap map)
    {
        var result = new List<Corner>(corners.Count);
        foreach (var corner in corners)
        {
            double nearest = double.MaxValue;
            foreach (var (_, center) in map.Keys)
                nearest = Math.Min(nearest, Vec2.Distance(corner.Point, center));
            result.Add(corner with { NearestKeyDistance = nearest });
        }
        return result;
    }

    internal readonly record struct Corner(Vec2 Point, double Angle, double NearestKeyDistance);

    /// <summary>Tracks the N-th best score seen so far.</summary>
    sealed class TopScores
    {
        readonly int _size;
        readonly List<double> _scores = new();

        public TopScores(int size) => _size = size;

        public double Threshold => _scores.Count < _size ? double.NegativeInfinity : _scores[^1];

        public void Add(double score)
        {
            int i = _scores.Count;
            while (i > 0 && _scores[i - 1] < score) i--;
            if (i >= _size) return;
            _scores.Insert(i, score);
            if (_scores.Count > _size) _scores.RemoveAt(_scores.Count - 1);
        }
    }

    /// <summary>
    /// Minimum total cost of assigning each letter, in order, to a gesture sample (monotonic alignment).
    /// A letter costs nothing when the gesture passed within <see cref="SwipeOptions.KeyFreeRadius"/> of it.
    /// </summary>
    double AlignmentPenalty(Vec2[] keys, Vec2[] gesture, double[] buffer)
    {
        int n = gesture.Length;
        var previous = buffer.AsSpan(0, n);
        var current = buffer.AsSpan(n, n);
        double freeRadius = _o.KeyFreeRadius;
        double denominator = 2 * _o.SigmaKey * _o.SigmaKey;

        for (int j = 0; j < n; j++) previous[j] = KeyCost(keys[0], gesture[j], freeRadius, denominator);

        for (int i = 1; i < keys.Length; i++)
        {
            double runningMin = double.MaxValue;
            for (int j = 0; j < n; j++)
            {
                if (previous[j] < runningMin) runningMin = previous[j];
                current[j] = runningMin + KeyCost(keys[i], gesture[j], freeRadius, denominator);
            }
            var swap = previous;
            previous = current;
            current = swap;
        }

        double min = double.MaxValue;
        for (int j = 0; j < n; j++) if (previous[j] < min) min = previous[j];
        return min;
    }

    static double KeyCost(Vec2 key, Vec2 point, double freeRadius, double denominator)
    {
        double d = Vec2.Distance(key, point) - freeRadius;
        return d <= 0 ? 0 : d * d / denominator;
    }

    List<char> NearKeys(SwipeKeyMap map, Vec2 p)
    {
        var list = new List<char>();
        char nearest = default;
        double nearestDistance = double.MaxValue;
        foreach (var (key, center) in map.Keys)
        {
            double d = Vec2.Distance(center, p);
            if (d <= _o.EndpointRadius) list.Add(key);
            if (d < nearestDistance)
            {
                nearestDistance = d;
                nearest = key;
            }
        }
        if (list.Count == 0 && nearestDistance < double.MaxValue) list.Add(nearest);
        return list;
    }

    /// <summary>
    /// Places where the pointer slowed down well below its usual speed (or stopped) for a moment, excluding the
    /// start and end of the gesture. Returned as corners so they must be explained by one of the word's letters.
    /// </summary>
    internal List<Corner> FindDwells(List<Vec2> points, List<long> times, double totalLength)
    {
        var dwells = new List<Corner>();
        int n = points.Count;
        if (n < 3) return dwells;

        var speeds = new double[n];
        var moving = new List<double>(n);
        for (int i = 1; i < n; i++)
        {
            double dt = Math.Max(1, times[i] - times[i - 1]);
            speeds[i] = Vec2.Distance(points[i - 1], points[i]) / dt;
            moving.Add(speeds[i]);
        }
        moving.Sort();
        double median = moving[moving.Count / 2];
        if (median <= 0) return dwells;
        double slow = median * _o.DwellSpeedRatio;

        const double edge = 0.35;
        double walked = 0;
        int runStart = -1;
        double runStartWalked = 0;
        for (int i = 1; i <= n; i++)
        {
            bool isSlow = i < n && speeds[i] < slow;
            if (i < n && isSlow && runStart < 0)
            {
                runStart = i;
                runStartWalked = walked;
            }
            if (runStart >= 0 && !isSlow)
            {
                double duration = times[i - 1] - times[runStart - 1];
                if (duration >= _o.DwellMinMs && runStartWalked > edge && totalLength - walked > edge)
                {
                    double x = 0, y = 0;
                    for (int k = runStart - 1; k < i; k++) { x += points[k].X; y += points[k].Y; }
                    int count = i - runStart + 1;
                    dwells.Add(new Corner(new Vec2(x / count, y / count), _o.DwellWeightAngle, 0));
                }
                runStart = -1;
            }
            if (i < n) walked += Vec2.Distance(points[i - 1], points[i]);
        }
        return dwells;
    }

    static (List<Vec2> Points, List<long>? Times) Clean(IReadOnlyList<Vec2> raw, IReadOnlyList<long>? rawTimes)
    {
        bool timed = rawTimes != null && rawTimes.Count == raw.Count;
        var list = new List<Vec2>(raw.Count);
        var times = timed ? new List<long>(raw.Count) : null;
        for (int i = 0; i < raw.Count; i++)
        {
            var p = raw[i];
            if (double.IsNaN(p.X) || double.IsNaN(p.Y)) continue;
            if (list.Count == 0 || Vec2.Distance(list[^1], p) > 0.01)
            {
                list.Add(p);
                times?.Add(rawTimes![i]);
            }
            else if (times != null)
            {
                // Stationary: keep the latest time so a pause shows up as a long interval.
                times[^1] = rawTimes![i];
            }
        }
        return (list, times);
    }
}
