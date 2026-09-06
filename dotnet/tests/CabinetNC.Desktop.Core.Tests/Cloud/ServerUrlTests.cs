using CabinetNC.Desktop.Core.Cloud;

namespace CabinetNC.Desktop.Core.Tests.Cloud;

public class ServerUrlTests
{
    [Theory]
    [InlineData("https://cabinetnc.shop.local", "https://cabinetnc.shop.local/")]
    [InlineData("cabinetnc.shop.local", "https://cabinetnc.shop.local/")]
    [InlineData("HTTPS://CabinetNC.shop.local:8443/api/", "https://cabinetnc.shop.local:8443/api/")]
    [InlineData("http://localhost:8080", "http://localhost:8080/")]
    [InlineData("http://127.0.0.1:8080/", "http://127.0.0.1:8080/")]
    public void Accepts_https_anywhere_and_http_only_on_loopback(string typed, string expected)
    {
        Assert.True(CloudClientOptions.TryParseServerUrl(typed, out var uri, out var error), error);
        Assert.Equal(expected, uri.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://cabinetnc.shop.local")]
    [InlineData("http://192.168.1.20:8080")]
    [InlineData("ftp://cabinetnc.shop.local")]
    [InlineData("https://cabinetnc.shop.local/?x=1")]
    [InlineData("not a url at all")]
    public void Rejects_plaintext_on_the_lan_and_garbage(string typed)
    {
        Assert.False(CloudClientOptions.TryParseServerUrl(typed, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
