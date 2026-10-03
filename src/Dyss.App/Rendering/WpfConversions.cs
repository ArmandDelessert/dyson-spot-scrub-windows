using System.Windows;
using System.Windows.Media;
using Dyss.Presentation.Map;

namespace Dyss.App.Rendering;

/// <summary>The map's framework-free types as WPF knows them.</summary>
internal static class WpfConversions
{
    public static Color ToWpf(this ArgbColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
    public static Point ToWpf(this Vec2 v) => new(v.X, v.Y);
    public static Rect ToWpf(this Rect2 r) => new(r.X, r.Y, r.Width, r.Height);
    public static Matrix ToWpf(this MapTransform m) => new(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY);
    public static Vec2 ToVec2(this Point p) => new(p.X, p.Y);
    public static Vec2 ToVec2(this Vector v) => new(v.X, v.Y);
}
