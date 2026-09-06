using CabinetNC.Cloud.Infrastructure.Storage;

namespace CabinetNC.Cloud.Infrastructure.Tests;

/// <summary>Keys come from server code today, but the store is the last line of defence against path games.</summary>
public class ObjectKeyRulesTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/tenant/a/jobs/b/input.json")]
    [InlineData("tenant\\a\\jobs\\b\\input.json")]
    [InlineData("../etc/passwd")]
    [InlineData("tenant/../other/input.json")]
    [InlineData("tenant/a/..")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("tenant/./a")]
    [InlineData("tenant//a")]
    [InlineData("tenant/a/")]
    [InlineData("tenant/a\u0000b")]
    [InlineData("tenant/a\nb")]
    public void Rejects_unsafe_keys(string key)
    {
        var ex = Assert.Throws<ArgumentException>(() => ObjectKeyRules.EnsureSafe(key));
        Assert.Equal("key", ex.ParamName);
    }

    [Fact]
    public void Rejects_overlong_keys()
    {
        var key = "tenant/" + new string('a', ObjectKeyRules.MaxLength);
        Assert.Throws<ArgumentException>(() => ObjectKeyRules.EnsureSafe(key));
    }

    [Theory]
    [InlineData("input.json")]
    [InlineData("tenant/a/jobs/b/input.json")]
    [InlineData("tenant/a-b_c.d/jobs/x.y/result.json")]
    [InlineData("tenant/a/jobs/b/..input.json")]
    public void Accepts_plain_relative_keys(string key)
    {
        ObjectKeyRules.EnsureSafe(key);
    }

    [Fact]
    public void Spec_layout_keys_are_safe()
    {
        var tenant = Guid.NewGuid();
        var job = Guid.NewGuid();

        ObjectKeyRules.EnsureSafe(ObjectKeys.JobInput(tenant, job));
        ObjectKeyRules.EnsureSafe(ObjectKeys.JobResult(tenant, job));
        Assert.Equal($"tenant/{tenant:D}/jobs/{job:D}/input.json", ObjectKeys.JobInput(tenant, job));
        Assert.Equal($"tenant/{tenant:D}/jobs/{job:D}/result.json", ObjectKeys.JobResult(tenant, job));
    }

    [Fact]
    public async Task Store_rejects_unsafe_keys_before_any_network_call()
    {
        // Endpoint that nothing listens on: an unsafe key must fail on validation, not on connect.
        var store = new MinioObjectStore(new ObjectStoreOptions
        {
            Endpoint = "127.0.0.1:1",
            AccessKey = "x",
            SecretKey = "y",
            Bucket = "never-created",
        });

        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync("../x", new MemoryStream([1]), "application/json", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.OpenReadAsync("/x", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ExistsAsync("a\\b", CancellationToken.None));
    }
}
