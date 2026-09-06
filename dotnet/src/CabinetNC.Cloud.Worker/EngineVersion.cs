using System.Reflection;
using CabinetNC.Compute.Core.Nesting;

namespace CabinetNC.Cloud.Worker;

/// <summary>
/// Recorded on every job so a result can always be traced to the exact compute build that produced
/// it. The SDK appends the git revision to the informational version when built inside the repo.
/// </summary>
public static class EngineVersion
{
    public static string Current { get; } = Compute();

    static string Compute()
    {
        var assembly = typeof(NestingRunner).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "unknown";
        return $"CabinetNC.Compute.Core/{version}";
    }
}
