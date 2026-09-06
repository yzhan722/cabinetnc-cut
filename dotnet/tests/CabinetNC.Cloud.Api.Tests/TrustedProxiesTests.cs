using System.Net;
using CabinetNC.Cloud.Api.Http;

namespace CabinetNC.Cloud.Api.Tests;

public class TrustedProxiesTests
{
    [Fact]
    public void Empty_or_missing_means_no_proxy_is_trusted()
    {
        Assert.Empty(TrustedProxies.Parse(null));
        Assert.Empty(TrustedProxies.Parse(""));
        Assert.Empty(TrustedProxies.Parse("  "));
    }

    [Fact]
    public void Parses_cidrs_and_single_addresses()
    {
        var networks = TrustedProxies.Parse(" 172.28.100.0/24, 10.0.0.5 ,fd00::/8");

        Assert.Equal(3, networks.Count);
        Assert.True(networks[0].Contains(IPAddress.Parse("172.28.100.7")));
        Assert.False(networks[0].Contains(IPAddress.Parse("172.28.101.7")));
        Assert.True(networks[1].Contains(IPAddress.Parse("10.0.0.5")));
        Assert.False(networks[1].Contains(IPAddress.Parse("10.0.0.6")));
        Assert.True(networks[2].Contains(IPAddress.Parse("fd00::1")));
    }

    [Fact]
    public void Rejects_garbage_without_echoing_it()
    {
        var error = Assert.Throws<InvalidOperationException>(() => TrustedProxies.Parse("172.28.100.0/24,not-a-network"));

        Assert.Contains(TrustedProxies.Variable, error.Message);
        Assert.DoesNotContain("not-a-network", error.Message);
    }
}
