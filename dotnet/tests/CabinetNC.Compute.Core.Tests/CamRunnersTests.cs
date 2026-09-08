using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.NestContract;
using CabinetNC.Compute.Core.Cam;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Compute.Core.Tests;

/// <summary>
/// Commercial gate G3: operations and NC computed on the server from the wire contract must equal what
/// the Desktop computes locally from the Domain objects ÃƒÂ¢Ã¢â€š?same planner, same emitter, same inputs.
/// </summary>
public class CamRunnersTests
{
    static Panel Door() => new()
    {
        PanelId = "door-1",
        Material = "MDF",
        ThicknessMm = 18,
        Outline = new Outline
        {
            Points = [new(0, 0), new(600, 0), new(600, 400), new(0, 400)],
            Segments =
            [
                new CadSegment(CadSegment.Line, new(0, 0), new(600, 0), null, 0, false),
                new CadSegment(CadSegment.Line, new(600, 0), new(600, 400), null, 0, false),
                new CadSegment(CadSegment.Line, new(600, 400), new(0, 400), null, 0, false),
                new CadSegment(CadSegment.Line, new(0, 400), new(0, 0), null, 0, false),
            ],
        },
        Features =
        [
            new PanelFeature { FeatureId = "hinge-1", Kind = "hole", FaceId = "A", X = 22, Y = 100, DiameterMm = 35, DepthMm = 12, Purpose = "hinge" },
            new PanelFeature { FeatureId = "hinge-2", Kind = "hole", FaceId = "A", X = 22, Y = 300, DiameterMm = 35, DepthMm = 12, Purpose = "hinge" },
            new PanelFeature { FeatureId = "dowel", Kind = "hole", FaceId = "A", X = 300, Y = 50, DiameterMm = 8, DepthMm = 10 },
            new PanelFeature { FeatureId = "back-groove", Kind = "groove", FaceId = "A", X = 0, Y = 0, WidthMm = 4, DepthMm = 6, Path = [new(20, 380), new(580, 380)] },
            new PanelFeature
            {
                FeatureId = "led-pocket", Kind = "pocket", FaceId = "A", DepthMm = 5, WidthMm = 0,
                Profile = [new(100, 150), new(500, 150), new(500, 250), new(100, 250)],
                Holes = [[new(280, 190), new(320, 190), new(320, 210), new(280, 210)]],
            },
            new PanelFeature { FeatureId = "vent", Kind = "cutout", FaceId = "THROUGH", Through = true, Path = [new(200, 40), new(400, 40), new(400, 90), new(200, 90)] },
        ],
    };

    static Panel Shelf() => new()
    {
        PanelId = "shelf-1",
        Material = "MDF",
        ThicknessMm = 18,
        Orientation = new WorkpieceOrientation { MillingFace = "B" },   // flattened into CamPanelDto.Side
        Outline = new Outline { Points = [new(0, 0), new(500, 0), new(500, 300), new(250, 340), new(0, 300)] },
        Features =
        [
            new PanelFeature { FeatureId = "s-drill", Kind = "hole", FaceId = "B", X = 50, Y = 150, DiameterMm = 5, DepthMm = 12 },
            new PanelFeature { FeatureId = "s-groove", Kind = "groove", FaceId = "B", WidthMm = 6, DepthMm = 8, Path = [new(10, 20), new(490, 20)], GroupId = "g1" },
        ],
    };

    static IReadOnlyList<NestPlacement> Placements() =>
    [
        new() { PanelId = "door-1", SheetIndex = 0, OffsetX = 15, OffsetY = 15, RotationDeg = 0 },
        new() { PanelId = "shelf-1", SheetIndex = 0, OffsetX = 640, OffsetY = 15, RotationDeg = 90 },
    ];

    static OperationsOptionsDto Options(double tool = 8) => new(true, true, true, 120, 12, tool);

    static SubmitOperationsJobRequest OperationsRequest(double tool = 8) =>
        new(new[] { Door(), Shelf() }.Select(CamContractMapper.FromPanel).ToList(), Placements().Select(CamContractMapper.FromPlacement).ToList(), Options(tool));

    static T Wire<T>(T value) => CloudJson.Deserialize<T>(CloudJson.Serialize(value));

    [Theory]
    [InlineData(8.0)]
    [InlineData(0.0)]
    public void Server_operations_equal_the_local_pipeline(double tool)
    {
        var local = OperationsRunner.RunPipeline(new[] { Door(), Shelf() }, Placements(), Options(tool));
        var localWire = CloudJson.Serialize(CamContractMapper.FromOps(local));

        var request = OperationsRequest(tool);
        Assert.Null(CamRequestRules.Validate(request));
        var server = new OperationsRunner().Run(Wire(request));

        Assert.Equal(localWire, CloudJson.Serialize(Wire(server)));
        // ÃƒËœ35 hinge cups exceed the drill threshold (12 mm) and become clearance ops; the ÃƒËœ8 dowel and ÃƒËœ5 shelf hole stay drills.
        Assert.Equal(2, server.DrillCount);
        Assert.Contains(server.Ops, o => o.FeatureId == "hinge-1" && o.Op != "drill");
        Assert.True(server.GrooveCount >= 2);
        Assert.True(server.ContourCount >= 2, "each panel gets a contour");
        Assert.All(server.Ops, o => Assert.True(o.Placed));
        Assert.Contains(server.Ops, o => o.SheetX is not null && o.RotationDeg == 90);
        Assert.All(server.Ops.Where(o => o.PanelId == "shelf-1"), o => Assert.Equal("B", o.Side));
    }

