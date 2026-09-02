namespace CabinetNC.Domain.Parts;

using CabinetNC.Domain.Geometry;

/// <summary>Port of src/geom/panel.js edit ops — returns new Panel instances.</summary>
public static class PanelEdit
{
    public static (double MinX, double MinY, double MaxX, double MaxY, double W, double H) BBox(Panel panel)
    {
        var pts = panel.Outline.Points;
        if (pts.Count == 0) return (0, 0, 0, 0, 0, 0);
        var minX = pts.Min(p => p.X);
        var minY = pts.Min(p => p.Y);
        var maxX = pts.Max(p => p.X);
        var maxY = pts.Max(p => p.Y);
        return (minX, minY, maxX, maxY, maxX - minX, maxY - minY);
    }

    public static bool IsAxisAlignedRect(Panel panel)
    {
        var pts = panel.Outline.Points;
        if (pts.Count < 4) return false;
        var (minX, minY, maxX, maxY, w, h) = BBox(panel);
        if (w < 1e-6 || h < 1e-6) return false;
        var uniq = new HashSet<(long, long)>();
        foreach (var p in pts)
        {
            var qx = (long)Math.Round(p.X * 1000);
            var qy = (long)Math.Round(p.Y * 1000);
            uniq.Add((qx, qy));
            var onEdge =
                (Math.Abs(p.X - minX) < 1e-6 || Math.Abs(p.X - maxX) < 1e-6) &&
                (p.Y >= minY - 1e-6 && p.Y <= maxY + 1e-6)
                ||
                (Math.Abs(p.Y - minY) < 1e-6 || Math.Abs(p.Y - maxY) < 1e-6) &&
                (p.X >= minX - 1e-6 && p.X <= maxX + 1e-6);
            if (!onEdge) return false;
        }
        return uniq.Count == 4;
    }

    public static Panel MoveHole(Panel panel, string featureId, double x, double y)
    {
        var feats = panel.Features.Select(f =>
        {
            if (!IsHole(f) || f.FeatureId != featureId) return f;
            return CloneFeature(f, x: x, y: y);
        }).ToList();
        return ClonePanel(panel, feats);
    }

    public static Panel MoveGroovePoint(Panel panel, string featureId, int pointIndex, double x, double y)
    {
        var feats = panel.Features.Select(f =>
        {
            if (!IsGroove(f) || f.FeatureId != featureId || f.Path is null) return f;
            if (pointIndex < 0 || pointIndex >= f.Path.Count) return f;
            var path = f.Path.ToList();
            path[pointIndex] = new Point2(x, y);
            return CloneFeature(f, path: path);
        }).ToList();
        return ClonePanel(panel, feats);
    }

    public static Panel TranslateFeatures(Panel panel, double dx, double dy)
    {
        Point2 Map(Point2 p) => new(p.X + dx, p.Y + dy);
        var feats = panel.Features.Select(f => MapFeature(f, Map)).ToList();
        return ClonePanel(panel, feats);
    }

    public static Panel RotatePanel(Panel panel, double deg)
    {
        var (minX, minY, maxX, maxY, _, _) = BBox(panel);
        var cx = (minX + maxX) / 2;
        var cy = (minY + maxY) / 2;
        var rad = deg * Math.PI / 180.0;
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);
        Point2 Map(Point2 p)
        {
            var dx = p.X - cx;
            var dy = p.Y - cy;
            return new Point2(cx + dx * c - dy * s, cy + dx * s + dy * c);
        }

