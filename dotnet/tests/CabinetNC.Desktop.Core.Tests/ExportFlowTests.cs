using CabinetNC.Desktop.Core;
using CabinetNC.Domain.Manufacturing;

namespace CabinetNC.Desktop.Core.Tests;

public class ExportFlowTests
{
    static LabelPaste Paste(string stem, string panel = "P") => new() { PanelId = panel, Stem = stem, SheetX = 100, SheetY = 200 };

    static string AncWithLabels(params string[] stems) =>
        LabelExport.WrapCutWithLabelProcess("N1 G90\r\nN2 M30\r\n", LabelExport.EmitPro2(stems.Select(s => Paste(s)).ToList()));

    [Fact]
    public void Plan_skips_files_without_valid_gcode()
    {
        var plan = ExportFlow.Plan(
        [
            new ExportItem("a.anc", "N1 G90\r\nN2 M30\r\n", []),
            new ExportItem("b.anc", "// emitter failed", []),
            new ExportItem("c.anc", "   ", []),
            new ExportItem("d.anc", null, []),
        ]);
        Assert.Equal(["a.anc"], plan.Files.Select(f => f.RelativeName));
        Assert.Equal(["b.anc", "c.anc", "d.anc"], plan.Skipped);
        Assert.False(plan.IsEmpty);
        Assert.Equal(0, plan.LabelCount);
    }

    [Fact]
    public void Bitmaps_are_flat_stem_bmp_and_expected_stems_come_from_the_program()
    {
        var anc = AncWithLabels("OHC_D0", "OHC_D1");
        var plan = ExportFlow.Plan([new ExportItem("s1.anc", anc, [Paste("OHC_D0"), Paste("OHC_D1")])]);
        Assert.Equal(["OHC_D0.bmp", "OHC_D1.bmp"], plan.Bitmaps.Select(b => b.RelativeName));
        Assert.Equal(["OHC_D0", "OHC_D1"], plan.ExpectedStems);
        Assert.Equal(2, plan.LabelCount);
    }

    [Fact]
    public void Duplicate_stems_across_sheets_are_written_once()
    {
        var plan = ExportFlow.Plan(
        [
            new ExportItem("s1.anc", AncWithLabels("A"), [Paste("A")]),
            new ExportItem("s2.anc", AncWithLabels("a", "B"), [Paste("a"), Paste("B")]),
        ]);
        Assert.Equal(2, plan.Bitmaps.Count);
        Assert.Equal(["A", "B"], plan.ExpectedStems);
    }

    [Fact]
    public void Missing_compares_case_insensitively_against_what_is_on_disk()
    {
        var plan = ExportFlow.Plan([new ExportItem("s1.anc", AncWithLabels("OHC_D0", "OHC_D1"), [Paste("OHC_D0"), Paste("OHC_D1")])]);
        Assert.Empty(ExportFlow.Missing(plan, ["ohc_d0", "OHC_D1"]));
        Assert.Equal(["OHC_D1"], ExportFlow.Missing(plan, ["OHC_D0"]));
        Assert.Equal(["OHC_D0", "OHC_D1"], ExportFlow.Missing(plan, []));
    }

    [Fact]
    public void Program_that_requests_a_label_nobody_rendered_is_reported_missing()
    {
        // The NC asks for two stems but the export only carries one paste (e.g. a stale label list).
        var plan = ExportFlow.Plan([new ExportItem("s1.anc", AncWithLabels("A", "B"), [Paste("A")])]);
        var onDisk = plan.Bitmaps.Select(b => Path.GetFileNameWithoutExtension(b.RelativeName));
        Assert.Equal(["B"], ExportFlow.Missing(plan, onDisk));
    }

    [Fact]
    public void Programs_without_process_2_expect_no_bitmaps()
    {
        var plan = ExportFlow.Plan([new ExportItem("plain.nc", "N1 G90\r\nN2 M30\r\n", [])]);
        Assert.Empty(plan.ExpectedStems);
        Assert.Empty(ExportFlow.Missing(plan, []));
    }
}
