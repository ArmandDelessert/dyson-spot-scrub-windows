namespace DyssCockpit.Presentation.Map;

/// <summary>A point or a displacement on a plane: world metres or screen pixels, depending on where it is used.</summary>
public readonly record struct Vec2(double X, double Y)
{
    public double Length => Math.Sqrt(X * X + Y * Y);

    public static Vec2 Of(DyssCockpit.Core.Point p) => new(p.X, p.Y);
    public DyssCockpit.Core.Point ToPoint() => new(X, Y);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 v, double k) => new(v.X * k, v.Y * k);
}

/// <summary>A width and a height, in pixels.</summary>
public readonly record struct Size2(double Width, double Height);

/// <summary>An upright rectangle. Top is the smaller y, whichever way y points.</summary>
public readonly record struct Rect2(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public Vec2 TopLeft => new(Left, Top);
    public Vec2 TopRight => new(Right, Top);
    public Vec2 BottomLeft => new(Left, Bottom);
    public Vec2 BottomRight => new(Right, Bottom);

    /// <summary>The rectangle with these two opposite corners, in any order.</summary>
    public static Rect2 FromCorners(Vec2 a, Vec2 b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}

/// <summary>
/// An affine transform of the plane, the way WPF's Matrix and Win2D's Matrix3x2 lay one out — row
/// vectors, so a point maps to (x·M11 + y·M21 + OffsetX, x·M12 + y·M22 + OffsetY) — but in doubles:
/// the map's transforms go from metres to pixels and back, and picking points on the robot's 5 cm
/// grid needs the precision. Immutable; each operation returns the transform followed by it.
/// </summary>
public readonly record struct MapTransform(double M11, double M12, double M21, double M22, double OffsetX, double OffsetY)
{
    public static readonly MapTransform Identity = new(1, 0, 0, 1, 0, 0);

    public double Determinant => M11 * M22 - M12 * M21;

    /// <summary>Ten times the machine epsilon: a determinant smaller than this counts as zero, as in WPF.</summary>
    private const double Zero = 10 * 2.2204460492503131e-16;

    /// <summary>Whether the transform can be undone; a singular one flattens the plane onto a line.</summary>
    public bool HasInverse => Math.Abs(Determinant) >= Zero;

    /// <summary>How long one unit becomes, whichever way the plane is turned: pixels per metre for the map's transform.</summary>
    public double ScaleFactor => Math.Sqrt(M11 * M11 + M12 * M12);

    public Vec2 Transform(Vec2 p) => new(p.X * M11 + p.Y * M21 + OffsetX, p.X * M12 + p.Y * M22 + OffsetY);
    public Vec2 Transform(DyssCockpit.Core.Point p) => Transform(Vec2.Of(p));
    public Vec2 Transform(double x, double y) => Transform(new Vec2(x, y));

    /// <summary>This transform, then <paramref name="next"/>.</summary>
    public MapTransform Then(MapTransform next) => new(
        M11 * next.M11 + M12 * next.M21,
        M11 * next.M12 + M12 * next.M22,
        M21 * next.M11 + M22 * next.M21,
        M21 * next.M12 + M22 * next.M22,
        OffsetX * next.M11 + OffsetY * next.M21 + next.OffsetX,
        OffsetX * next.M12 + OffsetY * next.M22 + next.OffsetY);

    public MapTransform Scaled(double sx, double sy) => Then(new(sx, 0, 0, sy, 0, 0));

    /// <summary>Scaled about the point (<paramref name="cx"/>, <paramref name="cy"/>), which stays where it is.</summary>
    public MapTransform ScaledAt(double sx, double sy, double cx, double cy) => Then(new(sx, 0, 0, sy, cx - sx * cx, cy - sy * cy));

    public MapTransform Translated(double dx, double dy) => this with { OffsetX = OffsetX + dx, OffsetY = OffsetY + dy };

    /// <summary>Turned about the origin by <paramref name="degrees"/>: clockwise on a screen, where y points down.</summary>
    public MapTransform Rotated(double degrees)
    {
        var radians = degrees % 360 * (Math.PI / 180);
        var (sin, cos) = (Math.Sin(radians), Math.Cos(radians));
        return Then(new(cos, sin, -sin, cos, 0, 0));
    }

    /// <summary>The transform that undoes this one, or null when there is none.</summary>
    public MapTransform? Inverse()
    {
        if (!HasInverse) return null;
        var k = 1 / Determinant;
        return new(M22 * k, -M12 * k, -M21 * k, M11 * k,
            (M21 * OffsetY - OffsetX * M22) * k, (OffsetX * M12 - M11 * OffsetY) * k);
    }
}
