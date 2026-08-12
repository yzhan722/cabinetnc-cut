using CabinetNC.Domain.Machines;

namespace CabinetNC.Domain.Tests;

public class MachineCatalogTests
{
    [Fact]
    public void Default_is_osai_e4_1325_only()
    {
        var p = MachineCatalog.Get(null);
        Assert.Equal(MachineCatalog.DefaultId, p.Id);
        Assert.Equal("OSAI E4 1325", p.Name);
        Assert.Single(MachineCatalog.All);
        Assert.Equal(MachineCatalog.DefaultId, MachineCatalog.Get("unknown_machine").Id);
    }
}
