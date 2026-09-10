namespace CabinetNC.Domain.Parts;

using CabinetNC.Domain.Geometry;

public enum DraftLayer
{
    Profile,
    Feature,
    Guide,
}

public sealed class DraftFigure
{
    public required DraftLayer Layer { get; init; }
    public required IReadOnlyList<Point2> Points { get; init; }
    public bool Closed { get; init; }
    public bool IsCircle { get; init; }
    public double CenterX { get; init; }
    public double CenterY { get; init; }
    public double RadiusMm { get; init; }
    /// <summary>Required on Feature: hole / pocket / groove depth in mm.</summary>
    public double? DepthMm { get; init; }
    /// <summary>Groove width when the feature is an open chain.</summary>
    public double? WidthMm { get; init; }
}

public sealed class DraftPanelRequest
{
    public required string PanelId { get; init; }
    public string? Name { get; init; }
    public string? Material { get; init; }
    public double ThicknessMm { get; init; } = 18;
    public string? ModuleId { get; init; }
    public WorkpieceIdentity? Identity { get; init; }
    public Panel? Seed { get; init; }
    public bool NormalizeOrigin { get; init; } = true;
    public double GrooveWidthMm { get; init; } = 6;
    public double GrooveDepthMm { get; init; } = 8;
    public double PocketDepthMm { get; init; } = 8;
}

public sealed class DraftCompileResult
{
    public bool Ok { get; init; }
    public Panel? Panel { get; init; }
    public string? Error { get; init; }

    public static DraftCompileResult Fail(string error) => new() { Ok = false, Error = error };

    public static DraftCompileResult Success(Panel panel) => new() { Ok = true, Panel = panel };
}

