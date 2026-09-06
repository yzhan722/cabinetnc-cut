namespace CabinetNC.Desktop.Core.Cloud;

/// <summary>Where the nest is computed. Chosen explicitly by the operator; never switched behind their back.</summary>
public enum ComputeMode
{
    /// <summary>In-process / local worker. Kept in the PoC for A/B comparison.</summary>
    Local,

    /// <summary>Submitted to the intranet cloud API and executed by the cloud worker.</summary>
    Intranet,
}
