namespace CabinetNC.Cloud.Worker;

public sealed record WorkerOptions
{
    public const string WorkerIdVariable = "CABINETNC_WORKER_ID";
    public const string LeaseSecondsVariable = "CABINETNC_WORKER_LEASE_SECONDS";
    public const string PollSecondsVariable = "CABINETNC_WORKER_POLL_SECONDS";

    /// <summary>Fencing token written into <c>LockedBy</c>; must differ between concurrently running workers.</summary>
    public required string WorkerId { get; init; }

    /// <summary>How long a claim stays exclusive without a result. A crashed worker's job becomes reclaimable after this.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan ErrorBackoff { get; init; } = TimeSpan.FromSeconds(5);

    public static WorkerOptions FromEnvironment()
    {
        var workerId = Environment.GetEnvironmentVariable(WorkerIdVariable);
        if (string.IsNullOrWhiteSpace(workerId))
            workerId = $"{Environment.MachineName}:{Environment.ProcessId}";
        if (workerId.Length > 200)
            throw new InvalidOperationException($"{WorkerIdVariable} must be at most 200 characters.");

        return new WorkerOptions
        {
            WorkerId = workerId,
            LeaseDuration = ReadSeconds(LeaseSecondsVariable, defaultSeconds: 300, min: 10, max: 3600),
            PollInterval = ReadSeconds(PollSecondsVariable, defaultSeconds: 1, min: 0.1, max: 60),
        };
    }

    static TimeSpan ReadSeconds(string variable, double defaultSeconds, double min, double max)
    {
        var text = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(text))
            return TimeSpan.FromSeconds(defaultSeconds);
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            || seconds < min
            || seconds > max)
        {
            throw new InvalidOperationException($"{variable} must be a number of seconds between {min} and {max}.");
        }
        return TimeSpan.FromSeconds(seconds);
    }
}
