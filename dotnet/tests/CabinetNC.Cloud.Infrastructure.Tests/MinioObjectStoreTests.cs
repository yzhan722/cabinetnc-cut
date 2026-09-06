using System.Security.Cryptography;
using System.Text;
using CabinetNC.Cloud.Infrastructure.Storage;
using Testcontainers.Minio;

namespace CabinetNC.Cloud.Infrastructure.Tests;

/// <summary>One MinIO per test class (external via CABINETNC_TEST_MINIO_*, else Testcontainers); a fresh bucket per class.</summary>
public sealed class MinioFixture : IAsyncLifetime
{
    public const string Image = "minio/minio:latest";

    MinioContainer? _container;

    public ObjectStoreOptions Options { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!MinioAvailability.IsAvailable)
            return;

        var bucket = "cabinetnc-test-" + Guid.NewGuid().ToString("N")[..12];
        var endpoint = Environment.GetEnvironmentVariable(MinioAvailability.EndpointVariable);
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            var uri = new Uri(endpoint.Contains("://") ? endpoint : "http://" + endpoint);
            Options = new ObjectStoreOptions
            {
                Endpoint = uri.Authority,
                UseSsl = uri.Scheme == Uri.UriSchemeHttps,
                AccessKey = Environment.GetEnvironmentVariable(MinioAvailability.AccessKeyVariable) ?? "",
                SecretKey = Environment.GetEnvironmentVariable(MinioAvailability.SecretKeyVariable) ?? "",
                Bucket = bucket,
            };
        }
        else
        {
            _container = new MinioBuilder(Image).Build();
            await _container.StartAsync();
            var uri = new Uri(_container.GetConnectionString());
            Options = new ObjectStoreOptions
            {
                Endpoint = uri.Authority,
                UseSsl = false,
                AccessKey = _container.GetAccessKey(),
                SecretKey = _container.GetSecretKey(),
                Bucket = bucket,
            };
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

public class MinioObjectStoreTests(MinioFixture minio) : IClassFixture<MinioFixture>
{
    static readonly CancellationToken CT = CancellationToken.None;

    IObjectStore NewStore() => new MinioObjectStore(minio.Options);

    static byte[] SamplePayload()
    {
        var head = Encoding.UTF8.GetBytes("{\"parts\":[{\"panelId\":\"A\",\"widthMm\":600}],\"blob\":\"");
        var body = new byte[64 * 1024 + 17];
        RandomNumberGenerator.Fill(body);
        var tail = Encoding.UTF8.GetBytes("\"}");
        return [.. head, .. body, .. tail];
    }

    [MinioFact]
    public async Task Put_then_OpenRead_returns_the_exact_bytes_for_the_job_input_key()
    {
        var store = NewStore();
        var key = ObjectKeys.JobInput(Guid.NewGuid(), Guid.NewGuid());
        var payload = SamplePayload();

        Assert.False(await store.ExistsAsync(key, CT));
        await store.PutAsync(key, new MemoryStream(payload), "application/json", CT);
        Assert.True(await store.ExistsAsync(key, CT));

        await using var read = await store.OpenReadAsync(key, CT);
        using var buffer = new MemoryStream();
        await read.CopyToAsync(buffer, CT);

        Assert.Equal(payload.Length, buffer.Length);
        Assert.Equal(payload, buffer.ToArray());
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(buffer.ToArray()));
    }

    [MinioFact]
    public async Task Missing_key_is_not_found()
    {
        var store = NewStore();
        var key = ObjectKeys.JobResult(Guid.NewGuid(), Guid.NewGuid());

        Assert.False(await store.ExistsAsync(key, CT));
        var ex = await Assert.ThrowsAsync<ObjectStoreNotFoundException>(() => store.OpenReadAsync(key, CT));
        Assert.Equal(key, ex.Key);
    }

    [MinioFact]
    public async Task Put_overwrites_the_previous_object()
    {
        var store = NewStore();
        var key = ObjectKeys.JobResult(Guid.NewGuid(), Guid.NewGuid());

        await store.PutAsync(key, new MemoryStream("first"u8.ToArray()), "application/json", CT);
        await store.PutAsync(key, new MemoryStream("second, longer"u8.ToArray()), "application/json", CT);

        await using var read = await store.OpenReadAsync(key, CT);
        using var reader = new StreamReader(read, Encoding.UTF8);
        Assert.Equal("second, longer", await reader.ReadToEndAsync(CT));
    }

    [MinioFact]
    public async Task Non_seekable_streams_are_stored_completely()
    {
        var store = NewStore();
        var key = ObjectKeys.JobInput(Guid.NewGuid(), Guid.NewGuid());
        var payload = SamplePayload();

        await store.PutAsync(key, new ForwardOnlyStream(payload), "application/json", CT);

        await using var read = await store.OpenReadAsync(key, CT);
        using var buffer = new MemoryStream();
        await read.CopyToAsync(buffer, CT);
        Assert.Equal(payload, buffer.ToArray());
    }

    [MinioFact]
    public async Task Two_stores_on_the_same_bucket_see_each_others_objects()
    {
        var writer = NewStore();
        var reader = NewStore();
        var key = ObjectKeys.JobInput(Guid.NewGuid(), Guid.NewGuid());

        await writer.PutAsync(key, new MemoryStream("shared"u8.ToArray()), "application/json", CT);

        Assert.True(await reader.ExistsAsync(key, CT));
    }

    sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
