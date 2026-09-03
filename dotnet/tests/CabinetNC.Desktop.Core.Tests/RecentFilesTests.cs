using CabinetNC.Desktop.Core;
using CabinetNC.Infrastructure.Library;

namespace CabinetNC.Desktop.Core.Tests;

public class RecentFilesTests
{
    static readonly DateTimeOffset T0 = new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Newest_first_and_deduplicated_case_insensitively()
    {
        var list = RecentFiles.Remember([], @"D:\jobs\a.cnjob", "package", T0);
        list = RecentFiles.Remember(list, @"D:\jobs\b.db", "project", T0.AddMinutes(1));
        list = RecentFiles.Remember(list, @"d:\JOBS\A.cnjob", "package", T0.AddMinutes(2));

        Assert.Equal(2, list.Count);
        Assert.Equal(@"d:\JOBS\A.cnjob", list[0].Path);
        Assert.Equal("project", list[1].Kind);
    }

    [Fact]
    public void List_is_bounded()
    {
        var list = new List<RecentFile>();
        for (var i = 0; i < 15; i++)
            list = RecentFiles.Remember(list, $@"D:\jobs\{i}.anc", "anc", T0.AddMinutes(i), max: 10);
        Assert.Equal(10, list.Count);
        Assert.Equal(@"D:\jobs\14.anc", list[0].Path);
        Assert.Equal(@"D:\jobs\5.anc", list[^1].Path);
    }

    [Fact]
    public void Without_removes_only_that_path()
    {
        var list = RecentFiles.Remember([], @"D:\a.anc", "anc", T0);
        list = RecentFiles.Remember(list, @"D:\b.anc", "anc", T0);
        var pruned = RecentFiles.Without(list, @"D:\A.ANC");
        Assert.Single(pruned);
        Assert.Equal(@"D:\b.anc", pruned[0].Path);
    }

    [Fact]
    public void Underscores_are_escaped_for_wpf_access_keys()
    {
        Assert.Equal("probe__two__panels.anc", RecentFiles.EscapeAccessKeys("probe_two_panels.anc"));
        Assert.Equal("plain.anc", RecentFiles.EscapeAccessKeys("plain.anc"));
    }

    [Theory]
    [InlineData("project", "工程")]
    [InlineData("anc", ".anc")]
    [InlineData("package", "方案")]
    [InlineData(null, "方案")]
    public void Kind_labels(string? kind, string expected) => Assert.Equal(expected, RecentFiles.KindLabel(kind));
}
