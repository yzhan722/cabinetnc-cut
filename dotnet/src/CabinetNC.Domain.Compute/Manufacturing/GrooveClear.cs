namespace CabinetNC.Domain.Manufacturing;

using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

/// <summary>
/// Wide grooves (slot wider than the tool) must be area-cleared.
/// Tongue still uses T1 / half-depth; only the XY path becomes a 回转.
/// </summary>
public static class GrooveClear
{
    /// <summary>No onion skin — dado width must match CAD (16mm board into 16mm slot).</summary>
    public const double OnionSkinMm = 0;

    public static bool NeedsClear(double widthMm, double toolDiameterMm) =>
        CamStrategy.NeedsGrooveClear(widthMm, toolDiameterMm);

    public static double ResolveWidthMm(PanelFeature f)
    {
        if (f.WidthMm is > 1e-9)
            return f.WidthMm.Value;
        return GrooveGeometry.InferWidthMm(f.Path, f.Profile);
    }

    public static IReadOnlyList<(double X, double Y)> Outline(PanelFeature f, double widthMm)
    {
        if (f.Profile is { Count: >= 3 } profile)
            return profile.Select(p => (p.X, p.Y)).ToList();
        if (f.Path is { Count: >= 2 })
            return GrooveGeometry.OutlineFromCenterline(f.Path, widthMm)
                .Select(p => (p.X, p.Y)).ToList();
        return [];
    }

    public static PocketClearer.PocketClearResult? TryClear(
        PanelFeature f,
        double toolDiameterMm,
        LocalBounds? panelBounds = null)
    {
        var width = ResolveWidthMm(f);
        if (!NeedsClear(width, toolDiameterMm))
            return null;
        var outline = Outline(f, width);
        if (outline.Count < 3)
        {
            return new PocketClearer.PocketClearResult
            {
                Path = [],
                TooSmallForTool = true,
            };
        }

        return PocketClearer.Clear(new PocketClearer.PocketClearRequest
        {
            Outline = outline,
            ToolDiameterMm = toolDiameterMm,
            OnionSkinMm = OnionSkinMm,
            PanelBounds = panelBounds,
        });
    }
}
