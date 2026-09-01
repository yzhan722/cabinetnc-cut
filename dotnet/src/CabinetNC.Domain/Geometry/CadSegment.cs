namespace CabinetNC.Domain.Geometry;

/// <summary>
/// Exact CAD entity for a profile loop. Old packages omit these and
/// still run the tessellated polyline + <see cref="PolylineArcFit"/> path.
/// </summary>
public readonly record struct CadSegment(
    string Type,
    Point2 Start,
    Point2 End,
    Point2? Center,
    double RadiusMm,
    bool Cw)
{
    public const string Line = "line";
    public const string Arc = "arc";
    public const string Circle = "circle";

    public bool IsLine => Type == Line;
    public bool IsArc => Type is Arc or Circle;
    public bool IsCircle => Type == Circle;

    public static CadSegment MakeLine(Point2 start, Point2 end) =>
        new(Line, start, end, null, 0, false);

    public static CadSegment MakeArc(Point2 start, Point2 end, Point2 center, double radius, bool cw) =>
        new(Arc, start, end, center, radius, cw);

    public static CadSegment MakeCircle(Point2 center, double radius, Point2? start = null, bool cw = false)
    {
        var p = start ?? new Point2(center.X + radius, center.Y);
        return new(Circle, p, p, center, radius, cw);
    }
}
