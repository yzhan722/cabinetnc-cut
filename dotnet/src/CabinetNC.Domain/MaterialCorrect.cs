namespace CabinetNC.Domain;

using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Materials;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

public enum BlindFeatureDepthPolicy
{
    Keep,
    ScaleWithThickness,
}

/// <summary>Merge stock material kinds (Fusion thickness drift) and retarget feature depths.</summary>
public static class MaterialCorrect
{
    const double FullSlotTolMm = 0.25;

    public static bool SameKind(Panel panel, NestGroupKey key) =>
        NestGroupKey.From(panel.Material, panel.ThicknessMm).Equals(key);

    public static bool HasHalfSlotOrHinge(IEnumerable<Panel> panels) =>
        panels.Any(p => p.Features.Any(f => IsHalfSlotOrHinge(f, p.ThicknessMm)));

    public static bool IsThroughOrFullSlot(PanelFeature f, double thicknessMm)
    {
        if (f.Through) return true;
        if (PanelEdit.IsCutout(f)) return true;
        if (f.Kind.Contains("through", StringComparison.OrdinalIgnoreCase)) return true;
        return PanelEdit.IsGroove(f)
            && f.DepthMm is { } d
            && Math.Abs(d - thicknessMm) <= FullSlotTolMm;
    }

    public static bool IsHalfSlotOrHinge(PanelFeature f, double thicknessMm)
    {
        if (IsThroughOrFullSlot(f, thicknessMm)) return false;
        if (ClearanceToolPick.IsHingeFeature(f)) return true;
        if (PanelEdit.IsTongueGroove(f)) return true;
        return PanelEdit.IsGroove(f);
    }

    public static CutPackage MergeKinds(
        CutPackage package,
        IReadOnlyList<NestGroupKey> selected,
        NestGroupKey target,
        BlindFeatureDepthPolicy blindPolicy)
    {
        if (selected.Count < 2 || !selected.Any(k => k.Equals(target)))
            return package;

        var pick = selected.ToHashSet();
        var panels = package.Panels.Select(p =>
        {
            var key = NestGroupKey.From(p.Material, p.ThicknessMm);
            if (!pick.Contains(key) || key.Equals(target))
                return p;
            return RewritePanel(p, target, blindPolicy);
        }).ToList();

        return package.WithPanels(panels).WithSheets(MergeSheets(package.Sheets, pick, target));
    }

    static Panel RewritePanel(Panel panel, NestGroupKey target, BlindFeatureDepthPolicy blindPolicy)
    {
        var tOld = panel.ThicknessMm;
        var tNew = target.ThicknessMm;
        var material = target.Material == "(unspecified)" ? panel.Material : target.Material;
        var feats = panel.Features
            .Select(f => RewriteFeature(f, tOld, tNew, blindPolicy))
            .ToList();
        return new Panel
        {
            PanelId = panel.PanelId,
            Name = panel.Name,
            Material = material,
            ThicknessMm = tNew,
            DecorId = panel.DecorId,
            SubstrateId = panel.SubstrateId,
            ColorName = panel.ColorName,
            SurfaceMode = panel.SurfaceMode,
            Quantity = panel.Quantity,
            AllowedRotations = panel.AllowedRotations,
            GrainDirection = panel.GrainDirection,
            Outline = panel.Outline,
            Features = feats,
            Faces = panel.Faces,
            Identity = panel.Identity,
            Orientation = panel.Orientation,
            EdgeBanding = panel.EdgeBanding,
            Notes = panel.Notes,
            Side = panel.Side,
        };
    }

    static PanelFeature RewriteFeature(
        PanelFeature f,
        double tOld,
        double tNew,
        BlindFeatureDepthPolicy blindPolicy)
    {
        if (IsThroughOrFullSlot(f, tOld))
        {
            return CloneFeature(f, through: true, depthMm: tNew);
        }

        if (IsHalfSlotOrHinge(f, tOld))
        {
            if (blindPolicy == BlindFeatureDepthPolicy.Keep || f.DepthMm is not { } depth)
                return f;
            if (tOld <= 1e-9)
                return f;
            var scaled = depth * (tNew / tOld);
            if (scaled >= tNew - 0.05)
                scaled = Math.Max(0.1, tNew - 0.1);
            return CloneFeature(f, depthMm: scaled);
        }

        return f;
    }

    static PanelFeature CloneFeature(PanelFeature f, bool? through = null, double? depthMm = null) =>
        new()
        {
            FeatureId = f.FeatureId,
            Kind = f.Kind,
            FaceId = f.FaceId,
            Through = through ?? f.Through,
            GroupId = f.GroupId,
            Purpose = f.Purpose,
            SourceRelationshipId = f.SourceRelationshipId,
            X = f.X,
            Y = f.Y,
            DiameterMm = f.DiameterMm,
            DepthMm = depthMm ?? f.DepthMm,
            WidthMm = f.WidthMm,
            Path = f.Path,
            Profile = f.Profile,
            Holes = f.Holes,
        };

    static IReadOnlyList<SheetStock> MergeSheets(
        IReadOnlyList<SheetStock> sheets,
        HashSet<NestGroupKey> selected,
        NestGroupKey target)
    {
        var keep = sheets.FirstOrDefault(s => NestGroupKey.From(s.Material, s.ThicknessMm).Equals(target));
        var donor = sheets.FirstOrDefault(s => selected.Contains(NestGroupKey.From(s.Material, s.ThicknessMm)));
        var rest = sheets
            .Where(s => !selected.Contains(NestGroupKey.From(s.Material, s.ThicknessMm)))
            .ToList();
        if (keep is not null)
        {
            rest.Insert(0, keep);
            return rest;
        }

        if (donor is null)
            return rest;

        rest.Insert(0, new SheetStock
        {
            SheetId = donor.SheetId,
            Material = target.Material == "(unspecified)" ? donor.Material : target.Material,
            ThicknessMm = target.ThicknessMm,
            WidthMm = donor.WidthMm,
            LengthMm = donor.LengthMm,
            MarginMm = donor.MarginMm,
            KerfMm = donor.KerfMm,
            PartClearanceMm = donor.PartClearanceMm,
            DefectRegions = donor.DefectRegions,
        });
        return rest;
    }
}
