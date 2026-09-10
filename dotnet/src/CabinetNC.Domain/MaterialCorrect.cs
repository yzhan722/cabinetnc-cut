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
        var ids = package.Panels
            .Where(p => pick.Contains(NestGroupKey.From(p.Material, p.ThicknessMm)))
            .Select(p => p.PanelId)
            .ToList();
        return RetargetPanels(package, ids, target, blindPolicy);
    }

    /// <summary>Rewrite selected panels onto <paramref name="target"/>; drop emptied-kind sheets.</summary>
    public static CutPackage RetargetPanels(
        CutPackage package,
        IReadOnlyList<string> panelIds,
        NestGroupKey target,
        BlindFeatureDepthPolicy blindPolicy)
    {
        if (package.Panels.Count == 0 || panelIds.Count == 0)
            return package;

        var idSet = panelIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var donor = package.Panels.FirstOrDefault(p =>
            !idSet.Contains(p.PanelId) && SameKind(p, target));
        var changed = false;
        var panels = package.Panels.Select(p =>
        {
            if (!idSet.Contains(p.PanelId) || SameKind(p, target))
                return p;
            changed = true;
            return RewritePanel(p, target, blindPolicy, donor);
        }).ToList();
        if (!changed)
            return package;

        return package.WithPanels(panels).WithSheets(SyncSheets(package.Sheets, panels, target));
    }

    static Panel RewritePanel(
        Panel panel,
        NestGroupKey target,
        BlindFeatureDepthPolicy blindPolicy,
        Panel? donor)
    {
        var tOld = panel.ThicknessMm;
        var tNew = target.ThicknessMm;
        var material = target.Material == "(unspecified)"
            ? (donor?.Material ?? panel.Material)
            : target.Material;
        var feats = panel.Features
            .Select(f => RewriteFeature(f, tOld, tNew, blindPolicy))
            .ToList();
        return new Panel
        {
            PanelId = panel.PanelId,
            Name = panel.Name,
            Material = material,
            ThicknessMm = tNew,
            DecorId = donor?.DecorId ?? panel.DecorId,
            SubstrateId = donor?.SubstrateId ?? panel.SubstrateId,
            ColorName = donor?.ColorName ?? panel.ColorName,
            SurfaceMode = donor?.SurfaceMode ?? panel.SurfaceMode,
            Quantity = panel.Quantity,
            AllowedRotations = panel.AllowedRotations,
            GrainDirection = panel.GrainDirection,
            Outline = panel.Outline,
            Features = feats,
            Faces = panel.Faces,
            Identity = WithDonorRole(panel.Identity, donor?.Identity),
            Orientation = panel.Orientation,
            EdgeBanding = panel.EdgeBanding,
            Notes = panel.Notes,
            Side = panel.Side,
        };
    }

    static WorkpieceIdentity? WithDonorRole(WorkpieceIdentity? dest, WorkpieceIdentity? donor)
    {
        if (donor is null || string.IsNullOrWhiteSpace(donor.Role))
            return dest;
        if (dest is null)
            return new WorkpieceIdentity { Role = donor.Role };
        if (string.Equals(dest.Role, donor.Role, StringComparison.Ordinal))
            return dest;
        return new WorkpieceIdentity
        {
            PackageId = dest.PackageId,
            PackageLabel = dest.PackageLabel,
            ProjectId = dest.ProjectId,
            ModuleId = dest.ModuleId,
            WorkpieceId = dest.WorkpieceId,
            Role = donor.Role,
            SourcePath = dest.SourcePath,
            SourceFormat = dest.SourceFormat,
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

    static IReadOnlyList<SheetStock> SyncSheets(
        IReadOnlyList<SheetStock> sheets,
        IReadOnlyList<Panel> panels,
        NestGroupKey target)
    {
        var remaining = panels
            .Select(p => NestGroupKey.From(p.Material, p.ThicknessMm))
            .ToHashSet();
        var kept = new List<SheetStock>();
        var seen = new HashSet<NestGroupKey>();
        foreach (var s in sheets)
        {
            var key = NestGroupKey.From(s.Material, s.ThicknessMm);
            if (!remaining.Contains(key) || !seen.Add(key))
                continue;
            kept.Add(s);
        }

        if (!remaining.Contains(target) || seen.Contains(target))
            return kept;

        var donor = sheets.FirstOrDefault(s => NestGroupKey.From(s.Material, s.ThicknessMm).Equals(target))
            ?? sheets.FirstOrDefault();
        if (donor is null)
            return kept;

        kept.Insert(0, new SheetStock
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
        return kept;
    }
}
