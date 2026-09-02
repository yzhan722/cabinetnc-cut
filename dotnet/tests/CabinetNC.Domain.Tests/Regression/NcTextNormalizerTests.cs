namespace CabinetNC.Domain.Tests.Regression;

public class NcTextNormalizerTests
{
    [Fact]
    public void Strips_n_words_blank_lines_and_decor_comments_keeps_tool_and_motion()
    {
        var raw = """
            (cabinetnc-cut nc · osai_e4_1325 · OSAI · generic)
            (wcs: sheet SW origin)
            (cam safety: drill→tongue→clearance→profile)
            (origin: table)

            N10 G90
            N20 (tool T3)
            N30 G0 X10.0000 Y20.0000
            N40 (sheet 1)
            N50 M2

            """;

        var got = NcTextNormalizer.Normalize(raw);

        Assert.Equal(
            "G90\n(tool T3)\nG0 X10.0000 Y20.0000\n(sheet 1)\nM2\n",
            got);
    }

    [Fact]
    public void Keeps_troy_uao_dly_and_m6t_strips_only_n_prefix()
    {
        var raw = "N1 (UAO,1)\r\nN2 M6 T3\r\nN3 (DLY,3)\r\nN4 G0 X0.0000 Y0.0000\r\n";
        var got = NcTextNormalizer.Normalize(raw);
        Assert.Equal("(UAO,1)\nM6 T3\n(DLY,3)\nG0 X0.0000 Y0.0000\n", got);
    }
}
