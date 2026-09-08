using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.NestContract;
using CabinetNC.Compute.Core.Nesting;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Compute.Core.Tests;

/// <summary>
/// Commercial gate G2: what the server computes for a v2 request must be exactly what the Desktop
/// computes locally for the same Domain objects — same router, same engines, same size function.
/// </summary>
public class NestingRunnerV2Tests
{
    static Panel Rect(string id, double w, double h, string? grain = null, IReadOnlyList<int>? rotations = null, string material = "MDF", int quantity = 1) => new()
    {
        PanelId = id,
        Material = material,
        ThicknessMm = 18,
        GrainDirection = grain,
        AllowedRotations = rotations,
        Quantity = quantity,
        Outline = new Outline { Points = [new(0, 0), new(w, 0), new(w, h), new(0, h)] },
    };

    static Panel LShape(string id) => new()
    {
        PanelId = id,
        Material = "MDF",
        ThicknessMm = 18,
        Outline = new Outline { Points = [new(0, 0), new(700, 0), new(700, 250), new(300, 250), new(300, 500), new(0, 500)] },
    };

    /// <summary>Host with a through cut-out large enough for a small child (parts-in-part).</summary>
    static Panel Host(string id) => new()
    {
        PanelId = id,
        Material = "MDF",
        ThicknessMm = 18,
        Orientation = new WorkpieceOrientation { GrainDirection = "Y" },
        Outline = new Outline { Points = [new(0, 0), new(900, 0), new(900, 700), new(0, 700)] },
        Features =
        [
            new PanelFeature { FeatureId = "win", Kind = "cutout", Through = true, FaceId = "THROUGH", Path = [new(200, 150), new(700, 150), new(700, 550), new(200, 550)] },
            new PanelFeature { FeatureId = "hinge", Kind = "drill", X = 50, Y = 50, DiameterMm = 35, DepthMm = 12 },
        ],
    };

    static IReadOnlyList<Panel> Panels() =>
    [
        Rect("A", 600, 400),
        Rect("B", 600, 400, grain: "X", rotations: [0, 180]),
        Rect("C", 350, 250, material: "PLY"),
        Rect("D", 1300, 300, quantity: 2),
        Rect("small", 200, 150),
        LShape("L1"),
        Host("H1"),
        Rect("BIG", 3000, 3000),
    ];

    static IReadOnlyList<NestSheetSpec> Sheets() =>
    [
        new()
        {
            WidthMm = 1220, LengthMm = 2440, BorderMm = 15, SpacingMm = 12, AllowRotation = true, AllowPartsInPart = true,
            Label = "MDF full", Material = "MDF", ThicknessMm = 18, SheetGrain = SheetGrainKind.AlongLength,
            Blocked = [new NestBlockedRect { MinX = 0, MinY = 0, MaxX = 150, MaxY = 150 }],
        },
        new() { WidthMm = 800, LengthMm = 1200, BorderMm = 10, InsetLeftMm = 40, SpacingMm = 8, Label = "remnant", Material = "MDF", ThicknessMm = 18 },
        new() { WidthMm = 1250, LengthMm = 2500, BorderMm = 15, SpacingMm = 12, Label = "PLY", Material = "PLY", ThicknessMm = 18 },
    ];

    static NestSettings Settings() => new()
    {
        MarginMm = 15,
        ClearanceMm = 12,
        AllowRotation = true,
        GrainLock = true,
        PreferLockedPlacements = true,
    };

    static IEnumerable<(string, int, double, double, double)> Flat(NestResult r) =>
        r.Placements.Select(p => (p.PanelId, p.SheetIndex, Math.Round(p.OffsetX, 6), Math.Round(p.OffsetY, 6), Math.Round(p.RotationDeg, 6)));

    [Theory]
    [InlineData("blf")]
    [InlineData("nfp")]
    public void Server_v2_equals_local_router_for_the_same_inputs(string preference)
    {
        var panels = Panels();
        var sheets = Sheets();
        var settings = Settings();
        var timeout = TimeSpan.FromSeconds(25);

        var (local, localLog) = NestingRunner.RunRouter(panels, settings, sheets, preference, timeout);

        var request = NestContractV2Mapper.ToRequest(panels, settings, sheets, preference, timeout);
        Assert.Null(NestRequestV2Rules.Validate(request));
        var wire = CloudJson.Deserialize<SubmitNestJobRequestV2>(CloudJson.Serialize(request));   // exactly what the server receives
        var payload = new NestingRunner().RunV2(wire);
        var (server, serverLog) = NestContractV2Mapper.ToResult(CloudJson.Deserialize<NestJobResultPayloadV2>(CloudJson.Serialize(payload)));

        Assert.Equal(local.Engine, server.Engine);
        Assert.Equal(localLog.SelectedEngine, serverLog.SelectedEngine);
        Assert.Equal(localLog.FallbackReason, serverLog.FallbackReason);
        Assert.Equal(local.SheetCount, server.SheetCount);
        Assert.Equal(Flat(local), Flat(server));
        Assert.Equal(local.Unplaced, server.Unplaced);
        Assert.Equal(local.UnplacedReasons.Select(u => (u.PanelId, u.Code)), server.UnplacedReasons.Select(u => (u.PanelId, u.Code)));
        Assert.Equal(local.GroupReports.Select(g => (g.Key, g.PartCount, g.PlacedCount, g.SheetCount, g.LocalSheetStart, Math.Round(g.UtilizationPct, 6))),
                     server.GroupReports.Select(g => (g.Key, g.PartCount, g.PlacedCount, g.SheetCount, g.LocalSheetStart, Math.Round(g.UtilizationPct, 6))));
        Assert.Equal(local.SheetsUsed.Select(s => (s.Label, s.WidthMm, s.LengthMm)), server.SheetsUsed.Select(s => (s.Label, s.WidthMm, s.LengthMm)));
        Assert.Equal(local.PartInPartSlots.Select(s => (s.HostPanelId, s.ChildPanelId, s.FeatureId, s.SheetIndex)),
                     server.PartInPartSlots.Select(s => (s.HostPanelId, s.ChildPanelId, s.FeatureId, s.SheetIndex)));

        // Sanity: the fixture really exercises the interesting paths.
        Assert.Contains("BIG", server.Unplaced);
        Assert.True(server.Placements.Count >= 6, "most parts must be placed");
    }

