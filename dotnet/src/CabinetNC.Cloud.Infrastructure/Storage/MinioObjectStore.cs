using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace CabinetNC.Cloud.Infrastructure.Storage;

/// <summary>
/// S3-compatible store on MinIO. Creates the bucket on first use. Reads are buffered in memory:
/// payloads are nest input/result JSON (kilobytes to a few megabytes), not media files.
/// </summary>
public sealed class MinioObjectStore : IObjectStore, IDisposable
{
    readonly IMinioClient _client;
    readonly string _bucket;
    readonly SemaphoreSlim _bucketGate = new(1, 1);
    volatile bool _bucketReady;

    public MinioObjectStore(ObjectStoreOptions options)
    {
        var builder = new MinioClient()
            .WithEndpoint(options.Endpoint)
            .WithCredentials(options.AccessKey, options.SecretKey)
            .WithSSL(options.UseSsl);
        if (!string.IsNullOrWhiteSpace(options.Region))
            builder = builder.WithRegion(options.Region);
        _client = builder.Build();
        _bucket = options.Bucket;
    }

    public async Task PutAsync(string key, Stream data, string contentType, CancellationToken ct)
    {
        ObjectKeyRules.EnsureSafe(key);
        await EnsureBucketAsync(ct);

        // MinIO needs the length up front; buffer forward-only streams.
        Stream upload = data;
        MemoryStream? buffered = null;
        if (!data.CanSeek)
        {
            buffered = new MemoryStream();
            await data.CopyToAsync(buffered, ct);
            buffered.Position = 0;
            upload = buffered;
        }
        else
        {
            upload.Position = 0;
        }

        try
        {
            await _client.PutObjectAsync(new PutObjectArgs()
                .WithBucket(_bucket)
                .WithObject(key)
                .WithStreamData(upload)
                .WithObjectSize(upload.Length)
                .WithContentType(contentType), ct);
        }
        finally
        {
            if (buffered is not null)
                await buffered.DisposeAsync();
        }
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        ObjectKeyRules.EnsureSafe(key);
        await EnsureBucketAsync(ct);

        var buffer = new MemoryStream();
        try
        {
            await _client.GetObjectAsync(new GetObjectArgs()
                .WithBucket(_bucket)
                .WithObject(key)
                .WithCallbackStream(async (stream, token) => await stream.CopyToAsync(buffer, token)), ct);
        }
        catch (ObjectNotFoundException)
        {
            await buffer.DisposeAsync();
            throw new ObjectStoreNotFoundException(key);
        }
        catch
        {
            await buffer.DisposeAsync();
            throw;
        }
        buffer.Position = 0;
        return buffer;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        ObjectKeyRules.EnsureSafe(key);
        await EnsureBucketAsync(ct);

        try
        {
            await _client.StatObjectAsync(new StatObjectArgs().WithBucket(_bucket).WithObject(key), ct);
            return true;
        }
        catch (ObjectNotFoundException)
        {
            return false;
        }
    }

    async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketReady)
            return;
        await _bucketGate.WaitAsync(ct);
        try
        {
            if (_bucketReady)
                return;
            if (!await BucketExistsWithStartupRetryAsync(ct))
            {
                try
                {
                    await _client.MakeBucketAsync(new MakeBucketArgs().WithBucket(_bucket), ct);
                }
                catch (MinioException)
                {
                    // Another process may have created it between our check and our MakeBucket.
                    if (!await _client.BucketExistsAsync(new BucketExistsArgs().WithBucket(_bucket), ct))
                        throw;
                }
            }
            _bucketReady = true;
        }
        finally
        {
            _bucketGate.Release();
        }
    }

    /// <summary>
    /// The first call after a MinIO (re)start can land in the window where the server accepts
    /// connections but still answers 503 with an empty body, which the SDK surfaces as a
    /// <see cref="NullReferenceException"/> from its error parser. Retry only this readiness probe;
    /// real problems such as bad credentials still surface after the last attempt.
    /// </summary>
    async Task<bool> BucketExistsWithStartupRetryAsync(CancellationToken ct)
    {
        const int attempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _client.BucketExistsAsync(new BucketExistsArgs().WithBucket(_bucket), ct);
            }
            catch (Exception ex) when (attempt < attempts && ex is MinioException or NullReferenceException or HttpRequestException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1)), ct);
            }
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _bucketGate.Dispose();
    }
}
