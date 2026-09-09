using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class LabelExportTests
{
    static Panel Rect(string id, string? name, double w, double h) =>
        new()
        {
            PanelId = id,
            Name = name,
            ThicknessMm = 18,
            Material = "PB-WHITE",
            Outline = new Outline
            {
                Points = [new(0, 0), new(w, 0), new(w, h), new(0, h)],
            },
        };

    [Fact]
    public void Ls11Stems_reads_every_label_request_in_program_order()
    {
        var pastes = new[]
        {
            new LabelPaste { PanelId = "A", Stem = "OHC_OH_D0_2", SheetX = 226.266, SheetY = 218.491 },
            new LabelPaste { PanelId = "B", Stem = "OHC_OH_D1_2", SheetX = 500, SheetY = 300 },
        };
        var anc = LabelExport.WrapCutWithLabelProcess("N1 G90\r\nN2 M30\r\n", LabelExport.EmitPro2(pastes));
        Assert.Equal(["OHC_OH_D0_2", "OHC_OH_D1_2"], LabelExport.Ls11Stems(anc));
    }

    [Fact]
    public void MissingBitmaps_names_stems_without_a_picture_case_insensitively()
    {
        var pastes = new[]
        {
            new LabelPaste { PanelId = "A", Stem = "OHC_OH_D0_2" },
            new LabelPaste { PanelId = "B", Stem = "OHC_OH_D1_2" },
        };
        var anc = LabelExport.EmitPro2(pastes);
        Assert.Empty(LabelExport.MissingBitmaps(anc, ["ohc_oh_d0_2", "OHC_OH_D1_2"]));
        Assert.Equal(["OHC_OH_D1_2"], LabelExport.MissingBitmaps(anc, ["OHC_OH_D0_2"]));
        Assert.Empty(LabelExport.MissingBitmaps("N1 G90\r\nN2 M30\r\n", []));
    }

    [Fact]
    public void SafeStem_strips_apostrophe_and_spaces()
    {
        Assert.Equal("226_Club_Kitchen_V3", LabelExport.SafeStem("22'6 Club · Kitchen-V3"));
        Assert.DoesNotContain('\'', LabelExport.SafeStem("Rouge 22'6 Lounge"));
    }

    [Fact]
    public void Build_uses_anchor_centre_and_unique_stems()
    {
        var panels = new[]
        {
            Rect("A", "Kitchen-V3", 400, 300),
            Rect("B", "Kitchen-V3", 400, 300),
        };
        var places = new[]
        {
            new NestPlacement { PanelId = "A", SheetIndex = 0, OffsetX = 10, OffsetY = 20, RotationDeg = 0 },
            new NestPlacement { PanelId = "B", SheetIndex = 0, OffsetX = 50, OffsetY = 20, RotationDeg = 0 },
        };
        var pastes = LabelExport.Build(panels, places);
        Assert.Equal(2, pastes.Count);
        Assert.Equal(10 + 200, pastes[0].SheetX, 1);
        Assert.Equal(20 + 150, pastes[0].SheetY, 1);
        Assert.NotEqual(pastes[0].Stem, pastes[1].Stem);
        Assert.DoesNotContain('\'', pastes[0].Stem);
        Assert.True(pastes[0].Stem.Length <= LabelExport.StemMaxLen);
    }

    [Fact]
    public void Build_uses_override_sheet_point()
    {
        var panels = new[] { Rect("A", "Kitchen-V3", 400, 300) };
        var places = new[]
        {
            new NestPlacement { PanelId = "A", SheetIndex = 0, OffsetX = 10, OffsetY = 20, RotationDeg = 0 },
        };
        var ov = new Dictionary<string, (double X, double Y)> { ["A"] = (80, 40) };
        var pastes = LabelExport.Build(panels, places, ov);
        Assert.Equal(10 + 80, pastes[0].SheetX, 1);
        Assert.Equal(20 + 40, pastes[0].SheetY, 1);
    }

    [Fact]
    public void EmitPro2_writes_LS11_M701_UV_M702()
    {
        var nc = LabelExport.EmitPro2(
        [
            new LabelPaste
            {
                PanelId = "A",
                Stem = "Kitchen_V3",
                SheetIndex = 0,
                SheetX = 364,
                SheetY = 187,
                Title = "V3",
            },
        ]);
        Assert.Contains("LS11='Kitchen_V3'", nc);
        Assert.Contains("M701", nc);
        Assert.Contains("G90 G0 V187.000 U364.000", nc);
        Assert.Contains("M702", nc);
        Assert.DoesNotContain('\'', nc.Replace("LS11='Kitchen_V3'", "", StringComparison.Ordinal));
    }

    [Fact]
    public void ShopPartTitle_keeps_cabinet_and_part()
    {
        var kitchen = Rect("A", "Kitchen-V3", 400, 300);
        Assert.Equal("Kitchen V3", LabelExport.ShopPartTitle(kitchen));

        var ohc = Rect("B", "OHC_1-OH_D1 (2)", 417, 400);
        Assert.Equal("OHC OH D1 (2)", LabelExport.ShopPartTitle(ohc));

        var generic = Rect("C", "Ensuite_lower_cabinet_1-Component43", 600, 400);
        Assert.Equal("Ensuite lower cabinet", LabelExport.ShopPartTitle(generic));
    }

    [Fact]
    public void ShopStockShort_maps_common_decors()
    {
        var panel = new Panel
        {
            PanelId = "p1",
            Name = "Kitchen-OHC-D1",
            ThicknessMm = 15,
            ColorName = "White Stipple",
            SurfaceMode = "DOUBLE_SIDED",
            Identity = new WorkpieceIdentity
            {
                Role = "carcass",
                PackageLabel = "19'6 Rear Door",
            },
            Outline = new Outline
            {
                Points = [new(0, 0), new(417, 0), new(417, 400), new(0, 400)],
            },
        };
        Assert.Equal("白点 DS", LabelExport.ShopStockShort(panel));
        Assert.Equal("19'6 Rear Door", LabelExport.ShopProject(panel, "Bedroom Style 3"));
        Assert.Equal("Kitchen OHC-D1", LabelExport.ShopPartTitle(panel));
    }

    [Fact]
    public void Build_fills_shop_artwork_fields()
    {
        var panel = new Panel
        {
            PanelId = "A",
            Name = "OHC_1-OH_BP",
            ThicknessMm = 15,
            ColorName = "Wood Grain",
            SurfaceMode = "SINGLE_SIDED",
            Identity = new WorkpieceIdentity { PackageLabel = "Bedroom Style 3" },
            Outline = new Outline
            {
                Points = [new(0, 0), new(400, 0), new(400, 300), new(0, 300)],
            },
        };
        var pastes = LabelExport.Build(
            [panel],
            [new NestPlacement { PanelId = "A", SheetIndex = 2, OffsetX = 10, OffsetY = 20, RotationDeg = 0 }],
            projectFallback: "19'6 Rear Door");
        Assert.Equal("OHC OH BP", pastes[0].Title);
        Assert.Equal("Bedroom Style 3", pastes[0].Project);
        Assert.Equal("木纹 SS", pastes[0].Material);
        Assert.Equal(15, pastes[0].ThicknessMm);
        Assert.Equal(400, pastes[0].WidthMm, 1);
        Assert.Equal(300, pastes[0].HeightMm, 1);
    }

    [Fact]
    public void Wrap_puts_PRO2_before_cut()
    {
        var wrapped = LabelExport.WrapCutWithLabelProcess("N1 G90\r\nN2 M30\r\n", LabelExport.EmitPro2(
        [
            new LabelPaste { PanelId = "A", Stem = "P1", SheetX = 1, SheetY = 2 },
        ]));
        Assert.Contains("(GTO,PRO2,!PROC(0)=2)", wrapped);
        Assert.Contains("\"PRO2\"", wrapped);
        Assert.Contains("\"PRO1\"", wrapped);
        Assert.True(wrapped.IndexOf("\"PRO2\"", StringComparison.Ordinal)
                    < wrapped.IndexOf("\"PRO1\"", StringComparison.Ordinal));
        Assert.Contains("N1 G90", wrapped);
    }
}
