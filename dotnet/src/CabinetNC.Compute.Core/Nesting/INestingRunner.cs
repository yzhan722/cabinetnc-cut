namespace CabinetNC.Compute.Core.Nesting;

/// <summary>
/// Transport-neutral rectangular nesting. The Named Pipe gRPC worker and the intranet cloud
/// worker are both thin adapters over this one implementation, so Local/Server parity is
/// "same runner, same output".
/// </summary>
public interface INestingRunner
{
    NestingOutput Run(NestingInput input);
}
