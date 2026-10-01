using CabinetNC.Domain;

namespace CabinetNC.Domain.Tests;

public class ExportNamingTests
{
    [Fact]
    public void FileStem_drops_spaces_and_turns_dot_into_separator()
    {
        Assert.Equal("ClubLoungeMainPart", ExportNaming.FileStem("Club Lounge Main Part"));
        Assert.Equal("Carcass_WhiteStipple_DS_15mm", ExportNaming.FileStem("Carcass_White Stipple_DS · 15mm"));
    }

    [Fact]
    public void AncFileName_is_thickness_ordinal_color_kind_project()
    {
        Assert.Equal(
            "15mm_01_WhiteStipple_Carcass_ClubLounge.anc",
            ExportNaming.AncFileName(1, 15, "White Stipple", "Carcass", "Club Lounge"));
    }
}
