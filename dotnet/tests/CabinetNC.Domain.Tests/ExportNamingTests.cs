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
    public void AncFileName_is_project_kind_ordinal()
    {
        Assert.Equal(
            "ClubLounge_Carcass_WhiteStipple_DS_15mm_01.anc",
            ExportNaming.AncFileName("Club Lounge", "Carcass_White Stipple_DS · 15mm", 1));
    }
}