    [Fact]
    public void Panel_projection_keeps_exactly_what_the_engines_read()
    {
        var host = Host("H1");
        var dto = NestContractV2Mapper.FromPanel(host);
        var back = NestContractV2Mapper.ToPanel(dto);

        Assert.Equal(host.PanelId, back.PanelId);
        Assert.Equal(host.Outline.Points, back.Outline.Points);
        Assert.Equal("Y", back.GrainDirection);                     // flattened from Orientation
        Assert.Equal(host.MayRotate90, back.MayRotate90);
        Assert.Equal(Settings().PanelMayRotate90(host), Settings().PanelMayRotate90(back));
        var cutout = Assert.Single(back.Features);                  // drills are not nesting inputs
        Assert.Equal("win", cutout.FeatureId);
        Assert.True(PanelEdit.IsCutout(cutout));
        Assert.Equal(host.Features[0].Path, cutout.Path);

        var grained = Rect("B", 600, 400, grain: "X", rotations: [0, 180]);
        var grainedBack = NestContractV2Mapper.ToPanel(NestContractV2Mapper.FromPanel(grained));
        Assert.Equal([0, 180], grainedBack.AllowedRotations);
        Assert.False(grainedBack.MayRotate90);
    }

    [Fact]
    public void Sheet_and_settings_projection_round_trips()
    {
        var sheet = Sheets()[0];
        var back = NestContractV2Mapper.ToSheet(NestContractV2Mapper.FromSheet(sheet));
        Assert.Equal((sheet.WidthMm, sheet.LengthMm, sheet.BorderMm, sheet.SpacingMm, sheet.AllowRotation, sheet.AllowPartsInPart, sheet.Label, sheet.Material, sheet.ThicknessMm, sheet.SheetGrain),
                     (back.WidthMm, back.LengthMm, back.BorderMm, back.SpacingMm, back.AllowRotation, back.AllowPartsInPart, back.Label, back.Material, back.ThicknessMm, back.SheetGrain));
        Assert.Equal(sheet.Insets(), back.Insets());
        Assert.Equal(sheet.Blocked.Select(b => (b.MinX, b.MinY, b.MaxX, b.MaxY)), back.Blocked.Select(b => (b.MinX, b.MinY, b.MaxX, b.MaxY)));

        var settings = new NestSettings { MarginMm = 10, ClearanceMm = 6, AllowRotation = false, AllowedRotations = [0, 90], RotationStepDeg = 45, GrainLock = false, SheetGrain = SheetGrainKind.AlongWidth, MirrorPermission = true, PreferLockedPlacements = false };
        var settingsBack = NestContractV2Mapper.ToSettings(NestContractV2Mapper.FromSettings(settings));
        Assert.Equal((settings.MarginMm, settings.ClearanceMm, settings.AllowRotation, settings.RotationStepDeg, settings.GrainLock, settings.SheetGrain, settings.MirrorPermission, settings.PreferLockedPlacements),
                     (settingsBack.MarginMm, settingsBack.ClearanceMm, settingsBack.AllowRotation, settingsBack.RotationStepDeg, settingsBack.GrainLock, settingsBack.SheetGrain, settingsBack.MirrorPermission, settingsBack.PreferLockedPlacements));
        Assert.Equal(settings.AllowedRotations, settingsBack.AllowedRotations);
    }

    [Fact]
    public void V2_request_is_canonical_and_deterministic()
    {
        var a = CloudJson.Serialize(NestContractV2Mapper.ToRequest(Panels(), Settings(), Sheets(), "nfp", TimeSpan.FromSeconds(25)));
        var b = CloudJson.Serialize(NestContractV2Mapper.ToRequest(Panels(), Settings(), Sheets(), "nfp", TimeSpan.FromSeconds(25)));
        Assert.Equal(a, b);
        Assert.Contains("\"enginePreference\":\"nfp\"", a);
        Assert.Contains("\"sheetGrain\":\"AlongLength\"", a);
    }
}
