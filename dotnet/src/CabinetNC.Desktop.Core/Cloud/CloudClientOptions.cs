namespace CabinetNC.Desktop.Core.Cloud;

/// <summary>Timeouts and polling rules from the design spec (§13): short requests, async jobs, bounded waits.</summary>
public sealed class CloudClientOptions
{
    public required Uri BaseAddress { get; init; }

    /// <summary>Tenant slug sent with login; the JWT's tenant id always comes from the server.</summary>
    public required string Tenant { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan PollInitial { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan PollMax { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Total wall time a job may take before the client gives up (the job itself keeps running server-side).</summary>
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long transport failures are retried before the operation is reported as unavailable.</summary>
    public TimeSpan NetworkGrace { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Refresh the access token when it has less than this left.</summary>
    public TimeSpan RefreshLeadTime { get; init; } = TimeSpan.FromSeconds(60);
}
