using System.Diagnostics;

namespace CabinetNC.Cloud.Infrastructure.Tests;

/// <summary>
/// Locking and idempotency must be proven against a real PostgreSQL, never EF InMemory. Tests run
/// against <c>CABINETNC_TEST_PG</c> when set, otherwise a throwaway container via Testcontainers.
/// When neither is possible the tests are SKIPPED with the reason — a missing database is never a PASS.
/// </summary>
public static class PostgresAvailability
{
    public const string ConnectionStringVariable = "CABINETNC_TEST_PG";

    static readonly Lazy<(bool Available, string Reason)> Probe = new(ProbeCore);

    public static bool IsAvailable => Probe.Value.Available;
    public static string Reason => Probe.Value.Reason;

    static (bool, string) ProbeCore()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringVariable)))
            return (true, "");

        var hasEndpoint = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST"))
                          || (OperatingSystem.IsWindows() && Directory.GetFiles(@"\\.\pipe\").Any(p => p.EndsWith("docker_engine", StringComparison.OrdinalIgnoreCase)))
                          || (!OperatingSystem.IsWindows() && File.Exists("/var/run/docker.sock"));
        if (!hasEndpoint)
            return (false, $"PostgreSQL unavailable: set {ConnectionStringVariable} or start Docker (no docker endpoint found).");

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
                return (false, "PostgreSQL unavailable: `docker info` timed out.");
            }
            var osType = proc.StandardOutput.ReadToEnd().Trim();
            if (proc.ExitCode != 0)
                return (false, $"PostgreSQL unavailable: `docker info` failed: {proc.StandardError.ReadToEnd().Trim()}");
            if (!string.Equals(osType, "linux", StringComparison.OrdinalIgnoreCase))
                return (false, $"PostgreSQL unavailable: Docker engine runs {osType} containers; postgres needs Linux containers.");
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, $"PostgreSQL unavailable: docker CLI not usable ({ex.GetType().Name}: {ex.Message}).");
        }
    }
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
