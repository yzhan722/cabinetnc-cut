using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;

namespace CabinetNC.Domain.Tests;

public class MachineCatalogTests
{
    [Fact]
    public void Default_is_osai_e4_1325_only()
    {
        var p = MachineCatalog.Get(null);
        Assert.Equal(MachineCatalog.DefaultId, p.Id);
        Assert.Equal("OSAI E4 1325", p.Name);
        Assert.Equal(2, MachineCatalog.All.Count);
        var syntec = Assert.Single(MachineCatalog.All, m => m.Id == SyntecPost.MachineId);
        Assert.Equal("新代 E4 1330", syntec.Name);
        Assert.Equal(MachineCatalog.DefaultId, MachineCatalog.Get("unknown_machine").Id);
    }
}
