namespace CabinetNC.FusionPackage;

using System.IO.Compression;
using System.Text.Json;
using CabinetNC.Domain;

/// <summary>
/// Opens on-disk packages. Primary: <c>cabinetnc.manufacturing-snapshot</c> (.cnjob / zip / json).
/// Legacy: <c>cabinetnc.woodjob</c> (folder/.zip), <c>cabinetnc.cut-package</c> JSON.
/// </summary>
public static class PackageImporter
{
    public static PackageImportResult FromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new PackageImportResult
            {
                Ok = false,
                Errors = [new ValidationIssue("path", "$", "empty path")],
            };

        if (File.Exists(path)
            && (path.EndsWith(".cnjob", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
        {
            var format = PeekZipManifestFormat(path);
            if (string.Equals(format, ManufacturingSnapshot.SchemaName, StringComparison.Ordinal))
                return ManufacturingSnapshotImporter.FromArchive(path);
            if (string.Equals(format, CutPackage.WoodJobFormat, StringComparison.Ordinal)
                || WoodJobImporter.LooksLikeWoodJobZip(path))
                return WoodJobImporter.FromZip(path);
            if (path.EndsWith(".cnjob", StringComparison.OrdinalIgnoreCase))
                return ManufacturingSnapshotImporter.FromArchive(path);
            return new PackageImportResult
            {
                Ok = false,
                Errors =
                [
                    new ValidationIssue(
                        "format",
                        "manifest.format",
                        $"unsupported zip package format {(string.IsNullOrWhiteSpace(format) ? "(missing)" : format)}"),
                ],
            };
        }

        if (Directory.Exists(path))
            return WoodJobImporter.FromPath(path);

        if (File.Exists(path) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var json = File.ReadAllText(path);
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("schema", out var schema)
                    && schema.GetString() == ManufacturingSnapshot.SchemaName)
                    return ManufacturingSnapshotImporter.FromJson(json);
            }
            catch (JsonException)
            {
                // Let the legacy importer return its normal JSON diagnostic.
            }
            return CutPackageImporter.FromJson(json);
        }

        return new PackageImportResult
        {
            Ok = false,
            Errors =
            [
                new ValidationIssue(
                    "path",
                    path,
                    "unsupported package — use .cnjob, manufacturing-snapshot .json/.zip, woodjob folder/.zip, or cut-package .json"),
            ],
        };
    }

    /// <summary>Read <c>manifest.format</c> from a zip/.cnjob without extracting.</summary>
    public static string? PeekZipManifestFormat(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var manifestEntry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                ?? zip.Entries.FirstOrDefault(e =>
                    e.FullName.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase));
            if (manifestEntry is null) return null;
            using var stream = manifestEntry.Open();
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.TryGetProperty("format", out var format)
                ? format.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
