using System.IO;
using System.Text;
using System.Windows;
using CabinetNC.Desktop.Core;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Infrastructure.Diagnostics;

namespace CabinetNC.Desktop;

public partial class MainWindow
{
    CloudJobsWindow? _cloudJobs;

    void OnCloudJobsClick(object sender, RoutedEventArgs e)
    {
        if (_cloudJobs is { IsVisible: true })
        {
            _cloudJobs.Activate();
            return;
        }
        _cloudJobs = new CloudJobsWindow(PrepareCloudUpload, name =>
        {
            SetStatus("已上传到车间 · " + name);
            ShowToast("已上传到车间", name + "：工程文件。", StatusSeverity.Success);
            UsageLog.LogActionResult("cloud.upload", new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["project"] = name,
            });
        }, OpenCloudProject)
        { Owner = this };
        _cloudJobs.Closed += (_, _) => _cloudJobs = null;
        _cloudJobs.Show();
    }

    void OnOpenCloudClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardUnsavedWork("打开云端工程")) return;
        OnCloudJobsClick(sender, e);
    }

    void OpenCloudProject(string path)
    {
        OpenProjectPath(path);
        UsageLog.LogActionResult("cloud.open", new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["path"] = path,
        });
    }

    (string? Problem, CloudUploadBundle? Bundle) PrepareCloudUpload()
    {
        if (_session.Package is null || string.IsNullOrWhiteSpace(_session.PackageJson))
            return ("请先载入方案。", null);

        var sheets = _nest is { Ok: true } ? _nest.SheetCount : 0;
        var temp = Path.Combine(Path.GetTempPath(), "omnicam-upload-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            _store.Save(temp, BuildProjectDocument(_session.ResolvedProjectName));
            return (null, new CloudUploadBundle(
                _session.ResolvedProjectName,
                sheets,
                SelectedMachineId(),
                [new CloudUploadFile("project.db", File.ReadAllBytes(temp))]));
        }
        catch (Exception ex)
        {
            UsageLog.LogActionResult("cloud.upload.prepare", new Dictionary<string, object?>
            {
                ["project"] = _session.ResolvedProjectName,
            }, error: ex.Message);
            return ("准备上传失败：" + ex.Message, null);
        }
        finally
        {
            TryDelete(temp);
            TryDelete(temp + "-wal");
            TryDelete(temp + "-shm");
        }
    }

    readonly record struct SyntecBlob(string RelativePath, byte[] Bytes);

    List<SyntecBlob> BuildSyntecBlobs(IReadOnlyList<ExportNcFile> usable)
    {
        var sheets = usable.Select(f => new SyntecSheet
        {
            NcFileName = f.FileName,
            NcText = f.NcText,
            WidthMm = f.SheetWidthMm,
            LengthMm = f.SheetLengthMm,
            ThicknessMm = f.ThicknessMm,
            Color = f.Color,
            Labels = f.Labels,
        }).ToList();
        var (writtenFiles, bitmaps) = SyntecBundle.Build(sheets);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var blobs = new List<SyntecBlob>(writtenFiles.Count + bitmaps.Count + usable.Count * 2);
        foreach (var file in writtenFiles)
        {
            var bytes = file.Utf16 ? Encoding.Unicode.GetBytes(file.Text) : utf8.GetBytes(file.Text);
            blobs.Add(new SyntecBlob(file.RelativePath, bytes));
        }
        foreach (var bmp in bitmaps)
            blobs.Add(new SyntecBlob(bmp.RelativePath, LabelBmp.Render(bmp.Paste)));
        foreach (var sheet in usable)
        {
            var n = PlateOrdinal(sheet.FileName).ToString(System.Globalization.CultureInfo.InvariantCulture);
            blobs.Add(new SyntecBlob("label/W" + n + ".bmp",
                LabelBmp.RenderSheetPreview(sheet.SheetWidthMm, sheet.SheetLengthMm, sheet.Color, sheet.Ops, 720)));
            blobs.Add(new SyntecBlob("label/T" + n + ".bmp",
                LabelBmp.RenderSheetPreview(sheet.SheetWidthMm, sheet.SheetLengthMm, sheet.Color, sheet.Ops, 240)));
        }
        return blobs;
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A leftover temp snapshot must not fail the upload.
        }
    }
}