    [Fact]
    public void Server_nc_equals_local_nc_byte_for_byte()
    {
        var ops = OperationsRunner.RunPipeline(new[] { Door(), Shelf() }, Placements(), Options());
        var machine = MachineCatalog.All[0];
        var recipe = new PostRecipe
        {
            ProfileFirstRamp45 = true,
            HomeXyAtEnd = false,
            Bridges = [new ProfileBridge { Id = "b1", PanelId = "door-1", SheetIndex = 0, ArcLengthMm = 120, X = 315, Y = 15, WidthMm = 8 }],
        };
        var localNc = NcEmitter.OpsToNc(ops, machine, recipe: recipe);

        var request = new SubmitPostJobRequest(ops.Select(CamContractMapper.FromOp).ToList(), CamContractMapper.FromMachine(machine), CamContractMapper.FromRecipe(recipe));
        Assert.Null(CamRequestRules.Validate(request));
        var server = new PostProcessorRunner().Run(Wire(request));

        Assert.Equal(localNc, server.NcText);
        Assert.Equal(localNc.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, server.LineCount);
        Assert.Equal(machine.Id, server.MachineId);
        Assert.True(server.LineCount > 20);
        Assert.NotEqual(NcEmitter.OpsToNc(ops, machine, recipe: PostRecipe.TroyDefault()), server.NcText);   // the recipe actually matters
    }

    [Fact]
    public void Feature_operation_machine_and_recipe_projections_round_trip()
    {
        var door = Door();
        var back = CamContractMapper.ToPanel(Wire(CamContractMapper.FromPanel(door)));
        Assert.Equal(door.Outline.Points, back.Outline.Points);
        Assert.Equal(door.Outline.Segments!.Count, back.Outline.Segments!.Count);
        Assert.Equal(door.Features.Count, back.Features.Count);
        var pocket = back.Features.Single(f => f.FeatureId == "led-pocket");
        Assert.Equal(door.Features.Single(f => f.FeatureId == "led-pocket").Profile, pocket.Profile);
        Assert.Single(pocket.Holes!);
        Assert.True(PanelEdit.IsPocket(pocket));
        Assert.True(PanelEdit.IsCutout(back.Features.Single(f => f.FeatureId == "vent")));

        var ops = OperationsRunner.RunPipeline(new[] { door }, Placements().Take(1).ToList(), Options());
        foreach (var op in ops)
        {
            var roundTripped = CamContractMapper.ToOp(Wire(CamContractMapper.FromOp(op)));
            Assert.Equal(CloudJson.Serialize(CamContractMapper.FromOp(op)), CloudJson.Serialize(CamContractMapper.FromOp(roundTripped)));
        }

        var machine = MachineCatalog.All[0];
        var machineBack = CamContractMapper.ToMachine(Wire(CamContractMapper.FromMachine(machine)));
        Assert.Equal(CloudJson.Serialize(CamContractMapper.FromMachine(machine)), CloudJson.Serialize(CamContractMapper.FromMachine(machineBack)));

        var recipe = new PostRecipe { TongueFeed = 1234, Bridges = [new ProfileBridge { Id = "b", PanelId = "door-1", ArcLengthMm = 1, WidthMm = 8 }] };
        var recipeBack = CamContractMapper.ToRecipe(Wire(CamContractMapper.FromRecipe(recipe)));
        Assert.Equal(1234, recipeBack.TongueFeed);
        Assert.Single(recipeBack.Bridges);
    }

    [Fact]
    public void Requests_are_validated_before_any_algorithm_runs()
    {
        Assert.Contains("At least one panel", CamRequestRules.Validate(new SubmitOperationsJobRequest([], [], Options())));
        var unknownPlacement = OperationsRequest() with { Placements = [new NestPlacementDto("ghost", 0, 0, 0, 0)] };
        Assert.Contains("unknown panel", CamRequestRules.Validate(unknownPlacement));
        var badTool = OperationsRequest() with { Options = Options(double.NaN) };
        Assert.Contains("contourToolDiameterMm", CamRequestRules.Validate(badTool));
        Assert.Contains("machine profile", CamRequestRules.Validate(new SubmitPostJobRequest([], new MachineProfileDto("", "", "g", "M2", 5, 1, 1, 1, 6, 18, 0, 0, true, true, true, null), CamContractMapper.FromRecipe(PostRecipe.TroyDefault()))));
    }
}