/// <summary>
/// Turn CAD draft figures into a shop panel.
/// Nested Profiles: outermost = outline, rings inside it = through cutouts,
/// rings inside a cutout = islands. Sibling Profiles that do not nest are rejected.
/// Feature circles = holes, closed Feature = pockets, open Feature = grooves.
/// </summary>
public static class PanelDraftCompile
{
    public static DraftCompileResult TryBuild(IReadOnlyList<DraftFigure> figures, DraftPanelRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PanelId))
            return DraftCompileResult.Fail("缺少板件编号");
        var thickness = request.ThicknessMm > 0.2 ? request.ThicknessMm : 18;

        var profiles = figures
            .Where(f => f.Layer == DraftLayer.Profile)
            .Select(NormalizeFigure)
            .Where(f => f.Points.Count >= 2)
            .ToList();
        var features = figures
            .Where(f => f.Layer == DraftLayer.Feature)
            .Select(NormalizeFigure)
            .Where(f => f.Points.Count >= 2 || f.IsCircle)
            .ToList();

        var closedProfiles = profiles
            .Where(CanBeOutline)
            .Select(EnsureClosed)
            .OrderByDescending(AreaAbs)
            .ToList();
        if (closedProfiles.Count == 0)
            return DraftCompileResult.Fail("先画 Profile 外框（矩形 / 至少 3 点的多段线 / 圆）");

        var nest = NestProfiles(closedProfiles);
        if (nest.Error is { } nestErr)
            return DraftCompileResult.Fail(nestErr);

        var outlineFig = nest.Outline!;
        var (originX, originY) = request.NormalizeOrigin
            ? BBoxMin(outlineFig)
            : (0d, 0d);

        DraftFigure ShiftFig(DraftFigure f) => ShiftFigure(f, originX, originY);

        outlineFig = ShiftFig(outlineFig);
        var cutouts = nest.Cutouts
            .Select(c => (Fig: ShiftFig(c.Fig), Islands: c.Islands.Select(ShiftFig).ToList()))
            .ToList();
        features = features.Select(ShiftFig).ToList();

        var outlinePts = UniqueRing(outlineFig.Points);
        if (outlinePts.Count < 3)
            return DraftCompileResult.Fail("外框点数不足");

        var outline = new Outline
        {
            Points = outlinePts,
            Closed = true,
            Segments = SegmentsOf(outlineFig),
        };

        var built = new List<PanelFeature>();
        var n = 1;
        foreach (var (cut, islands) in cutouts)
        {
            var feat = MakeThroughCutout(cut, islands, $"C{n++}", thickness);
            if (feat is not null)
                built.Add(feat);
        }

        foreach (var feat in features)
        {
            if (feat.DepthMm is not { } depth || depth <= 0)
                return DraftCompileResult.Fail("特征必须写入深度（孔 / 口袋 / 槽）");
            var through = depth >= thickness - 0.01;

            if (feat.IsCircle && feat.RadiusMm > 0.4)
            {
                built.Add(new PanelFeature
                {
                    FeatureId = $"H{n++}",
                    Kind = "holeVertical",
                    Through = through,
                    X = feat.CenterX,
                    Y = feat.CenterY,
                    DiameterMm = feat.RadiusMm * 2,
                    DepthMm = depth,
                });
                continue;
            }

            var closed = feat.Closed || ClosedRing(feat.Points);
            if (closed)
            {
                var ring = UniqueRing(feat.Points);
                if (ring.Count < 3) continue;
                built.Add(new PanelFeature
                {
                    FeatureId = $"P{n++}",
                    Kind = through ? "throughCutout" : "pocket",
                    Through = through,
                    DepthMm = depth,
                    Path = ring,
                    Profile = ring,
                    ProfileSegments = SegmentsOf(feat),
                });
                continue;
            }

            if (feat.Points.Count >= 2)
            {
                built.Add(new PanelFeature
                {
                    FeatureId = $"G{n++}",
                    Kind = "grooveVertical",
                    X = feat.Points[0].X,
                    Y = feat.Points[0].Y,
                    WidthMm = feat.WidthMm is { } w && w > 0
                        ? w
                        : (request.GrooveWidthMm > 0 ? request.GrooveWidthMm : 6),
                    DepthMm = depth,
                    Path = feat.Points.ToList(),
                });
            }
        }

        var seed = request.Seed;
        var identity = request.Identity ?? seed?.Identity;
        if (identity is null || string.IsNullOrWhiteSpace(identity.WorkpieceId))
        {
            identity = new WorkpieceIdentity
            {
                PackageId = identity?.PackageId ?? seed?.Identity?.PackageId,
                PackageLabel = identity?.PackageLabel ?? seed?.Identity?.PackageLabel,
                ProjectId = identity?.ProjectId ?? seed?.Identity?.ProjectId,
                ModuleId = request.ModuleId ?? identity?.ModuleId ?? seed?.Identity?.ModuleId ?? "Draft",
                WorkpieceId = request.PanelId,
                Role = identity?.Role ?? seed?.Identity?.Role,
                SourcePath = identity?.SourcePath ?? seed?.Identity?.SourcePath,
                SourceFormat = identity?.SourceFormat ?? seed?.Identity?.SourceFormat ?? "draft",
            };
        }
        else if (identity.WorkpieceId != request.PanelId)
        {
            identity = new WorkpieceIdentity
            {
                PackageId = identity.PackageId,
                PackageLabel = identity.PackageLabel,
                ProjectId = identity.ProjectId,
                ModuleId = request.ModuleId ?? identity.ModuleId,
                WorkpieceId = request.PanelId,
                Role = identity.Role,
                SourcePath = identity.SourcePath,
                SourceFormat = identity.SourceFormat,
            };
        }

        var panel = new Panel
        {
            PanelId = request.PanelId,
            Name = string.IsNullOrWhiteSpace(request.Name) ? request.PanelId : request.Name.Trim(),
            Material = string.IsNullOrWhiteSpace(request.Material)
                ? seed?.Material
                : request.Material.Trim(),
            ThicknessMm = thickness,
            DecorId = seed?.DecorId,
            SubstrateId = seed?.SubstrateId,
            ColorName = seed?.ColorName,
            SurfaceMode = seed?.SurfaceMode,
            Quantity = seed?.Quantity > 0 ? seed.Quantity : 1,
            AllowedRotations = seed?.AllowedRotations,
            GrainDirection = seed?.GrainDirection,
            Outline = outline,
            Features = built,
            Identity = identity,
            Orientation = seed?.Orientation ?? new WorkpieceOrientation
            {
                PrimaryFace = "A",
                MillingFace = "A",
                AllowMirror = false,
            },
            EdgeBanding = seed?.EdgeBanding,
            Notes = seed?.Notes,
            Side = seed?.Side ?? "A",
            Faces = seed?.Faces ?? [],
        };
        return DraftCompileResult.Success(panel);
    }

    public static IReadOnlyList<DraftFigure> Explode(Panel panel)
    {
        var list = new List<DraftFigure>();
        var outline = panel.Outline;
        if (CircleFromSegments(outline.Segments, out var ocx, out var ocy, out var orad)
            || TryFitCircle(outline.Points, out ocx, out ocy, out orad))
        {
            list.Add(CircleFigure(DraftLayer.Profile, ocx, ocy, orad));
        }
        else if (outline.Points.Count >= 3)
        {
            list.Add(new DraftFigure
            {
                Layer = DraftLayer.Profile,
                Points = CloseRing(outline.Points),
                Closed = true,
            });
        }

        foreach (var f in panel.Features)
        {
            if (PanelEdit.IsHole(f) && (f.DiameterMm ?? 0) > 0.4)
            {
                list.Add(CircleFigure(DraftLayer.Feature, f.X, f.Y, f.DiameterMm!.Value * 0.5, depthMm: f.DepthMm));
                continue;
            }

            if (PanelEdit.IsCutout(f))
            {
                if ((f.DiameterMm ?? 0) > 0.4)
                    list.Add(CircleFigure(DraftLayer.Profile, f.X, f.Y, f.DiameterMm!.Value * 0.5));
                else if (f.Path is { Count: >= 3 } path)
                    list.Add(new DraftFigure
                    {
                        Layer = DraftLayer.Profile,
                        Points = CloseRing(path),
                        Closed = true,
                    });
                foreach (var island in f.Holes ?? [])
                {
                    if (island.Count < 3) continue;
                    list.Add(new DraftFigure
                    {
                        Layer = DraftLayer.Profile,
                        Points = CloseRing(island),
                        Closed = true,
                    });
                }
                continue;
            }

            if (PanelEdit.IsPocket(f) && (f.Path ?? f.Profile) is { Count: >= 3 } pocket)
            {
                list.Add(new DraftFigure
                {
                    Layer = DraftLayer.Feature,
                    Points = CloseRing(pocket),
                    Closed = true,
                    DepthMm = f.DepthMm,
                });
                continue;
            }

            if (PanelEdit.IsGroove(f) && f.Path is { Count: >= 2 } groove)
            {
                list.Add(new DraftFigure
                {
                    Layer = DraftLayer.Feature,
                    Points = groove.ToList(),
                    Closed = false,
                    DepthMm = f.DepthMm,
                    WidthMm = f.WidthMm,
                });
            }
        }

        return list;
    }

    public static DraftFigure CircleFigure(
        DraftLayer layer,
        double cx,
        double cy,
        double radius,
        int segs = 64,
        double? depthMm = null,
        double? widthMm = null)
    {
        var pts = CirclePoints(cx, cy, radius, segs);
        return new DraftFigure
        {
            Layer = layer,
            Points = pts,
            Closed = true,
            IsCircle = true,
            CenterX = cx,
            CenterY = cy,
            RadiusMm = radius,
            DepthMm = depthMm,
            WidthMm = widthMm,
        };
    }

    public static IReadOnlyList<Point2> CirclePoints(double cx, double cy, double radius, int segs = 64)
    {
        var n = Math.Max(12, segs);
        var pts = new List<Point2>(n + 1);
        for (var i = 0; i < n; i++)
        {
            var a = Math.PI * 2 * i / n;
            pts.Add(new Point2(cx + Math.Cos(a) * radius, cy + Math.Sin(a) * radius));
        }
        pts.Add(pts[0]);
        return pts;
    }

    static DraftFigure NormalizeFigure(DraftFigure f)
    {
        if (f.IsCircle && f.RadiusMm > 0.25)
        {
            var pts = f.Points.Count >= 8 ? f.Points : CirclePoints(f.CenterX, f.CenterY, f.RadiusMm);
            return new DraftFigure
            {
                Layer = f.Layer,
                Points = pts,
                Closed = true,
                IsCircle = true,
                CenterX = f.CenterX,
                CenterY = f.CenterY,
                RadiusMm = f.RadiusMm,
                DepthMm = f.DepthMm,
                WidthMm = f.WidthMm,
            };
        }

        if (TryFitCircle(f.Points, out var cx, out var cy, out var r))
        {
            return new DraftFigure
            {
                Layer = f.Layer,
                Points = f.Points,
                Closed = true,
                IsCircle = true,
                CenterX = cx,
                CenterY = cy,
                RadiusMm = r,
                DepthMm = f.DepthMm,
                WidthMm = f.WidthMm,
            };
        }

        return new DraftFigure
        {
            Layer = f.Layer,
            Points = f.Points,
            Closed = f.Closed || ClosedRing(f.Points),
            IsCircle = false,
            DepthMm = f.DepthMm,
            WidthMm = f.WidthMm,
        };
    }

    sealed class ProfileNest
    {
        public DraftFigure? Outline { get; init; }
        public List<(DraftFigure Fig, List<DraftFigure> Islands)> Cutouts { get; init; } = [];
        public string? Error { get; init; }
    }

    static ProfileNest NestProfiles(IReadOnlyList<DraftFigure> closed)
    {
        var n = closed.Count;
        var parent = new int[n];
        Array.Fill(parent, -1);
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                if (!RingContains(closed[i], closed[j])) continue;
                var cur = parent[j];
                if (cur < 0 || AreaAbs(closed[i]) < AreaAbs(closed[cur]))
                    parent[j] = i;
            }
        }

        var roots = Enumerable.Range(0, n).Where(i => parent[i] < 0).ToList();
        if (roots.Count == 0)
            return new ProfileNest { Error = "先画 Profile 外框（矩形 / 至少 3 点的多段线 / 圆）" };
        if (roots.Count > 1)
            return new ProfileNest { Error = "有多个不相套的 Profile 外框，请分开创建板件" };

        var outline = roots[0];
        var cutouts = new List<(DraftFigure Fig, List<DraftFigure> Islands)>();
        for (var i = 0; i < n; i++)
        {
            if (parent[i] != outline) continue;
            var islands = new List<DraftFigure>();
            for (var k = 0; k < n; k++)
            {
                if (parent[k] != i) continue;
                if (Enumerable.Range(0, n).Any(c => parent[c] == k))
                    return new ProfileNest { Error = "Profile 套叠只支持外框 / 通切 / 岛屿三层" };
                islands.Add(closed[k]);
            }
            cutouts.Add((closed[i], islands));
        }

        return new ProfileNest { Outline = closed[outline], Cutouts = cutouts };
    }

    static PanelFeature? MakeThroughCutout(
        DraftFigure cut,
        IReadOnlyList<DraftFigure> islands,
        string id,
        double thickness)
    {
        var holes = islands
            .Select(i => UniqueRing(i.Points))
            .Where(r => r.Count >= 3)
            .Select(r => (IReadOnlyList<Point2>)r)
            .ToList();
        var holeSegs = islands
            .Select(SegmentsOf)
            .OfType<IReadOnlyList<CadSegment>>()
            .ToList();

        if (cut.IsCircle && cut.RadiusMm > 0.4)
        {
            return new PanelFeature
            {
                FeatureId = id,
                Kind = "throughCutout",
                Through = true,
                X = cut.CenterX,
                Y = cut.CenterY,
                DiameterMm = cut.RadiusMm * 2,
                DepthMm = thickness,
                Path = UniqueRing(cut.Points),
                ProfileSegments = [CadSegment.MakeCircle(new Point2(cut.CenterX, cut.CenterY), cut.RadiusMm)],
                Holes = holes.Count > 0 ? holes : null,
                HoleSegments = holeSegs.Count > 0 ? holeSegs : null,
            };
        }

        var ring = UniqueRing(cut.Points);
        if (ring.Count < 3) return null;
        return new PanelFeature
        {
            FeatureId = id,
            Kind = "throughCutout",
            Through = true,
            DepthMm = thickness,
            Path = ring,
            ProfileSegments = SegmentsOf(cut),
            Holes = holes.Count > 0 ? holes : null,
            HoleSegments = holeSegs.Count > 0 ? holeSegs : null,
        };
    }

    public static bool RingContains(DraftFigure outer, DraftFigure inner)
    {
        if (AreaAbs(outer) <= AreaAbs(inner) + 1) return false;
        var outerRing = UniqueRing(outer.Points);
        var innerPts = UniqueRing(inner.Points);
        if (outerRing.Count < 3 || innerPts.Count < 3) return false;
        foreach (var p in innerPts)
        {
            if (!PointInRing(p, outerRing)) return false;
        }
        return true;
    }

    static bool PointInRing(Point2 p, IReadOnlyList<Point2> ring)
    {
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            if (OnSegment(a, b, p)) return true;
        }

        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var yi = ring[i].Y;
            var yj = ring[j].Y;
            var xi = ring[i].X;
            var xj = ring[j].X;
            var hit = yi > p.Y != yj > p.Y
                && p.X < (xj - xi) * (p.Y - yi) / (yj - yi + 1e-12) + xi;
            if (hit) inside = !inside;
        }
        return inside;
    }

    static bool OnSegment(Point2 a, Point2 b, Point2 p)
    {
        var cross = (p.X - a.X) * (b.Y - a.Y) - (p.Y - a.Y) * (b.X - a.X);
        if (Math.Abs(cross) > 1e-6) return false;
        var dot = (p.X - a.X) * (b.X - a.X) + (p.Y - a.Y) * (b.Y - a.Y);
        if (dot < -1e-6) return false;
        var len2 = (b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y);
        return dot <= len2 + 1e-6;
    }

    public static bool CanBeOutline(DraftFigure f)
    {
        if (f.IsCircle && f.RadiusMm > 0.4) return true;
        var ring = UniqueRing(f.Points);
        if (ring.Count < 3) return false;
        return Math.Abs(SignedArea(CloseRing(ring))) > 1;
    }

    static DraftFigure EnsureClosed(DraftFigure f)
    {
        if (f.IsCircle) return f;
        return new DraftFigure
        {
            Layer = f.Layer,
            Points = CloseRing(f.Points),
            Closed = true,
            IsCircle = f.IsCircle,
            CenterX = f.CenterX,
            CenterY = f.CenterY,
            RadiusMm = f.RadiusMm,
            DepthMm = f.DepthMm,
            WidthMm = f.WidthMm,
        };
    }

    static DraftFigure ShiftFigure(DraftFigure f, double ox, double oy)
    {
        if (Math.Abs(ox) < 1e-9 && Math.Abs(oy) < 1e-9) return f;
        return new DraftFigure
        {
            Layer = f.Layer,
            Points = f.Points.Select(p => new Point2(p.X - ox, p.Y - oy)).ToList(),
            Closed = f.Closed,
            IsCircle = f.IsCircle,
            CenterX = f.CenterX - ox,
            CenterY = f.CenterY - oy,
            RadiusMm = f.RadiusMm,
            DepthMm = f.DepthMm,
            WidthMm = f.WidthMm,
        };
    }

    static IReadOnlyList<CadSegment>? SegmentsOf(DraftFigure f)
    {
        if (f.IsCircle && f.RadiusMm > 0.25)
            return [CadSegment.MakeCircle(new Point2(f.CenterX, f.CenterY), f.RadiusMm)];
        var ring = UniqueRing(f.Points);
        if (ring.Count < 3) return null;
        var segs = new List<CadSegment>(ring.Count);
        for (var i = 0; i < ring.Count; i++)
            segs.Add(CadSegment.MakeLine(ring[i], ring[(i + 1) % ring.Count]));
        return segs;
    }

    static bool ClosedRing(IReadOnlyList<Point2> pts)
    {
        if (pts.Count < 3) return false;
        return Near(pts[0], pts[^1]);
    }

    static List<Point2> CloseRing(IReadOnlyList<Point2> pts)
    {
        var list = pts.ToList();
        if (list.Count >= 3 && !Near(list[0], list[^1]))
            list.Add(list[0]);
        return list;
    }

    static List<Point2> UniqueRing(IReadOnlyList<Point2> pts)
    {
        var list = new List<Point2>(pts.Count);
        foreach (var p in pts)
        {
            if (list.Count > 0 && Near(list[^1], p)) continue;
            list.Add(p);
        }
        if (list.Count >= 2 && Near(list[0], list[^1]))
            list.RemoveAt(list.Count - 1);
        return list;
    }

    static (double X, double Y) BBoxMin(DraftFigure f)
    {
        if (f.IsCircle)
            return (f.CenterX - f.RadiusMm, f.CenterY - f.RadiusMm);
        return (f.Points.Min(p => p.X), f.Points.Min(p => p.Y));
    }

    static double AreaAbs(DraftFigure f)
    {
        if (f.IsCircle) return Math.PI * f.RadiusMm * f.RadiusMm;
        return Math.Abs(SignedArea(f.Points));
    }

    static double SignedArea(IReadOnlyList<Point2> pts)
    {
        double sum = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum * 0.5;
    }

    static bool CircleFromSegments(
        IReadOnlyList<CadSegment>? segs,
        out double cx, out double cy, out double r)
    {
        cx = cy = r = 0;
        if (segs is not { Count: 1 }) return false;
        var g = segs[0];
        if (!g.IsCircle || g.Center is null || g.RadiusMm < 0.25) return false;
        cx = g.Center.Value.X;
        cy = g.Center.Value.Y;
        r = g.RadiusMm;
        return true;
    }

    static bool TryFitCircle(IReadOnlyList<Point2> pts, out double cx, out double cy, out double r)
    {
        cx = cy = r = 0;
        var ring = UniqueRing(pts);
        if (ring.Count < 16) return false;
        var midX = ring.Average(p => p.X);
        var midY = ring.Average(p => p.Y);
        var radii = ring.Select(p => Dist(p, new Point2(midX, midY))).ToList();
        var meanR = radii.Average();
        if (meanR < 0.5) return false;
        var maxDev = radii.Max(v => Math.Abs(v - meanR));
        if (maxDev > Math.Max(0.35, meanR * 0.015)) return false;
        cx = midX;
        cy = midY;
        r = meanR;
        return true;
    }

    static bool Near(Point2 a, Point2 b) =>
        Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;

    static double Dist(Point2 a, Point2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
