using CabinetNC.Cloud.Contracts;

namespace CabinetNC.Compute.Core.Nesting;

/// <summary>
/// Transport-neutral nesting. The Named Pipe gRPC worker and the intranet cloud worker are both thin
/// adapters over this one implementation, so Local/Server parity is "same runner, same output".
/// </summary>
public interface INestingRunner
{
    /// <summary>v1: rectangular parts, one stock size (the original PoC contract).</summary>
    NestingOutput Run(NestingInput input);

    /// <summary>
    /// v2: the Desktop's full nest request — true-shape outlines, cutouts, stock queue, settings and
    /// engine preference — executed through the same <c>NestEngineRouter</c> the Desktop uses locally.
    /// </summary>
    NestJobResultPayloadV2 RunV2(SubmitNestJobRequestV2 request, CancellationToken ct = default);
}
