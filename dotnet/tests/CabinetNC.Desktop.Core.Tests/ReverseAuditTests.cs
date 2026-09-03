using CabinetNC.Desktop.Core;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Desktop.Core.Tests;

public class ReverseAuditTests
{
    static Panel Panel(string id, double w, double h, params PanelFeature[] features) => new()
    {
        PanelId = id,
        Outline = new Outline { Points = [new(0, 0), new(w, 0), new(w, h), new(0, h)] },
        Features = features,
    };

    static PanelFeature Feature(string kind, string id = "F") => new() { FeatureId = id, Kind = kind };

    static CutOp Op(string op) => new() { Op = op, PanelId = "P", ToolId = "T2" };

    static string FixturePath()
    {
        var walk = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var p = Path.Combine(walk, "dotnet", "tests", "ui-smoke", "fixtures", "two_panels.anc");
            if (File.Exists(p)) return p;
            var parent = Directory.GetParent(walk);
            if (parent is null) break;
            walk = parent.FullName;
        }
        throw new FileNotFoundException("two_panels.anc fixture not found");
    }

    [Fact]
    public void Real_program_with_two_panels_a_window_and_a_hole_is_fully_accounted()
    {
        var r = NcReverse.FromText(File.ReadAllText(FixturePath()));
        var s = ReverseAudit.Summarize(r);

        Assert.Equal(2, s.Panels);
        Assert.Equal(1, s.Windows);
        Assert.Equal(1, s.Drills);
        Assert.Equal(0, s.OrphanContours);
        Assert.Equal(0, s.OrphanFeatures);
        Assert.True(s.AllAccounted, string.Join("; ", s.Problems));
        Assert.Equal(2, s.Rows.Count);
        Assert.Contains(s.Rows, row => row.Features.Contains("开窗 1"));
        Assert.Contains("闭合外形 3 → 板 2 + 开窗 1", ReverseAudit.MetaLine(s, r.SafeZMm, r.ThicknessMm));
    }

    [Fact]
    public void Contour_that_became_neither_panel_nor_window_is_an_orphan()
    {
        var r = new NcReverseResult
        {
            Ops = [Op("contour"), Op("contour"), Op("contour")],
            Panels = [Panel("A", 300, 200, Feature("cutout", "W1"))],
        };
        var s = ReverseAudit.Summarize(r);
        Assert.Equal(1, s.OrphanContours);
        Assert.False(s.AllAccounted);
        Assert.Contains("1 个闭合外形没有归为板或开窗", s.Problems);
        Assert.Contains("缺失的特征不会出现在重切件上", ReverseAudit.WarningLine(s));
    }

    [Fact]
    public void Features_outside_every_panel_are_counted_per_kind()
    {
        var r = new NcReverseResult
        {
            Ops = [Op("contour"), Op("drill"), Op("drill"), Op("groove"), Op("pocket")],
            Panels = [Panel("A", 300, 200, Feature("holeVertical", "H1"))],
        };
        var s = ReverseAudit.Summarize(r);
        // 2 drills − 1 owned hole, 1 groove − 0, 1 pocket − 0
        Assert.Equal(3, s.OrphanFeatures);
        Assert.Equal(0, s.OrphanContours);
        Assert.Contains("3 个孔/槽/口袋不在任何板内", s.Problems);
    }

    [Fact]
    public void Reverse_warnings_are_surfaced_as_problems_and_remnant_cuts_are_reported()
    {
        var r = new NcReverseResult
        {
            Ops = [Op("contour"), Op("remnant")],
            Panels = [Panel("A", 300, 200)],
            Warnings = ["第 2 把刀未识别，按 T2 处理"],
        };
        var s = ReverseAudit.Summarize(r);
        Assert.False(s.AllAccounted);
        Assert.Equal(["第 2 把刀未识别，按 T2 处理"], s.Problems);
        Assert.Equal(1, s.RemnantCuts);
        Assert.Contains("余料切线 1", ReverseAudit.MetaLine(s, 30, 18));
        Assert.Equal("无特征", s.Rows[0].Features);
        Assert.Equal("300 × 200", s.Rows[0].Size);
    }
}
