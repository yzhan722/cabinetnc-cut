using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Manufacturing;

namespace CabinetNC.Domain.Tests;

public class ProfileBridgePlannerTests
{
    const double ToolD = 10;
    const double HitTol = 8;

    static IReadOnlyList<(double X, double Y)> Rect(double x, double y, double w, double h) =>
        [(x, y), (x + w, y), (x + w, y + h), (x, y + h)];

    static IReadOnlyList<Point2> Ring(double x, double y, double w, double h) =>
        [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)];

    static CutOp Contour(string panel, IReadOnlyList<(double X, double Y)> path, int sheet = 0) => new()
    {
        Op = "contour",
        PanelId = panel,
        Path = path,
        Placed = true,
        SheetIndex = sheet,
        ClosePath = true,
    };

    [Fact]
    public void Reproject_single_sheet_preserves_bridges_on_unrepresented_sheets()
    {
        var bridges = new[]
        {
            new ProfileBridge
            {
                Id = "a",
                PairId = "b",
                PanelId = "B",
                SheetIndex = 1,
                ArcLengthMm = 10,
                X = 10,
                Y = 0,
                WidthMm = 5,
            },
            new ProfileBridge
            {
                Id = "b",
                PairId = "a",
                PanelId = "C",
                SheetIndex = 1,
                ArcLengthMm = 20,
                X = 20,
                Y = 0,
                WidthMm = 5,
            },
        };

        var kept = ProfileBridgePlanner.Reproject(
            bridges,
            [Contour("A", Rect(0, 0, 100, 50), sheet: 0)]);

        Assert.Equal(2, kept.Count);
        Assert.Equal("b", kept.Single(x => x.Id == "a").PairId);
        Assert.Equal("a", kept.Single(x => x.Id == "b").PairId);
    }

    [Fact]
    public void EnsureFacingPairs_adds_missing_neighbor_tab()
    {
        var ops = new[]
        {
            Contour("A", Rect(0, 0, 100, 50)),
            Contour("B", Rect(112, 0, 100, 50)),
        };
        var outlines = new Dictionary<string, IReadOnlyList<Point2>>
        {
            ["A"] = Ring(0, 0, 100, 50),
            ["B"] = Ring(112, 0, 100, 50),
        };
        var onlyA = new[]
        {
            new ProfileBridge
            {
                Id = "a",
                PanelId = "A",
                SheetIndex = 0,
                ArcLengthMm = 125,
                X = 100,
                Y = 25,
                WidthMm = 5,
            },
        };

        var fixedUp = ProfileBridgePlanner.EnsureFacingPairs(onlyA, ops, outlines, ToolD);
        Assert.Equal(2, fixedUp.Count);
        var a = Assert.Single(fixedUp, b => b.PanelId == "A");
        var b = Assert.Single(fixedUp, x => x.PanelId == "B");
        Assert.Equal(b.Id, a.PairId);
        Assert.Equal(a.Id, b.PairId);
        Assert.Equal(112, b.X, 1);
        Assert.Equal(25, b.Y, 1);

        var again = ProfileBridgePlanner.EnsureFacingPairs(fixedUp, ops, outlines, ToolD);
        Assert.Equal(2, again.Count);
    }

    [Fact]
    public void Isolated_edge_places_a_single_bridge()
    {
        var path = Rect(0, 0, 100, 50);
        var ops = new[] { Contour("A", path) };
        var outlines = new Dictionary<string, IReadOnlyList<Point2>> { ["A"] = Ring(0, 0, 100, 50) };
        var result = ProfileBridgePlanner.HandleClick(
            [], ops, outlines, 0, 100, 25, ToolD, 5, HitTol);
        Assert.True(result.Changed);
        Assert.Single(result.Bridges);
        Assert.Null(result.Bridges[0].PairId);
        Assert.Equal("A", result.Bridges[0].PanelId);
        Assert.Equal(100, result.Bridges[0].X, 3);
        Assert.Equal(25, result.Bridges[0].Y, 3);
    }

    [Fact]
    public void Facing_edges_closer_than_two_diameters_place_a_forced_pair()
    {
        // A [0..100], B [112..212], gap 12 < 20
        var ops = new[]
        {
            Contour("A", Rect(0, 0, 100, 50)),
            Contour("B", Rect(112, 0, 100, 50)),
        };
        var outlines = new Dictionary<string, IReadOnlyList<Point2>>
        {
            ["A"] = Ring(0, 0, 100, 50),
            ["B"] = Ring(112, 0, 100, 50),
        };
        var result = ProfileBridgePlanner.HandleClick(
            [], ops, outlines, 0, 100, 25, ToolD, 5, HitTol);
        Assert.True(result.Changed);
        Assert.Equal(2, result.Bridges.Count);
        Assert.Contains(result.Bridges, b => b.PanelId == "A" && b.PairId is not null);
        Assert.Contains(result.Bridges, b => b.PanelId == "B" && b.PairId is not null);
        var a = result.Bridges.First(b => b.PanelId == "A");
        var b = result.Bridges.First(b => b.PanelId == "B");
        Assert.Equal(a.Id, b.PairId);
        Assert.Equal(b.Id, a.PairId);
        Assert.Equal(112, b.X, 2);
        Assert.Equal(25, b.Y, 2);
    }

    [Fact]
    public void Free_edge_of_adjacent_parts_stays_single()
    {
        var ops = new[]
        {
            Contour("A", Rect(0, 0, 100, 50)),
            Contour("B", Rect(112, 0, 100, 50)),
        };
        var outlines = new Dictionary<string, IReadOnlyList<Point2>>
        {
            ["A"] = Ring(0, 0, 100, 50),
            ["B"] = Ring(112, 0, 100, 50),
        };
        var result = ProfileBridgePlanner.HandleClick(
            [], ops, outlines, 0, 0, 25, ToolD, 5, HitTol);
        Assert.True(result.Changed);
        Assert.Single(result.Bridges);
        Assert.Null(result.Bridges[0].PairId);
        Assert.Equal("A", result.Bridges[0].PanelId);
    }

    [Fact]
    public void Facing_gap_at_or_above_two_diameters_is_single()
    {
        // gap 25 > 20
        var ops = new[]
        {
            Contour("A", Rect(0, 0, 100, 50)),
            Contour("B", Rect(125, 0, 100, 50)),
        };
        var outlines = new Dictionary<string, IReadOnlyList<Point2>>
        {
            ["A"] = Ring(0, 0, 100, 50),
            ["B"] = Ring(125, 0, 100, 50),
        };
        var result = ProfileBridgePlanner.HandleClick(
            [], ops, outlines, 0, 100, 25, ToolD, 5, HitTol);
        Assert.Single(result.Bridges);
        Assert.Null(result.Bridges[0].PairId);
    }

    [Fact]
    public void Clicking_a_symbol_in_delete_mode_removes_the_pair()
    {
        var ops = new[]
        {
            Contour("A", Rect(0, 0, 100, 50)),
            Contour("B", Rect(112, 0, 100, 50)),
        };
        var outlines = new Dictionary<string, IReadOnlyList<Point2>>
        {
            ["A"] = Ring(0, 0, 100, 50),
            ["B"] = Ring(112, 0, 100, 50),
        };
        var placed = ProfileBridgePlanner.HandleClick(
            [], ops, outlines, 0, 100, 25, ToolD, 5, HitTol);
        Assert.Equal(2, placed.Bridges.Count);
        var gone = ProfileBridgePlanner.HandleDelete(
            placed.Bridges, 0, 100, 25, HitTol);
        Assert.True(gone.Changed);
        Assert.Empty(gone.Bridges);
    }

    [Fact]
    public void Manual_click_on_a_symbol_does_not_delete()
    {
        var ops = new[] { Contour("A", Rect(0, 0, 100, 50)) };
        var outlines = new Dictionary<string, IReadOnlyList<Point2>> { ["A"] = Ring(0, 0, 100, 50) };
        var placed = ProfileBridgePlanner.HandleClick(
            [], ops, outlines, 0, 100, 25, ToolD, 5, HitTol);
        var again = ProfileBridgePlanner.HandleClick(
            placed.Bridges, ops, outlines, 0, 100, 25, ToolD, 5, HitTol);
        Assert.False(again.Changed);
        Assert.Single(again.Bridges);
    }

    [Fact]
    public void Missed_path_does_not_place()
    {
        var ops = new[] { Contour("A", Rect(0, 0, 100, 50)) };
        var outlines = new Dictionary<string, IReadOnlyList<Point2>> { ["A"] = Ring(0, 0, 100, 50) };
        var result = ProfileBridgePlanner.HandleClick(
            [], ops, outlines, 0, 50, 25, ToolD, 5, HitTol);
        Assert.False(result.Changed);
        Assert.Empty(result.Bridges);
    }

    [Fact]
    public void Caps_at_one_hundred_per_panel()
    {
        var ops = new[] { Contour("A", Rect(0, 0, 2000, 50)) };
        var outlines = new Dictionary<string, IReadOnlyList<Point2>> { ["A"] = Ring(0, 0, 2000, 50) };
        IReadOnlyList<ProfileBridge> cur = [];
        for (var i = 0; i < ProfileBridgePlanner.MaxPerPanel; i++)
        {
            var x = 10.0 + i * 15;
            var r = ProfileBridgePlanner.HandleClick(cur, ops, outlines, 0, x, 0, ToolD, 5, HitTol);
            Assert.True(r.Changed, $"place {i}");
            cur = r.Bridges;
        }
        Assert.Equal(ProfileBridgePlanner.MaxPerPanel, cur.Count);
        var extra = ProfileBridgePlanner.HandleClick(cur, ops, outlines, 0, 1990, 0, ToolD, 5, HitTol);
        Assert.False(extra.Changed);
        Assert.Equal(ProfileBridgePlanner.MaxPerPanel, extra.Bridges.Count);
    }

    [Fact]
    public void Classifies_narrow_long_parts_as_strips()
    {
        Assert.True(ProfileBridgePlanner.IsLongStrip(60, 1500));
        Assert.True(ProfileBridgePlanner.IsLongStrip(2000, 80));
        Assert.False(ProfileBridgePlanner.IsLongStrip(100, 1200));
        Assert.True(ProfileBridgePlanner.IsLongStrip(100, 1201));
        Assert.False(ProfileBridgePlanner.IsLongStrip(100, 500));
        Assert.False(ProfileBridgePlanner.IsLongStrip(400, 300));
        Assert.False(ProfileBridgePlanner.IsLongStrip(200, 200));
        Assert.True(ProfileBridgePlanner.IsLongStrip(100, 500, 4));
        Assert.True(ProfileBridgePlanner.IsSmallBoard(60, 1500));
        Assert.True(ProfileBridgePlanner.IsSmallBoard(300, 300));
        Assert.False(ProfileBridgePlanner.IsSmallBoard(500, 500));
        Assert.True(ProfileBridgePlanner.IsLargeBoard(800, 500));
        Assert.False(ProfileBridgePlanner.IsLargeBoard(200, 2500));
        Assert.Equal(3, ProfileBridgePlanner.TargetSmallBridges(0.09));
        Assert.Equal(2, ProfileBridgePlanner.TargetSmallBridges(0.12));
        Assert.Equal(2, ProfileBridgePlanner.TargetSmallBridges(0.15));
        Assert.Equal(3, ProfileBridgePlanner.TargetSmallBridges(0.25, 0.3));
    }

    [Fact]
    public void Auto_two_strips_pair_on_the_shared_seam_and_tab_both_ends()
    {
        var ops = new[]
        {
            Contour("A", Rect(0, 0, 60, 1500)),
            Contour("B", Rect(72, 0, 60, 1500)),
        };
        var outlines = new Dictionary<string, IReadOnlyList<Point2>>
        {
            ["A"] = Ring(0, 0, 60, 1500),
            ["B"] = Ring(72, 0, 60, 1500),
        };
        var result = ProfileBridgePlanner.AutoPlace([], ops, outlines, 0, ToolD, 5);
        Assert.True(result.Changed);
        var a = result.Bridges.Where(b => b.PanelId == "A").ToList();
        var b = result.Bridges.Where(b => b.PanelId == "B").ToList();
        Assert.True(a.Count >= 8, $"A count {a.Count}");
        Assert.True(b.Count >= 8, $"B count {b.Count}");
        Assert.Contains(a, p => Math.Abs(p.Y - 1500) < 2 && p.X > 10 && p.X < 50);
        Assert.Contains(a, p => Math.Abs(p.Y) < 2 && p.X > 10 && p.X < 50);
        var facingA = a.Where(p => Math.Abs(p.X - 60) < 1).Select(p => p.Y).OrderBy(y => y).ToList();
        var facingB = b.Where(p => Math.Abs(p.X - 72) < 1).Select(p => p.Y).OrderBy(y => y).ToList();
        Assert.True(facingA.Count >= 4, $"A facing {facingA.Count}");
        Assert.Equal(facingA.Count, facingB.Count);
        for (var i = 0; i < facingA.Count; i++)
            Assert.Equal(facingA[i], facingB[i], 1);
        Assert.Contains(facingA, y => Math.Abs(y - 550) < 5);
        Assert.True(a.Count(p => p.PairId is not null) >= 4);
    }

    [Fact]
    public void Auto_wide_strip_middle_is_one_left_right_pair()
    {
        var ops = new[] { Contour("A", Rect(0, 0, 150, 1900)) };
        var outlines = new Dictionary<string, IReadOnlyList<Point2>> { ["A"] = Ring(0, 0, 150, 1900) };
        var result = ProfileBridgePlanner.AutoPlace([], ops, outlines, 0, ToolD, 5);
        var left = result.Bridges
            .Where(b => b.PanelId == "A" && Math.Abs(b.X) < 2)
            .Select(b => b.Y)
            .OrderBy(y => y)
            .ToList();
        Assert.Contains(left, y => Math.Abs(y - 50) < 5);
        Assert.Contains(left, y => Math.Abs(y - 950) < 5);
        Assert.Contains(left, y => Math.Abs(y - 1850) < 5);
        Assert.Equal(3, left.Count);
        Assert.DoesNotContain(left, y => Math.Abs(y - 550) < 5);
    }

    [Fact]
    public void Auto_midwidth_strip_middle_is_two_even_pairs()
    {
        var ops = new[] { Contour("A", Rect(0, 0, 110, 1800)) };
        var outlines = new Dictionary<string, IReadOnlyList<Point2>> { ["A"] = Ring(0, 0, 110, 1800) };
        var result = ProfileBridgePlanner.AutoPlace([], ops, outlines, 0, ToolD, 5);
        var left = result.Bridges
            .Where(b => b.PanelId == "A" && Math.Abs(b.X) < 2)
            .Select(b => b.Y)
            .OrderBy(y => y)
            .ToList();
        Assert.Contains(left, y => Math.Abs(y - 50) < 5);
        Assert.Contains(left, y => Math.Abs(y - 600) < 5);
        Assert.Contains(left, y => Math.Abs(y - 1200) < 5);
        Assert.Contains(left, y => Math.Abs(y - 1750) < 5);
        Assert.Equal(4, left.Count);
        Assert.DoesNotContain(left, y => Math.Abs(y - 900) < 5);
    }

    [Fact]
    public void Auto_small_board_uses_different_edges_not_a_row_on_one_seam()
    {
        var ops = new[]
        {
            Contour("A", Rect(0, 0, 200, 200)),
            Contour("B", Rect(212, 0, 300, 200)),
        };
        var outlines = new Dictionary<string, IReadOnlyList<Point2>>
        {
            ["A"] = Ring(0, 0, 200, 200),
            ["B"] = Ring(212, 0, 300, 200),
        };
        var result = ProfileBridgePlanner.AutoPlace([], ops, outlines, 0, ToolD, 5);
        var a = result.Bridges.Where(b => b.PanelId == "A").ToList();
        Assert.Equal(3, a.Count);
        Assert.Equal(3, SideCount(a, 0, 0, 200, 200));
        var aFace = a.Where(b => Math.Abs(b.X - 200) < 2).ToList();
        Assert.Single(aFace);
        Assert.NotNull(aFace[0].PairId);
    }

    [Fact]
    public void Auto_isolated_small_board_still_gets_tabs()
    {
        var ops = new[] { Contour("A", Rect(0, 0, 200, 180)) };
        var outlines = new Dictionary<string, IReadOnlyList<Point2>> { ["A"] = Ring(0, 0, 200, 180) };
        var result = ProfileBridgePlanner.AutoPlace([], ops, outlines, 0, ToolD, 5);
        Assert.Equal(3, result.Bridges.Count);
        Assert.Equal(3, SideCount(result.Bridges, 0, 0, 200, 180));
        Assert.All(result.Bridges, b => Assert.Null(b.PairId));
    }

    [Fact]
    public void Auto_mid_small_board_gets_two_edges()
    {
        var ops = new[] { Contour("A", Rect(0, 0, 320, 320)) };
        var outlines = new Dictionary<string, IReadOnlyList<Point2>> { ["A"] = Ring(0, 0, 320, 320) };
        var result = ProfileBridgePlanner.AutoPlace([], ops, outlines, 0, ToolD, 5);
        Assert.Equal(2, result.Bridges.Count);
        Assert.Equal(2, SideCount(result.Bridges, 0, 0, 320, 320));
    }

    [Fact]
    public void Auto_respects_custom_area_limits()
    {
        var ops = new[] { Contour("A", Rect(0, 0, 500, 500)) };
        var outlines = new Dictionary<string, IReadOnlyList<Point2>> { ["A"] = Ring(0, 0, 500, 500) };
        var three = ProfileBridgePlanner.AutoPlace([], ops, outlines, 0, ToolD, 5, tinyAreaM2: 0.3, largeAreaM2: 0.4);
        Assert.Equal(3, three.Bridges.Count);
        var none = ProfileBridgePlanner.AutoPlace([], ops, outlines, 0, ToolD, 5, tinyAreaM2: 0.1, largeAreaM2: 0.2);
        Assert.Empty(none.Bridges);
    }

    [Fact]
    public void Auto_large_board_is_not_seeded()
    {
        var ops = new[] { Contour("L", Rect(0, 0, 800, 500)) };
        var outlines = new Dictionary<string, IReadOnlyList<Point2>> { ["L"] = Ring(0, 0, 800, 500) };
        var result = ProfileBridgePlanner.AutoPlace([], ops, outlines, 0, ToolD, 5);
        Assert.Empty(result.Bridges);
    }

    [Fact]
    public void Auto_small_against_large_stays_unidirectional()
    {
        var ops = new[]
        {
            Contour("A", Rect(0, 0, 200, 200)),
            Contour("L", Rect(212, 0, 800, 500)),
        };
        var outlines = new Dictionary<string, IReadOnlyList<Point2>>
        {
            ["A"] = Ring(0, 0, 200, 200),
            ["L"] = Ring(212, 0, 800, 500),
        };
        var result = ProfileBridgePlanner.AutoPlace([], ops, outlines, 0, ToolD, 5);
        Assert.DoesNotContain(result.Bridges, b => b.PanelId == "L");
        var a = result.Bridges.Where(b => b.PanelId == "A").ToList();
        Assert.Equal(3, a.Count);
        var face = a.Where(b => Math.Abs(b.X - 200) < 2).ToList();
        Assert.Single(face);
        Assert.Null(face[0].PairId);
    }

    [Fact]
    public void Auto_step2_merges_same_edge_within_300mm()
    {
        var ops = new[]
        {
            Contour("A", Rect(0, 0, 60, 1000)),
            Contour("B", Rect(72, 0, 200, 2500)),
        };
        var outlines = new Dictionary<string, IReadOnlyList<Point2>>
        {
            ["A"] = Ring(0, 0, 60, 1000),
            ["B"] = Ring(72, 0, 200, 2500),
        };
        var result = ProfileBridgePlanner.AutoPlace([], ops, outlines, 0, ToolD, 5);
        var leftB = result.Bridges
            .Where(b => b.PanelId == "B" && Math.Abs(b.X - 72) < 2)
            .Select(b => b.Y)
            .OrderBy(y => y)
            .ToList();
        Assert.DoesNotContain(leftB, y => Math.Abs(y - 1050) < 20);
        for (var i = 1; i < leftB.Count; i++)
        {
            var gap = leftB[i] - leftB[i - 1];
            Assert.True(gap >= 300 - 1e-6 || gap < 1, $"B left gap {gap} at {leftB[i - 1]}->{leftB[i]}");
        }
    }

    [Fact]
    public void Clear_sheet_drops_only_that_page()
    {
        var a = new ProfileBridge
        {
            Id = "a", PanelId = "A", SheetIndex = 0, ArcLengthMm = 0, X = 1, Y = 1, WidthMm = 5,
        };
        var b = new ProfileBridge
        {
            Id = "b", PanelId = "B", SheetIndex = 1, ArcLengthMm = 0, X = 2, Y = 2, WidthMm = 5,
        };
        var result = ProfileBridgePlanner.ClearSheet([a, b], 0);
        Assert.True(result.Changed);
        Assert.Single(result.Bridges);
        Assert.Equal("b", result.Bridges[0].Id);
    }

    [Fact]
    public void Auto_all_places_every_sheet()
    {
        var ops = new[]
        {
            Contour("A", Rect(0, 0, 200, 180), sheet: 0),
            Contour("B", Rect(0, 0, 200, 180), sheet: 1),
        };
        var outlines = new Dictionary<string, IReadOnlyList<Point2>>
        {
            ["A"] = Ring(0, 0, 200, 180),
            ["B"] = Ring(0, 0, 200, 180),
        };
        var result = ProfileBridgePlanner.AutoPlaceAll([], ops, outlines, ToolD, 5);
        Assert.True(result.Changed);
        Assert.Equal(3, result.Bridges.Count(b => b.PanelId == "A" && b.SheetIndex == 0));
        Assert.Equal(3, result.Bridges.Count(b => b.PanelId == "B" && b.SheetIndex == 1));
    }

    static int SideCount(IEnumerable<ProfileBridge> bridges, double minX, double minY, double maxX, double maxY)
    {
        var sides = new HashSet<string>();
        foreach (var b in bridges)
        {
            var dl = Math.Abs(b.X - minX);
            var dr = Math.Abs(b.X - maxX);
            var db = Math.Abs(b.Y - minY);
            var dt = Math.Abs(b.Y - maxY);
            var m = Math.Min(Math.Min(dl, dr), Math.Min(db, dt));
            if (Math.Abs(m - dl) < 1e-6) sides.Add("L");
            else if (Math.Abs(m - dr) < 1e-6) sides.Add("R");
            else if (Math.Abs(m - db) < 1e-6) sides.Add("B");
            else sides.Add("T");
        }
        return sides.Count;
    }
}
