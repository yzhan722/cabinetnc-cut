namespace CabinetNC.Domain.Manufacturing;

using System.Runtime.CompilerServices;
using CabinetNC.Domain.Machines;

public sealed class GenericMmPostProcessor : IPostProcessor
{
    public string Id => "generic_mm";
    public string Emit(IEnumerable<CutOp> ops, MachineProfile profile, PostRecipe? recipe = null) =>
        NcEmitter.OpsToNc(ops, PostProcessorCatalog.CloneProfile(profile, "generic", profile.ProgramEnd), recipe: recipe);
}

public sealed class FanucLikePostProcessor : IPostProcessor
{
    public string Id => "fanuc_like";
    public string Emit(IEnumerable<CutOp> ops, MachineProfile profile, PostRecipe? recipe = null) =>
        NcEmitter.OpsToNc(ops, PostProcessorCatalog.CloneProfile(profile, "fanuc_like", "M30"), recipe: recipe);
}

/// <summary>Makes the in-process emitters available wherever this assembly is loaded (developer Desktop, worker, tests).</summary>
public static class ComputePostProcessors
{
    [ModuleInitializer]
    public static void Register() =>
        PostProcessorCatalog.Provider ??= profile =>
            profile.Dialect == "fanuc_like" ? new FanucLikePostProcessor() : new GenericMmPostProcessor();
}
