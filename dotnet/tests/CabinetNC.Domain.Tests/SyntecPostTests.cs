using CabinetNC.Domain.Manufacturing;

namespace CabinetNC.Domain.Tests;

public class SyntecPostTests
{
    static string[] Lines(string nc) =>
        nc.Replace("\r\n", "\n").Trim().Split('\n');

    static CutOp Outer(double thickness = 18) => new()
    {
        Op = "contour",
        PanelId = "P1",
        ToolId = "T2",
        Placed = true,
        ClosePath = true,
        Through = true,
        ThicknessMm = thickness,
        DepthMm = thickness + 0.5,
        Path = [(0, 0), (200, 0), (200, 100), (0, 100)],
    };

    static HashSet<(double X, double Y)> XyMoves(string nc) =>
        Lines(nc)
            .Where(l => l.StartsWith("G0", StringComparison.Ordinal) && l.Contains(" X", StringComparison.Ordinal))
            .Select(l =>
            {
                var w = l.Split(' ');
                double V(char c) => double.Parse(
                    w.First(t => t[0] == c)[1..], System.Globalization.CultureInfo.InvariantCulture);
                return (V('X'), V('Y'));
            })
            .ToHashSet();

    [Fact]
    public void Machine_x_is_measured_from_the_right_and_the_shape_is_unchanged()
    {
        const double width = 1220;
        (double X, double Y)[] shape = [(300, 40), (900, 40), (900, 240), (500, 240), (500, 940), (300, 940)];
        var nc = SyntecPost.EmitNc([Outer() with { Path = shape }], width, 18);

        Assert.Equal(shape.Select(p => (width - p.X, p.Y)).ToHashSet(), XyMoves(nc));

        var back = SyntecPost.ToSheet(OsaiTroyParser.Replay(nc).Strokes, width)
            .Where(s => !s.Rapid)
            .Select(s => (Math.Round(s.X1, 2), Math.Round(s.Y1, 2)))
            .ToHashSet();
        Assert.Equal(shape.ToHashSet(), back);
    }

    [Fact]
    public void Program_is_g54_single_tool_and_cuts_through_the_spoilboard()
    {
        var nc = SyntecPost.EmitNc([Outer()], 1220, 18);
        var lines = Lines(nc);
        Assert.Equal(["G54", "T1", "M03 S18000", "G43 H1"], lines.Take(4));
        Assert.Equal(["M05", "M30"], lines.TakeLast(2));
        Assert.DoesNotContain(lines, l => l.StartsWith('N') || l.Contains("G55", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("G00 X", StringComparison.Ordinal));
        Assert.Contains("G01 Z18.00 F8000", lines);
        Assert.Contains("G01 Z-0.10 F3000", lines);
        Assert.Contains("G00 Z38.00 F12000", lines);
        Assert.Contains(lines, l => l.Contains("F12000", StringComparison.Ordinal) && l.StartsWith("G01 X", StringComparison.Ordinal));
    }

    [Fact]
    public void Blind_groove_stays_above_the_table()
    {
        var groove = new CutOp
        {
            Op = "groove",
            PanelId = "P1",
            ToolId = "T1",
            Placed = true,
            ClosePath = false,
            Through = false,
            ThicknessMm = 18,
            DepthMm = 9,
            Path = [(10, 10), (190, 10)],
        };
        var nc = SyntecPost.EmitNc([groove], 1220, 18);
        Assert.Contains("G01 Z9.00 F3000", Lines(nc));
        Assert.DoesNotContain("Z-0.10", nc);
    }

    [Fact]
    public void Drills_are_not_written()
    {
        var drill = new CutOp
        {
            Op = "drill",
            PanelId = "P1",
            ToolId = "T3",
            Placed = true,
            SheetX = 30,
            SheetY = 40,
            ThicknessMm = 18,
            DepthMm = 18,
            Through = true,
        };
        var nc = SyntecPost.EmitNc([drill, Outer()], 1220, 18);
        Assert.DoesNotContain("X30.00", nc);
        Assert.Contains("G54", nc);
    }

    [Fact]
    public void Bundle_lists_the_sheet_and_keeps_label_coordinates()
    {
        var nc = SyntecPost.EmitNc([Outer(18.3)], 1220, 18.3);
        var paste = new LabelPaste
        {
            PanelId = "P1",
            Stem = "door",
            SheetIndex = 0,
            SheetX = 100,
            SheetY = 40,
            Title = "门板",
        };
        var (files, bitmaps) = SyntecBundle.Build(
        [
            new SyntecSheet
            {
                NcFileName = "001-18.3.nc",
                NcText = nc,
                WidthMm = 1220,
                LengthMm = 2440,
                ThicknessMm = 18.3,
                Color = "冰山白",
                Labels = [paste],
            },
        ]);

        var xml = files.Single(f => f.RelativePath == "1.xml");
        Assert.False(xml.Utf16);
        Assert.Contains("PlateID\" Value=\"001-18.3.nc\"", xml.Text);
        Assert.Contains("LabelName\" Value=\"Label_001-18.3.cyc\"", xml.Text);
        Assert.Contains("Length\" Value=\"2440\"", xml.Text);
        Assert.Contains("Width\" Value=\"1220\"", xml.Text);
        Assert.Contains("Thickness\" Value=\"18.3\"", xml.Text);
        Assert.Contains("Color\" Value=\"冰山白\"", xml.Text);

        var cyc = files.Single(f => f.RelativePath == "label/Label_001-18.3.cyc");
        Assert.True(cyc.Utf16);
        Assert.Contains("encoding=\"utf-16\"", cyc.Text);
        Assert.Contains("LabelName\" Value=\"001-18.3_0001.bmp\"", cyc.Text);
        Assert.Contains("X\" Value=\"1120\"", cyc.Text);
        Assert.Contains("Y\" Value=\"40\"", cyc.Text);
        Assert.Contains("R\" Value=\"0\"", cyc.Text);
        Assert.Equal("label/001-18.3_0001.bmp", bitmaps[0].RelativePath);

        Assert.Equal("18", SyntecPost.ThicknessToken(18));
        Assert.Equal("012-18", SyntecPost.PlateStem(12, 18));
    }
}
