using System.Text.Json.Nodes;

using static Dyss.Core.Translation;

namespace Dyss.Core;

/// <summary>
/// A kind of restriction zone: its jdm type for set_virtual_wall, its REST behaviour in
/// persistent-maps, and its name in the current language. The pairing was established on 2026-09-23 by matching the
/// coordinates of the same rectangles on both sides. The names mostly follow the phone app's; two
/// are reworded as nouns, "Zone à éviter" and "Seuil à franchir" for its "Éviter la zone" and
/// "Franchir le seuil", so all four read as what the zone is.
/// </summary>
public sealed record RestrictionKind(int JdmType, string Behavior, string Label, string Effect)
{
    public static readonly IReadOnlyList<RestrictionKind> All =
    [
        new(2, "keepOut", T("Zone à éviter", "No-go zone"), T("le robot n'y va pas", "the robot stays out")),
        new(13, "climbObstacle", T("Seuil à franchir", "Threshold to climb"), T("le robot tente de franchir les petits obstacles", "the robot tries to climb small obstacles")),
        new(12, "brushBarOff", T("Lavage uniquement", "Mop only"), T("le robot y passe sans la brosse", "the robot goes there without its brush bar")),
        new(6, "noMop", T("Aspirateur uniquement", "Vacuum only"), T("le robot y passe sans laver", "the robot goes there without mopping")),
    ];

    public static RestrictionKind? FromBehavior(string? behavior) => All.FirstOrDefault(k => k.Behavior == behavior);
    public static RestrictionKind? FromJdm(int type) => All.FirstOrDefault(k => k.JdmType == type);

    public override string ToString() => Label;
}

/// <summary>
/// A piece of furniture the phone app offers: jdm code for adjust_furniture, REST type in
/// persistent-maps, its name and the app's category in the current language, and the size it had in the captures
/// (<see cref="Length"/> along the first side, from corner 1 to 2, <see cref="Width"/> along the
/// second). Codes established on 2026-09-25 by moving one piece on a map carrying all of them.
/// </summary>
public sealed record FurnitureKind(int Code, string RestType, string Label, string Category, double Length, double Width)
{
    public static readonly IReadOnlyList<FurnitureKind> All =
    [
        new(1513, "doubleBed", T("Lit double", "Double bed"), T("Lits", "Beds"), 2.1, 1.8),
        new(1601, "singleBed", T("Lit simple", "Single bed"), T("Lits", "Beds"), 2.1, 1.2),
        new(1519, "bedsideTable", T("Table de chevet", "Bedside table"), T("Lits", "Beds"), 0.6, 0.8),
        new(1528, "singleSeaterSofa", T("Fauteuil", "Armchair"), T("Assises", "Seating"), 1.0, 0.9),
        new(1512, "twoSeaterSofa", T("Canapé deux places", "Two-seater sofa"), T("Assises", "Seating"), 1.1, 1.8),
        new(1525, "threeSeaterSofa", T("Canapé trois places", "Three-seater sofa"), T("Assises", "Seating"), 1.1, 2.5),
        new(1526, "lShapedSofaLeft", T("Canapé d'angle, à gauche", "L-shaped sofa, left"), T("Assises", "Seating"), 1.7, 2.4),
        new(1527, "lShapedSofaRight", T("Canapé d'angle, à droite", "L-shaped sofa, right"), T("Assises", "Seating"), 1.8, 2.4),
        new(1511, "diningTableAndChairs", T("Table et chaises", "Dining table and chairs"), T("Tables", "Tables"), 1.4, 2.0),
        new(1518, "squareCoffeeTable", T("Table basse carrée", "Square coffee table"), T("Tables", "Tables"), 0.5, 1.1),
        new(1602, "roundCoffeeTable", T("Table basse ronde", "Round coffee table"), T("Tables", "Tables"), 1.1, 1.1),
        new(1603, "desk", T("Bureau", "Desk"), T("Tables", "Tables"), 1.4, 1.6),
        new(1515, "cabinet", T("Meuble", "Cabinet"), T("Rangements", "Storage"), 0.9, 2.1),
        new(1520, "tvStand", T("Meuble TV", "TV stand"), T("Rangements", "Storage"), 0.6, 2.3),
        new(1604, "storageCabinet", T("Meuble de rangement", "Storage cabinet"), T("Rangements", "Storage"), 0.4, 1.4),
        new(1605, "shoeCabinet", T("Meuble à chaussures", "Shoe cabinet"), T("Rangements", "Storage"), 0.4, 1.4),
        new(1606, "wardrobe", T("Armoire", "Wardrobe"), T("Rangements", "Storage"), 1.0, 1.8),
        new(1607, "bookshelf", T("Bibliothèque", "Bookshelf"), T("Rangements", "Storage"), 0.4, 1.4),
        new(1516, "refrigerator", T("Réfrigérateur", "Refrigerator"), T("Électroménager", "Appliances"), 0.8, 0.8),
        new(1524, "washingMachine", T("Lave-linge", "Washing machine"), T("Électroménager", "Appliances"), 1.0, 0.9),
        new(1608, "cabinetWithStove", T("Meuble avec cuisinière", "Cabinet with stove"), T("Électroménager", "Appliances"), 0.6, 0.6),
        new(1514, "toilet", T("Toilettes", "Toilet"), T("Autres", "Other"), 0.7, 0.5),
        new(1613, "indoorPlant", T("Plante", "Indoor plant"), T("Autres", "Other"), 0.5, 0.5),
        new(1614, "standingMirror", T("Miroir sur pied", "Standing mirror"), T("Autres", "Other"), 0.5, 0.7),
    ];

