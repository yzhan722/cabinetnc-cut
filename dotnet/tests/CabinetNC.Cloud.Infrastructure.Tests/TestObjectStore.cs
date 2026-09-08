using System.Collections.Concurrent;
using CabinetNC.Cloud.Infrastructure.Storage;

namespace CabinetNC.Cloud.Infrastructure.Tests;

/// <summary>Thread-safe test double shared by API/worker integration tests; never used by production.</summary>
public sealed class TestObjectStore : IObjectStore
{
    readonly ConcurrentDictionary<string, StoredObject> _objects = new(StringComparer.Ordinal);

    public Func<string, bool>? FailPutWhen { get; set; }
    public Func<string, bool>? FailReadWhen { get; set; }

    public IReadOnlyDictionary<string, StoredObject> Objects => _objects;

    public async Task PutAsync(string key, Stream data, string contentType, CancellationToken ct)
    {
        ObjectKeyRules.EnsureSafe(key);
        if (FailPutWhen?.Invoke(key) == true)
            throw new IOException("Injected object-store put failure.");
        using var buffer = new MemoryStream();
        await data.CopyToAsync(buffer, ct);
        _objects[key] = new StoredObject(buffer.ToArray(), contentType);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        ObjectKeyRules.EnsureSafe(key);
        ct.ThrowIfCancellationRequested();
        if (FailReadWhen?.Invoke(key) == true)
            throw new IOException("Injected object-store read failure.");
        if (!_objects.TryGetValue(key, out var value))
            throw new ObjectStoreNotFoundException(key);
        return Task.FromResult<Stream>(new MemoryStream(value.Bytes, writable: false));
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        ObjectKeyRules.EnsureSafe(key);
        ct.ThrowIfCancellationRequested();
        if (FailReadWhen?.Invoke(key) == true)
            throw new IOException("Injected object-store read failure.");
        return Task.FromResult(_objects.ContainsKey(key));
    }

    public sealed record StoredObject(byte[] Bytes, string ContentType);
}
