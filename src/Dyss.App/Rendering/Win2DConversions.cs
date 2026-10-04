using System.Numerics;
using Dyss.Presentation.Map;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.UI;

namespace Dyss.App.Rendering;

/// <summary>The map's framework-free types as Win2D takes them.</summary>
internal static class Win2DConversions
{
    public static Color ToColor(this ArgbColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
    public static Vector2 ToVector2(this Vec2 v) => new((float)v.X, (float)v.Y);
    public static Rect ToRect(this Rect2 r) => new(r.X, r.Y, r.Width, r.Height);
    public static Matrix3x2 ToMatrix(this MapTransform m) =>
        new((float)m.M11, (float)m.M12, (float)m.M21, (float)m.M22, (float)m.OffsetX, (float)m.OffsetY);
    public static Vec2 ToVec2(this Point p) => new(p.X, p.Y);

    /// <summary>The figures of a <see cref="PathData"/> string as a Win2D geometry.</summary>
    public static CanvasGeometry ToGeometry(this IReadOnlyList<PathFigure> figures, ICanvasResourceCreator creator)
    {
        using var builder = new CanvasPathBuilder(creator);
        foreach (var figure in figures)
        {
            builder.BeginFigure(figure.Start.ToVector2());
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line:
                        builder.AddLine(line.End.ToVector2());
                        break;
                    case QuadraticSegment quad:
                        builder.AddQuadraticBezier(quad.Control.ToVector2(), quad.End.ToVector2());
                        break;
                    case ArcSegment arc:
                        builder.AddArc(arc.End.ToVector2(), (float)arc.RadiusX, (float)arc.RadiusY, (float)(arc.RotationDegrees * Math.PI / 180),
                            arc.Clockwise ? CanvasSweepDirection.Clockwise : CanvasSweepDirection.CounterClockwise,
                            arc.LargeArc ? CanvasArcSize.Large : CanvasArcSize.Small);
                        break;
                }
            }
            builder.EndFigure(figure.Closed ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
        }
        return CanvasGeometry.CreatePath(builder);
    }
}