    public static FurnitureKind? FromRestType(string? type) => All.FirstOrDefault(k => k.RestType == type);
    public static FurnitureKind? FromCode(int code) => All.FirstOrDefault(k => k.Code == code);

    public string Display => $"{Category} · {Label}";
    public override string ToString() => Display;
}

/// <summary>Plane geometry for the rectangles zones and furniture are made of, in world metres.</summary>
public static class MapShapes
{
    /// <summary>Slack when comparing lengths, so a side worked out as 0.2999999 m still counts as 0.3 m.</summary>
    public const double Tolerance = 1e-6;

    /// <summary>
    /// The four corners of the upright rectangle spanned by two opposite corners, in the order the
    /// phone sends them: top-left, bottom-left, bottom-right, top-right, with y pointing up.
    /// </summary>
    public static Point[] Rectangle(Point a, Point b)
    {
        double left = Math.Min(a.X, b.X), right = Math.Max(a.X, b.X), bottom = Math.Min(a.Y, b.Y), top = Math.Max(a.Y, b.Y);
        return [new(left, top), new(left, bottom), new(right, bottom), new(right, top)];
    }

    /// <summary>
    /// <paramref name="corner"/>, pushed away from <paramref name="anchor"/> along each axis until
    /// the rectangle they span is at least <paramref name="minimum"/> wide, so a rectangle being
    /// drawn or resized stops at the smallest size accepted instead of shrinking below it. Each
    /// axis keeps the side of <paramref name="side"/> when given (a resize never flips the shape),
    /// or else the side the corner is on (a drawing follows the pointer).
    /// </summary>
    public static Point KeepMinimum(Point anchor, Point corner, double minimum, Point? side = null)
    {
        if (minimum <= 0) return corner;
        double Axis(double a, double c, double? s)
        {
            var direction = (s ?? c) - a < 0 ? -1 : 1;
            return (c - a) * direction >= minimum ? c : a + direction * minimum;
        }
        return new Point(Axis(anchor.X, corner.X, side?.X), Axis(anchor.Y, corner.Y, side?.Y));
    }

    /// <summary>An upright rectangle centred on a point, <paramref name="length"/> along the first side (vertical) and <paramref name="width"/> along the second.</summary>
    public static Point[] Centred(Point centre, double length, double width) =>
        Rectangle(new(centre.X - width / 2, centre.Y + length / 2), new(centre.X + width / 2, centre.Y - length / 2));

    public static Point Centre(IReadOnlyList<Point> corners) =>
        new(corners.Average(p => p.X), corners.Average(p => p.Y));

    public static Point[] Translate(IReadOnlyList<Point> corners, double dx, double dy) =>
        [.. corners.Select(p => new Point(p.X + dx, p.Y + dy))];

    /// <summary>A quarter turn clockwise about the shape's centre (y pointing up), which keeps the corners in their order.</summary>
    public static Point[] RotateClockwise(IReadOnlyList<Point> corners)
    {
        var c = Centre(corners);
        return [.. corners.Select(p => new Point(c.X + (p.Y - c.Y), c.Y - (p.X - c.X)))];
    }

