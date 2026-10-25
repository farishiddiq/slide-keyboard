namespace SlideKeyboard.Core.Geometry;

/// <summary>A 2D point/vector in keyboard units (1 unit = one standard key width / row height).</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);

    public double Length => Math.Sqrt(X * X + Y * Y);

    public static double Distance(Vec2 a, Vec2 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static Vec2 Lerp(Vec2 a, Vec2 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}

/// <summary>An axis-aligned rectangle in keyboard units.</summary>
public readonly record struct RectU(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public Vec2 Center => new(X + Width / 2, Y + Height / 2);

    public bool Contains(Vec2 p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

    /// <summary>Distance from the point to the rectangle edge (0 when inside).</summary>
    public double DistanceTo(Vec2 p)
    {
        double dx = Math.Max(Math.Max(X - p.X, 0), p.X - Right);
        double dy = Math.Max(Math.Max(Y - p.Y, 0), p.Y - Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

public static class PathMath
{
    public static double Length(IReadOnlyList<Vec2> points)
    {
        double length = 0;
        for (int i = 1; i < points.Count; i++)
            length += Vec2.Distance(points[i - 1], points[i]);
        return length;
    }

    /// <summary>Resamples a polyline into <paramref name="count"/> points spaced equally along its length.</summary>
    public static Vec2[] Resample(IReadOnlyList<Vec2> points, int count)
    {
        var result = new Vec2[count];
        if (points.Count == 0 || count == 0) return result;

        double total = Length(points);
        if (points.Count == 1 || total <= 1e-9)
        {
            Array.Fill(result, points[0]);
            return result;
        }

        double step = total / (count - 1);
        double target = step;
        double walked = 0;
        int k = 1;
        result[0] = points[0];

        for (int i = 1; i < points.Count && k < count - 1; i++)
        {
            Vec2 a = points[i - 1], b = points[i];
            double segment = Vec2.Distance(a, b);
            while (k < count - 1 && walked + segment >= target)
            {
                double t = segment <= 1e-12 ? 0 : (target - walked) / segment;
                result[k++] = Vec2.Lerp(a, b, t);
                target += step;
            }
            walked += segment;
        }

        while (k < count) result[k++] = points[^1];
        return result;
    }
}
