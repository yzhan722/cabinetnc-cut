namespace CabinetNC.Domain.Parts;

using CabinetNC.Domain;

/// <summary>
/// Import workpieces from another package into a target package (Day 12 leftover).
/// Generates new PanelIds; preserves Material/Thickness/Identity module when present.
/// </summary>
public static class WorkpieceImporter
{
    public static CutPackage ImportPanels(
        CutPackage target,
        IEnumerable<Panel> sourcePanels,
        string? idPrefix = null)
    {
        var prefix = string.IsNullOrWhiteSpace(idPrefix) ? "IMP" : idPrefix.Trim();
        var list = target.Panels.ToList();
        var used = new HashSet<string>(list.Select(p => p.PanelId), StringComparer.OrdinalIgnoreCase);
        var n = 1;
        foreach (var src in sourcePanels)
        {
            string id;
            do { id = $"{prefix}_{n++}"; }
            while (!used.Add(id));

            var copy = PanelEdit.Duplicate(src, id);
            // Duplicate already remaps feature ids; keep source workpiece lineage in notes/identity
            copy = new Panel
            {
                PanelId = copy.PanelId,
                Name = copy.Name ?? src.Name,
                Material = copy.Material,
                ThicknessMm = copy.ThicknessMm,
                DecorId = copy.DecorId,
                SubstrateId = copy.SubstrateId,
                ColorName = copy.ColorName,
                SurfaceMode = copy.SurfaceMode,
                Quantity = copy.Quantity,
                AllowedRotations = copy.AllowedRotations,
                GrainDirection = copy.GrainDirection,
                Outline = copy.Outline,
                Features = copy.Features,
                Faces = copy.Faces,
                Identity = new WorkpieceIdentity
                {
                    ProjectId = src.Identity?.ProjectId,
                    ModuleId = src.Identity?.ModuleId,
                    WorkpieceId = copy.PanelId,
                    Role = src.Identity?.Role,
                    SourcePath = src.Identity?.SourcePath,
                    SourceFormat = src.Identity?.SourceFormat ?? "import",
                },
                Orientation = copy.Orientation,
                EdgeBanding = copy.EdgeBanding,
                Notes = string.IsNullOrWhiteSpace(src.Notes)
                    ? $"imported from {src.PanelId}"
                    : src.Notes,
                Side = copy.Side,
            };
            list.Add(copy);
        }

        return new CutPackage
        {
            SchemaName = target.SchemaName,
            Version = target.Version,
            JobId = target.JobId,
            Units = target.Units,
            Sheets = target.Sheets,
            Panels = list,
        };
    }
}
