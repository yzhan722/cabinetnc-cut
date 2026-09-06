namespace CabinetNC.Cloud.Infrastructure;

/// <summary>Object-store layout from the design spec §10. Only metadata and hashes live in PostgreSQL.</summary>
public static class ObjectKeys
{
    public static string JobInput(Guid tenantId, Guid jobId) => $"tenant/{tenantId:D}/jobs/{jobId:D}/input.json";
    public static string JobResult(Guid tenantId, Guid jobId) => $"tenant/{tenantId:D}/jobs/{jobId:D}/result.json";
}
