using System.Diagnostics;

namespace CabinetNC.Cloud.Infrastructure.Tests;

/// <summary>
/// Locking, idempotency and byte round-trips must be proven against real services, never fakes.
/// Each suite runs against an externally provided service when its environment variable is set,
/// otherwise against a throwaway container via Testcontainers. When neither is possible the tests
/// are SKIPPED with the reason — a missing service is never a PASS.
/// </summary>
public static class DockerProbe
{
    static readonly Lazy<(bool Available, string Reason)> Probe = new(ProbeCore);

    public static bool IsAvailable => Probe.Value.Available;
    public static string Reason => Probe.Value.Reason;

    static (bool, string) ProbeCore()
    {
        var hasEndpoint = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST"))
                          || (OperatingSystem.IsWindows() && Directory.GetFiles(@"\\.\pipe\").Any(p => p.EndsWith("docker_engine", StringComparison.OrdinalIgnoreCase)))
                          || (!OperatingSystem.IsWindows() && File.Exists("/var/run/docker.sock"));
        if (!hasEndpoint)
            return (false, "start Docker (no docker endpoint found)");

        try
        {
            using var proc = Process.Start(new ProcessStartInfo("docker", "info --format {{.OSType}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            if (!proc.WaitForExit(TimeSpan.FromSeconds(15)))
            {
                proc.Kill(entireProcessTree: true);
                return (false, "`docker info` timed out");
            }
            var osType = proc.StandardOutput.ReadToEnd().Trim();
            if (proc.ExitCode != 0)
                return (false, $"`docker info` failed: {proc.StandardError.ReadToEnd().Trim()}");
            if (!string.Equals(osType, "linux", StringComparison.OrdinalIgnoreCase))
                return (false, $"Docker engine runs {osType} containers; Linux containers are required");
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, $"docker CLI not usable ({ex.GetType().Name}: {ex.Message})");
        }
    }
}

public static class PostgresAvailability
{
    public const string ConnectionStringVariable = "CABINETNC_TEST_PG";

    public static bool IsAvailable =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringVariable)) || DockerProbe.IsAvailable;

    public static string Reason => $"PostgreSQL unavailable: set {ConnectionStringVariable} or {DockerProbe.Reason}.";
}

public static class MinioAvailability
{
    public const string EndpointVariable = "CABINETNC_TEST_MINIO_ENDPOINT";
    public const string AccessKeyVariable = "CABINETNC_TEST_MINIO_ACCESS_KEY";
    public const string SecretKeyVariable = "CABINETNC_TEST_MINIO_SECRET_KEY";

    public static bool IsAvailable =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EndpointVariable)) || DockerProbe.IsAvailable;

    public static string Reason => $"MinIO unavailable: set {EndpointVariable}/{AccessKeyVariable}/{SecretKeyVariable} or {DockerProbe.Reason}.";
}

/// <summary>Fact that is skipped, with the reason, when no real PostgreSQL can be reached.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!PostgresAvailability.IsAvailable)
            Skip = PostgresAvailability.Reason;
    }
}

/// <summary>Fact that is skipped, with the reason, when no real MinIO can be reached.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class MinioFactAttribute : FactAttribute
{
    public MinioFactAttribute()
    {
        if (!MinioAvailability.IsAvailable)
            Skip = MinioAvailability.Reason;
    }
}