        var outline = new Outline
        {
            Points = panel.Outline.Points.Select(Map).ToList(),
            Closed = panel.Outline.Closed,
            Frame = panel.Outline.Frame,
            Segments = CadPath.Map(panel.Outline.Segments, Map),
        };
        var feats = panel.Features.Select(f => MapFeature(f, Map)).ToList();
        return ClonePanel(panel, feats, outline);
    }

    public static Panel ResizeFromEdges(Panel panel, double minX, double minY, double maxX, double maxY)
    {
        var box = BBox(panel);
        var w0 = Math.Max(box.W, 1e-6);
        var h0 = Math.Max(box.H, 1e-6);
        var w1 = Math.Max(maxX - minX, 10);
        var h1 = Math.Max(maxY - minY, 10);
        Point2 Map(Point2 p)
        {
            var u = (p.X - box.MinX) / w0;
            var v = (p.Y - box.MinY) / h0;
            return new Point2(minX + u * w1, minY + v * h1);
        }

        var outline = new Outline
        {
            Points =
            [
                new(minX, minY),
                new(maxX, minY),
                new(maxX, maxY),
                new(minX, maxY),
            ],
            Closed = true,
            Frame = panel.Outline.Frame,
            Segments =
            [
                CadSegment.MakeLine(new(minX, minY), new(maxX, minY)),
                CadSegment.MakeLine(new(maxX, minY), new(maxX, maxY)),
                CadSegment.MakeLine(new(maxX, maxY), new(minX, maxY)),
                CadSegment.MakeLine(new(minX, maxY), new(minX, minY)),
            ],
        };
        var feats = panel.Features.Select(f => MapFeature(f, Map)).ToList();
        return ClonePanel(panel, feats, outline);
    }

    public static Panel AddVerticalHole(Panel panel, double x, double y, double diameterMm = 8, double? depthMm = null)
    {
        var id = NextId(panel, "H");
        var feats = panel.Features.ToList();
        feats.Add(new PanelFeature
        {
            FeatureId = id,
            Kind = "holeVertical",
            FaceId = panel.Side ?? panel.Orientation?.MillingFace,
            Through = (depthMm ?? panel.ThicknessMm) >= panel.ThicknessMm - 0.01,
            X = x,
            Y = y,
            DiameterMm = diameterMm,
            DepthMm = depthMm ?? panel.ThicknessMm,
        });
        return ClonePanel(panel, feats);
    }

    public static Panel AddVerticalGroove(Panel panel, IReadOnlyList<Point2> path, double widthMm = 6, double depthMm = 8)
    {
        if (path.Count < 2) return panel;
        var id = NextId(panel, "G");
        var feats = panel.Features.ToList();
        feats.Add(new PanelFeature
        {
            FeatureId = id,
            Kind = "grooveVertical",
            FaceId = panel.Side ?? panel.Orientation?.MillingFace,
            X = path[0].X,
            Y = path[0].Y,
            WidthMm = widthMm,
            DepthMm = depthMm,
            Path = path.ToList(),
        });
        return ClonePanel(panel, feats);
    }

    public static Panel UpdateFeatureParams(
        Panel panel,
        string featureId,
        double? x = null,
        double? y = null,
        double? diameterMm = null,
        double? depthMm = null,
        double? widthMm = null)
    {
        var feats = panel.Features.Select(f =>
        {
            if (f.FeatureId != featureId) return f;
            return new PanelFeature
            {
                FeatureId = f.FeatureId,
                Kind = f.Kind,
                FaceId = f.FaceId,
                Through = f.Through,
                GroupId = f.GroupId,
                Purpose = f.Purpose,
                SourceRelationshipId = f.SourceRelationshipId,
                X = x ?? f.X,
                Y = y ?? f.Y,
                DiameterMm = diameterMm ?? f.DiameterMm,
                DepthMm = depthMm ?? f.DepthMm,
                WidthMm = widthMm ?? f.WidthMm,
                Path = f.Path,
                Profile = f.Profile,
                Holes = f.Holes,
                ProfileSegments = f.ProfileSegments,
                HoleSegments = f.HoleSegments,
            };
        }).ToList();
        return ClonePanel(panel, feats);
    }

    public static Panel RemoveFeature(Panel panel, string featureId)
    {
        var feats = panel.Features.Where(f => f.FeatureId != featureId).ToList();
        return ClonePanel(panel, feats);
    }

    /// <summary>Mirror about panel bbox center. axis "X" flips X; "Y" flips Y.</summary>
    public static Panel Mirror(Panel panel, string axis)
    {
        var ax = axis.Trim().ToUpperInvariant();
        var (minX, minY, maxX, maxY, _, _) = BBox(panel);
        var cx = (minX + maxX) / 2;
        var cy = (minY + maxY) / 2;
        Point2 Map(Point2 p) => ax switch
        {
            "X" => new Point2(2 * cx - p.X, p.Y),
            "Y" => new Point2(p.X, 2 * cy - p.Y),
            _ => p,
        };

        var outline = new Outline
        {
            Points = panel.Outline.Points.Select(Map).ToList(),
            Closed = panel.Outline.Closed,
            Frame = panel.Outline.Frame,
            Segments = CadPath.Map(panel.Outline.Segments, Map, flipCw: true),
        };
        var feats = panel.Features.Select(f => MapFeature(f, Map, flipCw: true)).ToList();

        var banding = panel.EdgeBanding;
        if (banding is not null)
        {
            banding = ax switch
            {
                "X" => new EdgeBanding
                {
                    Front = banding.Front,
                    Back = banding.Back,
                    Left = banding.Right,
                    Right = banding.Left,
                },
                "Y" => new EdgeBanding
                {
                    Front = banding.Back,
                    Back = banding.Front,
                    Left = banding.Left,
                    Right = banding.Right,
                },
                _ => banding,
            };
        }

        var side = FlipFace(panel.Side);
        var orient = panel.Orientation;
        if (orient is not null)
        {
            orient = new WorkpieceOrientation
            {
                PrimaryFace = FlipFace(orient.PrimaryFace) ?? orient.PrimaryFace,
                MillingFace = FlipFace(orient.MillingFace) ?? orient.MillingFace,
                GrainDirection = orient.GrainDirection,
                AllowedRotations = orient.AllowedRotations,
                AllowMirror = orient.AllowMirror,
                FlipStrategy = ax is "X" or "Y" ? ax.ToLowerInvariant() : orient.FlipStrategy,
            };
        }

        return new Panel
        {
            PanelId = panel.PanelId,
            Name = panel.Name,
            Material = panel.Material,
            ThicknessMm = panel.ThicknessMm,
            DecorId = panel.DecorId,
            SubstrateId = panel.SubstrateId,
            ColorName = panel.ColorName,
            SurfaceMode = panel.SurfaceMode,
            Quantity = panel.Quantity,
            AllowedRotations = panel.AllowedRotations,
            GrainDirection = panel.GrainDirection,
            Outline = outline,
            Features = feats,
            Identity = panel.Identity,
            Orientation = orient,
            EdgeBanding = banding,
            Notes = panel.Notes,
            Side = side ?? panel.Side,
            Faces = panel.Faces,
        };
    }

    /// <summary>Deep-ish copy with a new PanelId; feature IDs get a unique suffix.</summary>
    public static Panel Duplicate(Panel panel, string newPanelId)
    {
        var feats = panel.Features.Select(f => new PanelFeature
        {
            FeatureId = $"{f.FeatureId}_c",
            Kind = f.Kind,
            FaceId = f.FaceId,
            Through = f.Through,
            GroupId = f.GroupId,
            Purpose = f.Purpose,
            SourceRelationshipId = f.SourceRelationshipId,
            X = f.X,
            Y = f.Y,
            DiameterMm = f.DiameterMm,
            DepthMm = f.DepthMm,
            WidthMm = f.WidthMm,
            Path = f.Path?.ToList(),
            Profile = f.Profile?.ToList(),
            Holes = f.Holes?.Select(ring => (IReadOnlyList<Point2>)ring.ToList()).ToList(),
            ProfileSegments = f.ProfileSegments?.ToList(),
            HoleSegments = f.HoleSegments?
                .Select(ring => (IReadOnlyList<CadSegment>)ring.ToList()).ToList(),
        }).ToList();
        // ensure unique feature ids within panel
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < feats.Count; i++)
        {
            var id = feats[i].FeatureId;
            var n = 1;
            while (!used.Add(id))
                id = $"{feats[i].FeatureId}{n++}";
            if (id != feats[i].FeatureId)
                feats[i] = new PanelFeature
                {
                    FeatureId = id,
                    Kind = feats[i].Kind,
                    FaceId = feats[i].FaceId,
                    Through = feats[i].Through,
                    GroupId = feats[i].GroupId,
                    Purpose = feats[i].Purpose,
                    SourceRelationshipId = feats[i].SourceRelationshipId,
                    X = feats[i].X,
                    Y = feats[i].Y,
                    DiameterMm = feats[i].DiameterMm,
                    DepthMm = feats[i].DepthMm,
                    WidthMm = feats[i].WidthMm,
                    Path = feats[i].Path,
                    Profile = feats[i].Profile,
                    Holes = feats[i].Holes,
                    ProfileSegments = feats[i].ProfileSegments,
                    HoleSegments = feats[i].HoleSegments,
                };
        }

        WorkpieceIdentity? identity = panel.Identity is null
            ? null
            : new WorkpieceIdentity
            {
                PackageId = panel.Identity.PackageId,
                PackageLabel = panel.Identity.PackageLabel,
                ProjectId = panel.Identity.ProjectId,
                ModuleId = panel.Identity.ModuleId,
                WorkpieceId = newPanelId,
                Role = panel.Identity.Role,
                SourcePath = panel.Identity.SourcePath,
                SourceFormat = panel.Identity.SourceFormat,
            };

        return new Panel
        {
            PanelId = newPanelId,
            Name = panel.Name is null ? null : $"{panel.Name} (copy)",
            Material = panel.Material,
            ThicknessMm = panel.ThicknessMm,
            DecorId = panel.DecorId,
            SubstrateId = panel.SubstrateId,
            ColorName = panel.ColorName,
            SurfaceMode = panel.SurfaceMode,
            Quantity = panel.Quantity,
            AllowedRotations = panel.AllowedRotations,
            GrainDirection = panel.GrainDirection,
            Outline = new Outline
            {
                Points = panel.Outline.Points.ToList(),
                Closed = panel.Outline.Closed,
                Frame = panel.Outline.Frame,
                Segments = panel.Outline.Segments?.ToList(),
            },
            Features = feats,
            Identity = identity,
            Orientation = panel.Orientation,
            EdgeBanding = panel.EdgeBanding is null
                ? null
                : new EdgeBanding
                {
                    Front = panel.EdgeBanding.Front,
                    Back = panel.EdgeBanding.Back,
                    Left = panel.EdgeBanding.Left,
                    Right = panel.EdgeBanding.Right,
                },
            Notes = panel.Notes,
            Side = panel.Side,
            Faces = panel.Faces,
        };
    }

    /// <summary>ASSUMED Day4: shortest edge &lt; 80 mm or area &lt; 0.02 m².</summary>
    public static bool IsSmallPanel(Panel panel, out string reason)
    {
        var (_, _, _, _, w, h) = BBox(panel);
        var shortEdge = Math.Min(w, h);
        var areaM2 = (w * h) / 1_000_000.0;
        if (shortEdge < 80)
        {
            reason = $"最短边 {shortEdge:0.#} mm < 80 mm";
            return true;
        }
        if (areaM2 < 0.02)
        {
            reason = $"面积 {areaM2:0.####} m² < 0.02 m²";
            return true;
        }
        reason = "";
        return false;
    }

    static string? FlipFace(string? face)
    {
        if (string.IsNullOrWhiteSpace(face)) return face;
        return face.Trim().ToUpperInvariant() switch
        {
            "A" => "B",
            "B" => "A",
            _ => face,
        };
    }

    public static bool IsHole(PanelFeature f) =>
        f.Kind.Contains("hole", StringComparison.OrdinalIgnoreCase);

    public static bool IsGroove(PanelFeature f) =>
        f.Kind.Contains("groove", StringComparison.OrdinalIgnoreCase);

    public static bool IsTongueGroove(PanelFeature f)
    {
        if (!IsGroove(f)) return false;
        var blob = $"{f.Purpose} {f.Kind}";
        return blob.Contains("tongue", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("半槽", StringComparison.OrdinalIgnoreCase);
    }

    public static Panel SetFeaturePurpose(Panel panel, string featureId, string? purpose)
    {
        var feats = panel.Features
            .Select(f => f.FeatureId == featureId
                ? CloneFeature(f, purpose: purpose, replacePurpose: true)
                : f)
            .ToList();
        return ClonePanel(panel, feats);
    }

    public static bool IsCutout(PanelFeature f) =>
        f.Kind.Contains("cutout", StringComparison.OrdinalIgnoreCase)
        || (f.Through && f.Kind.Contains("pocket", StringComparison.OrdinalIgnoreCase)
            && f.Path is { Count: >= 3 });

    /// <summary>Blind closed floor (LED channels, cups, etc.) — not a through cutout.</summary>
    public static bool IsPocket(PanelFeature f) =>
        !f.Through
        && f.Kind.Contains("pocket", StringComparison.OrdinalIgnoreCase)
        && (f.Path is { Count: >= 3 } || f.Profile is { Count: >= 3 });

    public static string FeatureDisplayLabel(PanelFeature f)
    {
        var purpose = (f.Purpose ?? "").Trim();
        if (purpose.Length == 0) return "";
        if (purpose.Contains("led", StringComparison.OrdinalIgnoreCase))
            return "LED";
        return purpose;
    }

    static string NextId(Panel panel, string prefix)
    {
        var n = panel.Features.Count + 1;
        string id;
        do { id = $"{prefix}{n++}"; }
        while (panel.Features.Any(f => f.FeatureId == id));
        return id;
    }

    static Panel ClonePanel(Panel panel, IReadOnlyList<PanelFeature> feats, Outline? outline = null) =>
        new()
        {
            PanelId = panel.PanelId,
            Name = panel.Name,
            Material = panel.Material,
            ThicknessMm = panel.ThicknessMm,
            DecorId = panel.DecorId,
            SubstrateId = panel.SubstrateId,
            ColorName = panel.ColorName,
            SurfaceMode = panel.SurfaceMode,
            Quantity = panel.Quantity,
            AllowedRotations = panel.AllowedRotations,
            GrainDirection = panel.GrainDirection,
            Outline = outline ?? panel.Outline,
            Features = feats,
            Identity = panel.Identity,
            Orientation = panel.Orientation,
            EdgeBanding = panel.EdgeBanding,
            Notes = panel.Notes,
            Side = panel.Side,
            Faces = panel.Faces,
        };

    static PanelFeature MapFeature(PanelFeature f, Func<Point2, Point2> map, bool flipCw = false)
    {
        if (IsHole(f) && f.Path is null && f.Profile is null)
        {
            var q = map(new Point2(f.X, f.Y));
            return CloneFeature(f, x: q.X, y: q.Y);
        }
        var mappedHole = IsHole(f) ? map(new Point2(f.X, f.Y)) : new Point2(f.X, f.Y);
        return CloneFeature(
            f,
            x: mappedHole.X,
            y: mappedHole.Y,
            path: f.Path?.Select(map).ToList(),
            profile: f.Profile?.Select(map).ToList(),
            profileSegments: CadPath.Map(f.ProfileSegments, map, flipCw),
            holeSegments: f.HoleSegments?
                .Select(ring => CadPath.Map(ring, map, flipCw))
                .ToList());
    }

    static PanelFeature CloneFeature(
        PanelFeature f,
        double? x = null,
        double? y = null,
        IReadOnlyList<Point2>? path = null,
        IReadOnlyList<Point2>? profile = null,
        IReadOnlyList<CadSegment>? profileSegments = null,
        IReadOnlyList<IReadOnlyList<CadSegment>>? holeSegments = null,
        string? purpose = null,
        bool replacePurpose = false) =>
        new()
        {
            FeatureId = f.FeatureId,
            Kind = f.Kind,
            FaceId = f.FaceId,
            Through = f.Through,
            GroupId = f.GroupId,
            Purpose = replacePurpose
                ? (string.IsNullOrWhiteSpace(purpose) ? null : purpose)
                : f.Purpose,
            SourceRelationshipId = f.SourceRelationshipId,
            X = x ?? f.X,
            Y = y ?? f.Y,
            DiameterMm = f.DiameterMm,
            DepthMm = f.DepthMm,
            WidthMm = f.WidthMm,
            Path = path ?? f.Path,
            Profile = profile ?? f.Profile,
            Holes = f.Holes,
            ProfileSegments = profileSegments ?? f.ProfileSegments,
            HoleSegments = holeSegments ?? f.HoleSegments,
        };
}
