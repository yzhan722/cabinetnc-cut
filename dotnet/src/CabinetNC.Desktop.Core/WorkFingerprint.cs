using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CabinetNC.Infrastructure.Projects;

namespace CabinetNC.Desktop.Core;

public readonly record struct PlacementKey(string PanelId, int SheetIndex, double OffsetX, double OffsetY, double RotationDeg);

/// <summary>
/// Fingerprint of everything a project save would persist, minus view state, so
/// "unsaved changes" means the operator did work — not that they switched tabs.
/// </summary>
public static class WorkFingerprint
{
    public static string Compute(
        string? packageJson,
        IEnumerable<PlacementKey> placements,
        ProjectSessionState session,
        string projectName,
        string machineId)
    {
        // View-only state must not count as work.
        session.Stage = "";
        session.ActiveNestSheet = 0;
        session.OpsAllSheets = true;
        session.ShowNest = false;
        session.SelectedExportFile = null;
        // Ops are derived from placements + CAM settings, both of which are already covered;
        // dropping them keeps the hash cheap on large jobs.
        session.Ops = [];

        var nest = string.Join(";", placements.Select(p =>
            string.Create(CultureInfo.InvariantCulture, $"{p.PanelId}:{p.SheetIndex}:{p.OffsetX:0.###}:{p.OffsetY:0.###}:{p.RotationDeg:0.#}")));
        var raw = string.Concat(
            packageJson ?? "", "\n",
            nest, "\n",
            ProjectSessionCodec.Serialize(session), "\n",
            projectName, "\n",
            machineId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
