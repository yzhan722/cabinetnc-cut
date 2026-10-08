using CabinetNC.Domain.Manufacturing;

namespace CabinetNC.Desktop.Core;

/// <summary>One program the export stage offers: file name, its NC text and the labels it pastes.</summary>
public sealed record ExportItem(string FileName, string? NcText, IReadOnlyList<LabelPaste> Labels);

public sealed record PlannedFile(string RelativeName, string Text);

public sealed record PlannedBitmap(string RelativeName, LabelPaste Paste);

/// <summary>
/// Everything an export will put on disk, decided before touching the file system.
/// Bitmaps are flat next to the programs because the shop label software only searches
/// its picture folder, not sub-folders (2026-08-19 incident).
/// </summary>
public sealed class ExportPlan
{
    public required IReadOnlyList<PlannedFile> Files { get; init; }
    public required IReadOnlyList<PlannedBitmap> Bitmaps { get; init; }
    /// <summary>Every LS11 stem the written programs will request, de-duplicated (case-insensitive).</summary>
    public required IReadOnlyList<string> ExpectedStems { get; init; }
    /// <summary>File names skipped because they had no valid G-code (empty or an error comment).</summary>
    public required IReadOnlyList<string> Skipped { get; init; }

    public bool IsEmpty => Files.Count == 0;
    public int LabelCount => Bitmaps.Count;
}

public static class ExportFlow
{
    public static bool HasValidNc(string? nc) =>
        !string.IsNullOrWhiteSpace(nc) && !nc.TrimStart().StartsWith("//", StringComparison.Ordinal);

    public static ExportPlan Plan(IEnumerable<ExportItem> items)
    {
        var files = new List<PlannedFile>();
        var bitmaps = new List<PlannedBitmap>();
        var skipped = new List<string>();
        var stems = new List<string>();
        var seenStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenBitmaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (!HasValidNc(item.NcText))
            {
                skipped.Add(item.FileName);
                continue;
            }
            files.Add(new PlannedFile(item.FileName, item.NcText!));
            foreach (var paste in item.Labels)
            {
                if (string.IsNullOrWhiteSpace(paste.Stem)) continue;
                if (seenBitmaps.Add(paste.Stem))
                    bitmaps.Add(new PlannedBitmap(paste.Stem + ".bmp", paste));
            }
            foreach (var stem in LabelExport.Ls11Stems(item.NcText))
            {
                if (seenStems.Add(stem))
                    stems.Add(stem);
            }
        }

        return new ExportPlan
        {
            Files = files,
            Bitmaps = bitmaps,
            ExpectedStems = stems,
            Skipped = skipped,
        };
    }

    /// <summary>
    /// Stems the programs request that are not among <paramref name="bitmapStemsOnDisk"/> —
    /// checked after writing, so a bitmap that failed to write is caught too.
    /// </summary>
    public static IReadOnlyList<string> Missing(ExportPlan plan, IEnumerable<string> bitmapStemsOnDisk)
    {
        var have = new HashSet<string>(bitmapStemsOnDisk, StringComparer.OrdinalIgnoreCase);
        return plan.ExpectedStems.Where(s => !have.Contains(s)).ToList();
    }
}
