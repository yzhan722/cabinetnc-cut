using CabinetNC.Desktop.Core;

namespace CabinetNC.Desktop.Core.Tests;

public class FileRoutingTests
{
    [Theory]
    [InlineData(@"D:\jobs\S1.anc", "anc")]
    [InlineData(@"D:\jobs\S1.NC", "anc")]
    [InlineData(@"D:\jobs\lounge\project.db", "project")]
    [InlineData(@"D:\jobs\kitchen.cnjob", "package")]
    [InlineData(@"D:\jobs\wood.zip", "package")]
    [InlineData(@"D:\jobs\manifest.json", "package")]
    [InlineData(@"D:\jobs\readme.txt", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Extension_decides_the_opener(string? path, string? expected) =>
        Assert.Equal(expected, FileRouting.KindFor(path));

    [Fact]
    public void First_openable_skips_switches_missing_files_and_unknown_types()
    {
        string[] args = ["--debug", "/x", @"D:\missing.anc", @"D:\notes.txt", "\"D:\\jobs\\S1.anc\"", @"D:\jobs\S2.anc"];
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\jobs\S1.anc", @"D:\jobs\S2.anc", @"D:\notes.txt" };
        Assert.Equal(@"D:\jobs\S1.anc", FileRouting.FirstOpenable(args, existing.Contains));
        Assert.Null(FileRouting.FirstOpenable(["--debug"], existing.Contains));
        Assert.Null(FileRouting.FirstOpenable([], existing.Contains));
    }
}
