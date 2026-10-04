using System.Globalization;

namespace Dyss.Presentation.Map;

/// <summary>One piece of a <see cref="PathFigure"/>, from wherever the previous one ended.</summary>
public abstract record PathSegment(Vec2 End);
public sealed record LineSegment(Vec2 End) : PathSegment(End);
/// <summary>A quadratic Bézier curve bent towards <paramref name="Control"/>.</summary>
public sealed record QuadraticSegment(Vec2 Control, Vec2 End) : PathSegment(End);
/// <summary>An elliptical arc, as SVG's "A" describes it: <paramref name="Clockwise"/> is its sweep flag, in a plane whose y points down.</summary>
public sealed record ArcSegment(Vec2 End, double RadiusX, double RadiusY, double RotationDegrees, bool LargeArc, bool Clockwise) : PathSegment(End);

/// <summary>A run of connected segments from <see cref="Start"/>, closed back to it or left open.</summary>
public sealed record PathFigure(Vec2 Start, IReadOnlyList<PathSegment> Segments, bool Closed);

/// <summary>
/// The SVG path mini-language, as far as the icons use it: absolute M, L, H, V, Q, A and Z. Each
/// renderer turns the figures into its own geometry; WPF could parse the strings itself, Win2D cannot.
/// </summary>
public static class PathData
{
    public static IReadOnlyList<PathFigure> Parse(string data)
    {
        var figures = new List<PathFigure>();
        var tokens = Tokenise(data);
        var i = 0;
        Vec2? start = null;
        var current = default(Vec2);
        var segments = new List<PathSegment>();
        char command = '\0';

        while (i < tokens.Count)
        {
            if (tokens[i] is [var c] && char.IsLetter(c)) { command = c; i++; }
            switch (command)
            {
                case 'M':
                    Flush(closed: false);
                    current = Point();
                    start = current;
                    command = 'L';   // further pairs after an M are lines
                    break;
                case 'L': Add(new LineSegment(Point())); break;
                case 'H': Add(new LineSegment(new Vec2(Number(), current.Y))); break;
                case 'V': Add(new LineSegment(new Vec2(current.X, Number()))); break;
                case 'Q':
                    var control = Point();
                    Add(new QuadraticSegment(control, Point()));
                    break;
                case 'A':
                    var (rx, ry, rotation) = (Number(), Number(), Number());
                    var (large, sweep) = (Number() != 0, Number() != 0);
                    Add(new ArcSegment(Point(), rx, ry, rotation, large, sweep));
                    break;
                case 'Z':
                    Flush(closed: true);
                    break;
                default:
                    throw new FormatException($"Commande de tracé non prise en charge : « {command} » dans « {data} ».");
            }
        }
        Flush(closed: false);
        return figures;

        double Number() => double.Parse(tokens[i++], NumberStyles.Float, CultureInfo.InvariantCulture);
        Vec2 Point() => new(Number(), Number());

        void Add(PathSegment segment)
        {
            segments.Add(segment);
            current = segment.End;
        }

        void Flush(bool closed)
        {
            if (start is { } s && segments.Count > 0) figures.Add(new PathFigure(s, [.. segments], closed));
            segments.Clear();
            // A closed figure ends where it began; either way the next one starts with an M.
            if (closed && start is { } back) current = back;
            start = null;
        }
    }

    /// <summary>Commands and numbers, whatever separates them: spaces, commas, or nothing before a command letter or a minus sign.</summary>
    private static List<string> Tokenise(string data)
    {
        var tokens = new List<string>();
        var number = new System.Text.StringBuilder();
        void End()
        {
            if (number.Length > 0) tokens.Add(number.ToString());
            number.Clear();
        }
        foreach (var c in data)
        {
            if (char.IsLetter(c) && c is not ('e' or 'E')) { End(); tokens.Add(c.ToString()); }
            else if (c is ' ' or ',' or '\t' or '\n' or '\r') End();
            else if (c == '-' && number.Length > 0 && number[^1] is not ('e' or 'E')) { End(); number.Append(c); }
            else number.Append(c);
        }
        End();
        return tokens;
    }
}
