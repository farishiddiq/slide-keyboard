using SlideKeyboard.Core.Geometry;

namespace SlideKeyboard.Core.Layout;

/// <summary>A fully positioned keyboard page. Geometry is in keyboard units; the UI scales it to pixels.</summary>
public sealed class KeyboardLayout
{
    public KeyboardLayout(KeyboardPage page, IReadOnlyList<KeyDef> keys, double width, double height, bool rtl)
    {
        Page = page;
        Keys = keys;
        Width = width;
        Height = height;
        Rtl = rtl;
    }

    public KeyboardPage Page { get; }
    public IReadOnlyList<KeyDef> Keys { get; }
    public double Width { get; }
    public double Height { get; }
    public bool Rtl { get; }

    /// <summary>Key under the point, or the nearest key when the point falls in a gap.</summary>
    public KeyDef? HitTest(Vec2 p)
    {
        KeyDef? best = null;
        double bestDistance = double.MaxValue;
        foreach (var key in Keys)
        {
            if (key.Bounds.Contains(p)) return key;
            double d = key.Bounds.DistanceTo(p);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = key;
            }
        }
        return bestDistance <= 0.6 ? best : null;
    }

    public KeyDef? Find(KeyKind kind) => Keys.FirstOrDefault(k => k.Kind == kind);
}
