using SlideKeyboard.Core.Geometry;
using SlideKeyboard.Core.Swipe;

namespace SlideKeyboard.Core.Prediction;

/// <summary>Keyboard-aware Damerau-Levenshtein (optimal string alignment) distance for typo correction.</summary>
public static class EditDistance
{
    const double NeighborSubstitution = 0.55;
    const double Substitution = 1.0;
    const double Transposition = 0.7;
    const double Indel = 1.0;
    const double RepeatedIndel = 0.5;
    const double NeighborDistance = 1.25;

    /// <summary>
    /// Cost of turning <paramref name="typed"/> into <paramref name="target"/>. Substituting a neighbouring key and
    /// adding/removing a doubled letter are cheap. Returns a value above <paramref name="max"/> as soon as the
    /// distance is known to exceed it.
    /// </summary>
    public static double Weighted(string typed, string target, SwipeKeyMap? keys, double max)
    {
        int n = typed.Length, m = target.Length;
        int width = m + 1;
        int size = (n + 1) * width;
        Span<double> d = size <= 1024 ? stackalloc double[size] : new double[size];

        d[0] = 0;
        for (int i = 1; i <= n; i++) d[i * width] = d[(i - 1) * width] + IndelCost(typed, i - 1);
        for (int j = 1; j <= m; j++) d[j] = d[j - 1] + IndelCost(target, j - 1);

        for (int i = 1; i <= n; i++)
        {
            double rowMin = double.MaxValue;
            char a = typed[i - 1];
            for (int j = 1; j <= m; j++)
            {
                char b = target[j - 1];
                double substitute = a == b ? 0 : SubstitutionCost(a, b, keys);
                double value = Math.Min(
                    Math.Min(d[(i - 1) * width + j] + IndelCost(typed, i - 1), d[i * width + j - 1] + IndelCost(target, j - 1)),
                    d[(i - 1) * width + j - 1] + substitute);
                if (i > 1 && j > 1 && a == target[j - 2] && typed[i - 2] == b && a != b)
                    value = Math.Min(value, d[(i - 2) * width + j - 2] + Transposition);
                d[i * width + j] = value;
                if (value < rowMin) rowMin = value;
            }
            if (rowMin > max) return rowMin;
        }
        return d[n * width + m];
    }

    static double IndelCost(string s, int index) => index > 0 && s[index] == s[index - 1] ? RepeatedIndel : Indel;

    static double SubstitutionCost(char a, char b, SwipeKeyMap? keys)
    {
        if (keys != null && keys.TryGetCenter(a, out var pa) && keys.TryGetCenter(b, out var pb) &&
            Vec2.Distance(pa, pb) <= NeighborDistance)
            return NeighborSubstitution;
        return Substitution;
    }
}
