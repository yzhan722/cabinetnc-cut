namespace CabinetNC.Cloud.Api.Tests;

public class CloudApiOptionsTests
{
    [Fact]
    public void FromSource_reads_only_the_named_environment_values()
    {
        var values = new Dictionary<string, string?>
        {
            [CloudApiOptions.DbConnectionKey] = "Host=db;Database=cabinetnc",
            [CloudApiOptions.JwtSigningKeyKey] = new string('k', 64),
            [CloudApiOptions.BootstrapTenantKey] = "shop",
            [CloudApiOptions.BootstrapAdminEmailKey] = "admin@shop.test",
            [CloudApiOptions.BootstrapAdminPasswordKey] = "test-password-only",
            ["ConnectionStrings:Default"] = "must-not-be-read",
        };

        var options = CloudApiOptions.FromSource(key =>
            values.TryGetValue(key, out var value) ? value : null);

        Assert.Equal(values[CloudApiOptions.DbConnectionKey], options.DbConnection);
        Assert.Equal(values[CloudApiOptions.JwtSigningKeyKey], options.JwtSigningKey);
        Assert.Equal("shop", options.BootstrapTenant);
        Assert.Equal("admin@shop.test", options.BootstrapAdminEmail);
        Assert.Equal("test-password-only", options.BootstrapAdminPassword);
    }

    [Fact]
    public void FromSource_has_no_default_database_or_signing_key()
    {
        var missingDb = Assert.Throws<InvalidOperationException>(() =>
            CloudApiOptions.FromSource(_ => null));
        Assert.Contains(CloudApiOptions.DbConnectionKey, missingDb.Message);

        var values = new Dictionary<string, string?>
        {
            [CloudApiOptions.DbConnectionKey] = "Host=db",
            [CloudApiOptions.JwtSigningKeyKey] = "weak-secret",
        };
        var weakKey = Assert.Throws<InvalidOperationException>(() =>
            CloudApiOptions.FromSource(key => values.GetValueOrDefault(key)));
        Assert.Contains(CloudApiOptions.JwtSigningKeyKey, weakKey.Message);
        Assert.DoesNotContain("weak-secret", weakKey.Message);
    }

    [Fact]
    public void FromSource_rejects_an_unreasonably_large_signing_key()
    {
        var values = new Dictionary<string, string?>
        {
            [CloudApiOptions.DbConnectionKey] = "Host=db",
            [CloudApiOptions.JwtSigningKeyKey] = new string('k', CloudApiOptions.MaxSigningKeyBytes + 1),
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            CloudApiOptions.FromSource(key => values.GetValueOrDefault(key)));

        Assert.Contains(CloudApiOptions.JwtSigningKeyKey, error.Message);
        Assert.DoesNotContain(new string('k', 100), error.Message);
    }
}