    /// <summary>
    /// How many clockwise quarter turns a piece stands at, from the way <see cref="Centred"/> lays
    /// it out (first side pointing down): 0 to 3, or null when it sits at another angle.
    /// </summary>
    public static int? QuarterTurns(IReadOnlyList<Point> corners)
    {
        if (corners.Count < 2) return null;
        var angle = Math.Atan2(corners[1].Y - corners[0].Y, corners[1].X - corners[0].X) * 180 / Math.PI;
        // Straight down is -90°; each clockwise quarter turn takes another 90° off.
        var turns = ((-90 - angle) % 360 + 360) % 360 / 90;
        var nearest = Math.Round(turns);
        return Math.Abs(turns - nearest) * 90 <= 2 ? (int)nearest % 4 : null;
    }

    /// <summary>A piece laid out as <see cref="Centred"/> does, then turned clockwise the given number of quarter turns.</summary>
    public static Point[] Oriented(Point centre, double length, double width, int quarterTurns)
    {
        var corners = Centred(centre, length, width);
        for (var i = 0; i < ((quarterTurns % 4) + 4) % 4; i++) corners = RotateClockwise(corners);
        return corners;
    }

    /// <summary>The lengths of the first two sides, corner 1 to 2 then 2 to 3.</summary>
    public static (double First, double Second) Sides(IReadOnlyList<Point> corners) =>
        corners.Count < 3 ? (0, 0) : (Distance(corners[0], corners[1]), Distance(corners[1], corners[2]));

    public static double Area(IReadOnlyList<Point> polygon)
    {
        var sum = 0.0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(sum) / 2;
    }

    /// <summary>Whether the point lies inside the polygon (even-odd rule).</summary>
    public static bool Contains(IReadOnlyList<Point> polygon, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

/// <summary>A restriction zone as sent to the robot.</summary>
public sealed record RestrictionZone(RestrictionKind Kind, IReadOnlyList<Point> Corners);

/// <summary>A piece of furniture as sent to the robot. <see cref="Index"/> is the id persistent-maps gives it back under.</summary>
public sealed record FurniturePiece(int Index, FurnitureKind Kind, IReadOnlyList<Point> Corners);

public static class MapObjectCommands
{
    /// <summary>
    /// Replaces every restriction zone of the active map: set_virtual_wall carries the whole list,
    /// [count, [map_id, type, x1, y1 … x4, y4], …], and [0] clears them all. Adding or removing one
    /// zone therefore means sending all the others again.
    /// </summary>
    public static async Task<MapEditResult?> SetRestrictionsAsync(this IRobotCommands robot, long mapId, IReadOnlyList<RestrictionZone> zones, CancellationToken ct = default)
    {
        var virwall = new JsonArray(JsonValue.Create(zones.Count));   // the count comes first
        foreach (var z in zones)
        {
            var entry = new JsonArray(mapId, z.Kind.JdmType);
            foreach (var n in Coordinates(z.Corners)) entry.Add(n);
            virwall.Add(entry);
        }
        return MapEditResult.From(await robot.RequestJdmAsync("service.set_virtual_wall", new JsonObject { ["virwall"] = virwall }, ct: ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Replaces every piece of furniture of the active map. furniture_list is a string holding JSON,
    /// [[index, code, 1, x1, y1 … x4, y4], …], and "[]" clears them all; the call names no map.
    /// The robot answers with map_id 0, which counts as accepted.
    /// </summary>
    public static async Task<MapEditResult?> AdjustFurnitureAsync(this IRobotCommands robot, IReadOnlyList<FurniturePiece> furniture, DateTimeOffset? now = null, CancellationToken ct = default)
    {
        var list = new JsonArray();
        foreach (var f in furniture)
        {
            var entry = new JsonArray(f.Index, f.Kind.Code, 1);
            foreach (var n in Coordinates(f.Corners)) entry.Add(n);
            list.Add(entry);
        }
        return MapEditResult.From(await robot.RequestJdmAsync("service.adjust_furniture", new JsonObject
        {
            ["timestamp"] = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds(),
            // Presumably part and count, for splitting a long list; 22 pieces still fit in one.
            ["package"] = new JsonArray(1, 1),
            ["furniture_list"] = list.ToJsonString(),
        }, ct: ct).ConfigureAwait(false));
    }

    private static IEnumerable<JsonNode> Coordinates(IReadOnlyList<Point> corners)
    {
        if (corners.Count != 4) throw new ArgumentException("A zone or a piece of furniture has exactly four corners.", nameof(corners));
        foreach (var p in corners)
        {
            yield return JsonValue.Create(p.X);
            yield return JsonValue.Create(p.Y);
        }
    }
}
