using CabinetNC.Desktop.Core;
using CabinetNC.Infrastructure.Projects;

namespace CabinetNC.Desktop.Core.Tests;

public class WorkFingerprintTests
{
    static ProjectSessionState Session(string stage = "nest", int activeSheet = 0) => new()
    {
        Stage = stage,
        ActiveNestSheet = activeSheet,
        ShowNest = true,
        LockedPanelIds = ["A"],
        Cam = new ProjectCamSettings { ProfFirstFeed = 12000 },
    };

    static readonly PlacementKey[] Places = [new("A", 0, 15, 15, 0), new("B", 0, 300, 15, 90)];

    [Fact]
    public void View_state_does_not_change_the_fingerprint()
    {
        var a = WorkFingerprint.Compute("{pkg}", Places, Session("nest", 0), "job", "osai");
        var b = WorkFingerprint.Compute("{pkg}", Places, Session("out", 3), "job", "osai");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Derived_ops_do_not_change_the_fingerprint()
    {
        var s = Session();
        s.Ops = [new CutOpDto { Op = "contour", PanelId = "A" }];
        Assert.Equal(
            WorkFingerprint.Compute("{pkg}", Places, Session(), "job", "osai"),
            WorkFingerprint.Compute("{pkg}", Places, s, "job", "osai"));
    }

    [Fact]
    public void Real_work_changes_the_fingerprint()
    {
        var baseline = WorkFingerprint.Compute("{pkg}", Places, Session(), "job", "osai");
        Assert.NotEqual(baseline, WorkFingerprint.Compute("{pkg-edited}", Places, Session(), "job", "osai"));
        Assert.NotEqual(baseline, WorkFingerprint.Compute("{pkg}", [Places[0], new("B", 0, 310, 15, 90)], Session(), "job", "osai"));
        var cam = Session();
        cam.Cam.ProfFirstFeed = 9000;
        Assert.NotEqual(baseline, WorkFingerprint.Compute("{pkg}", Places, cam, "job", "osai"));
        Assert.NotEqual(baseline, WorkFingerprint.Compute("{pkg}", Places, Session(), "renamed", "osai"));
        Assert.NotEqual(baseline, WorkFingerprint.Compute("{pkg}", Places, Session(), "job", "other-machine"));
    }

    [Fact]
    public void Fingerprint_is_stable_and_hex()
    {
        var a = WorkFingerprint.Compute("{pkg}", Places, Session(), "job", "osai");
        var b = WorkFingerprint.Compute("{pkg}", Places, Session(), "job", "osai");
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        Assert.Matches("^[0-9A-F]+$", a);
    }
}
