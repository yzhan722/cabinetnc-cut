using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class NestDragTests
{
    static Panel Rect(double w, double h) => new()
    {
        PanelId = "P",
        ThicknessMm = 18,
        Outline = new Outline
        {
            Points = [new(0, 0), new(w, 0), new(w, h), new(0, h)],
        },
    };

    [Fact]
    public void RotateClockwise90_cycles_cardinals()
    {
        Assert.Equal(90, NestDrag.RotateClockwise90(0));
        Assert.Equal(180, NestDrag.RotateClockwise90(90));
        Assert.Equal(270, NestDrag.RotateClockwise90(180));
        Assert.Equal(0, NestDrag.RotateClockwise90(270));
        Assert.Equal(0, NestDrag.RotateClockwise90(-90));
    }

    [Fact]
    public void PackHoldCluster_row_then_wraps()
    {
        (string Id, double W, double H, double Rot)[] parts =
        [
            ("A", 100, 40, 0),
            ("B", 50, 40, 0),
            ("C", 80, 20, 0),
        ];
        var row = NestDrag.PackHoldCluster(parts, spacingMm: 12, maxWidth: 1000);
        Assert.Equal(100 + 12 + 50 + 12 + 80, row.GroupW, 6);
        Assert.Equal(40, row.GroupH, 6);
        Assert.Equal(0, row.Parts[0].LocalOx, 6);
        Assert.Equal(112, row.Parts[1].LocalOx, 6);
        Assert.Equal(174, row.Parts[2].LocalOx, 6);

        var wrapped = NestDrag.PackHoldCluster(parts, spacingMm: 12, maxWidth: 160);
        Assert.Equal(0, wrapped.Parts[0].LocalOy, 6);
        Assert.Equal(0, wrapped.Parts[1].LocalOx, 6);
        Assert.Equal(40 + 12, wrapped.Parts[1].LocalOy, 6);
        Assert.Equal(50 + 12, wrapped.Parts[2].LocalOx, 6);
        Assert.Equal(wrapped.Parts[1].LocalOy, wrapped.Parts[2].LocalOy, 6);
        Assert.Equal(Math.Max(100, 50 + 12 + 80), wrapped.GroupW, 6);
        Assert.Equal(40 + 12 + 40, wrapped.GroupH, 6);
    }

    [Fact]
    public void OffsetCenteredOn_puts_aabb_centre_on_cursor()
    {
        var panel = Rect(100, 40);
        var (ox, oy) = NestDrag.OffsetCenteredOn(panel, 80, 50, 0);
        Assert.Equal(30, ox, 6);
        Assert.Equal(30, oy, 6);
        var (w, h) = NestDrag.SizeRotated(panel, 0);
        Assert.Equal(80, ox + w / 2, 6);
        Assert.Equal(50, oy + h / 2, 6);
    }

    [Fact]
    public void OffsetKeepingCenter_swaps_aabb_without_moving_centre()
    {
        var panel = Rect(100, 40);
        var (ox, oy) = NestDrag.OffsetKeepingCenter(panel, 50, 20, 0, 90);
        Assert.Equal(80, ox, 6);
        Assert.Equal(-10, oy, 6);
        var (w0, h0) = NestDrag.SizeRotated(panel, 0);
        var (w1, h1) = NestDrag.SizeRotated(panel, 90);
        Assert.Equal(50 + w0 / 2, ox + w1 / 2, 6);
        Assert.Equal(20 + h0 / 2, oy + h1 / 2, 6);
    }

    [Fact]
    public void CardinalDelta_picks_dominant_axis()
    {
        Assert.Equal((12, 0), NestDrag.CardinalDelta(12, 5));
        Assert.Equal((0, -9), NestDrag.CardinalDelta(3, -9));
        Assert.Equal((4, 0), NestDrag.CardinalDelta(4, 4));
    }

    [Fact]
    public void RotatedOutline_90_matches_swapped_aabb()
    {
        (double X, double Y)[] pts = [(0, 0), (100, 0), (100, 40), (0, 40)];
        var rotated = NestTransform.RotatedOutline(pts, 90);
        var b = NestTransform.BoundsOf(rotated);
        Assert.Equal(40, b.MaxX - b.MinX, 6);
        Assert.Equal(100, b.MaxY - b.MinY, 6);
    }

    [Fact]
    public void BoxSelect_right_is_window_left_is_crossing()
    {
        var parts = new (string Id, double MinX, double MinY, double MaxX, double MaxY)[]
        {
            ("IN", 20, 20, 40, 40),
            ("TOUCH", 45, 10, 80, 30),
            ("OUT", 90, 90, 110, 110),
        };
        // Right-drag window 10,10 → 50,50: only fully inside.
        var window = NestDrag.BoxSelect(parts, 10, 10, 50, 50);
        Assert.Equal(["IN"], window);
        // Left-drag crossing 50,50 → 10,10: inside + overlapping.
        var crossing = NestDrag.BoxSelect(parts, 50, 50, 10, 10);
        Assert.Equal(["IN", "TOUCH"], crossing);
    }

    [Fact]
    public void SlideTo_stops_at_spacing_and_slides_along()
    {
        var a = Rect(100, 40);
        a = new Panel { PanelId = "A", ThicknessMm = 18, Outline = a.Outline };
        var b = new Panel
        {
            PanelId = "B",
            ThicknessMm = 18,
            Outline = new Outline { Points = [new(0, 0), new(100, 0), new(100, 40), new(0, 40)] },
        };
        var byId = new Dictionary<string, Panel>(StringComparer.Ordinal) { ["A"] = a, ["B"] = b };
        var others = new[] { ("B", 0, 112.0, 0.0, 0.0) };
        var moving = new NestDrag.SlideMember[] { new("A", a, 0, 0, 0) };

        var blocked = NestDrag.SlideTo(
            moving, "A", 0, 0, 40, 0, 0, others, byId,
            1220, 2440, spacingMm: 12, borderMm: 0, safeOx: 0, safeOy: 0);
        Assert.Equal(0, blocked.Ox, 2);

        var along = NestDrag.SlideTo(
            moving, "A", 0, 0, 0, 80, 0, others, byId,
            1220, 2440, spacingMm: 12, borderMm: 0, safeOx: 0, safeOy: 0);
        Assert.Equal(0, along.Ox, 2);
        Assert.Equal(80, along.Oy, 2);
    }

    [Fact]
    public void SlideTo_cardinal_then_hard_stops_on_axis()
    {
        var a = new Panel
        {
            PanelId = "A",
            ThicknessMm = 18,
            Outline = new Outline { Points = [new(0, 0), new(100, 0), new(100, 40), new(0, 40)] },
        };
        var wall = new Panel
        {
            PanelId = "W",
            ThicknessMm = 18,
            Outline = new Outline { Points = [new(0, 0), new(40, 0), new(40, 400), new(0, 400)] },
        };
        var byId = new Dictionary<string, Panel>(StringComparer.Ordinal) { ["A"] = a, ["W"] = wall };
        var others = new[] { ("W", 0, 160.0, 0.0, 0.0) };
        var moving = new NestDrag.SlideMember[] { new("A", a, 0, 0, 0) };
        // A is 100 wide at x=0; wall at 160; gap 12 → max ox = 160-12-100 = 48
        var (dx, dy) = NestDrag.CardinalDelta(80, 10);
        Assert.Equal((80, 0), (dx, dy));
        var toX = 0 + dx;
        var toY = 0 + dy;
        var slid = NestDrag.SlideTo(
            moving, "A", 0, 0, toX, toY, 0, others, byId,
            1220, 2440, spacingMm: 12, borderMm: 0, safeOx: 0, safeOy: 0);
        Assert.Equal(48, slid.Ox, 2);
        Assert.Equal(0, slid.Oy, 2);
    }

    [Fact]
    public void Resolve_ignores_part_in_part_host_and_stays_put()
    {
        var host = Rect(400, 300);
        host = new Panel { PanelId = "HOST", ThicknessMm = 18, Outline = host.Outline };
        var child = new Panel
        {
            PanelId = "CHILD",
            ThicknessMm = 18,
            Outline = new Outline { Points = [new(0, 0), new(80, 0), new(80, 60), new(0, 60)] },
        };
        var byId = new Dictionary<string, Panel>(StringComparer.Ordinal)
        {
            ["HOST"] = host,
            ["CHILD"] = child,
        };
        var others = new[] { ("HOST", 0, 15.0, 15.0, 0.0) };
        var ignore = new HashSet<(string, string)> { ("CHILD", "HOST"), ("HOST", "CHILD") };

        var blocked = NestDrag.Resolve(
            child, "CHILD", 40, 40, 0, 0, others, byId,
            1220, 2440, 12, 15, (20, 20), allowOverlap: false);
        Assert.True(blocked.Blocked);
        Assert.Equal(20, blocked.Ox, 6);

        var ok = NestDrag.Resolve(
            child, "CHILD", 40, 40, 0, 0, others, byId,
            1220, 2440, 12, 15, (20, 20), allowOverlap: false, ignore);
        Assert.False(ok.Blocked);
        Assert.Equal(40, ok.Ox, 6);
        Assert.Equal(40, ok.Oy, 6);
    }

    [Fact]
    public void Resolve_true_shape_keeps_interlocked_triangles()
    {
        var a = RightTri("A");
        var b = RightTri("B");
        var byId = new Dictionary<string, Panel>(StringComparer.Ordinal)
        {
            ["A"] = a,
            ["B"] = b,
        };
        var others = new[] { ("A", 0, 0.0, 0.0, 0.0) };

        var aabb = NestDrag.Resolve(
            b, "B", 20, 20, 180, 0, others, byId,
            1200, 2400, 8, 10, (0, 0), allowOverlap: false, ignorePairs: null, trueShape: false);
        Assert.True(aabb.Blocked);

        var poly = NestDrag.Resolve(
            b, "B", 20, 20, 180, 0, others, byId,
            1200, 2400, 0, 10, (0, 0), allowOverlap: false, ignorePairs: null, trueShape: true);
        Assert.False(poly.Blocked);
        Assert.Equal(20, poly.Ox, 6);
        Assert.Equal(20, poly.Oy, 6);
    }

    static Panel RightTri(string id) => new()
    {
        PanelId = id,
        ThicknessMm = 18,
        Outline = new Outline
        {
            Points = [new(0, 0), new(200, 0), new(0, 160)],
            Closed = true,
        },
    };

    [Fact]
    public void ClampInBounds_keeps_child_inside_void()
    {
        var child = new Panel
        {
            PanelId = "CHILD",
            ThicknessMm = 18,
            Outline = new Outline { Points = [new(0, 0), new(80, 0), new(80, 60), new(0, 60)] },
        };
        var (ox, oy) = NestDrag.ClampInBounds(child, 0, 0, 0, 100, 50, 300, 200);
        Assert.Equal(100, ox, 6);
        Assert.Equal(50, oy, 6);
        var (ox2, oy2) = NestDrag.ClampInBounds(child, 400, 400, 0, 100, 50, 300, 200);
        Assert.Equal(220, ox2, 6);
        Assert.Equal(140, oy2, 6);
    }
}
