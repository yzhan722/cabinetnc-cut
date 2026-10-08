using CabinetNC.Desktop.Core;
using CabinetNC.Domain.Manufacturing;

namespace CabinetNC.Desktop.Core.Tests;

public class NcSimTimelineTests
{
    const string Program = """
        N1 G90
        N2 M6 T2
        N3 M3 S14500
        N4 G0 X0.0000 Y0.0000 Z30.0000
        N5 G1 Z0.5000 F1000.0
        N6 G1 X100.0000 F12000.0
        N7 G1 Y50.0000
        N8 G0 Z30.0000
        N9 M30
        """;

    static NcSimTimeline Load() => new(OsaiTroyParser.Replay(Program).Strokes);

    [Fact]
    public void Starts_are_cumulative_and_total_matches_the_simulator()
    {
        var t = Load();
        Assert.Equal(t.Strokes.Count, t.Starts.Count);
        Assert.Equal(0, t.Starts[0]);
        for (var i = 1; i < t.Starts.Count; i++)
            Assert.True(t.Starts[i] >= t.Starts[i - 1]);
        Assert.Equal(NcCutSim.TotalSec(t.Strokes), t.TotalSec, 9);
    }

    [Fact]
    public void Step_forward_visits_every_distinct_stroke_start_then_the_end()
    {
        var t = Load();
        var distinct = t.Starts.Distinct().Skip(1).ToList();
        var time = 0d;
        var visited = new List<double>();
        for (var i = 0; i < distinct.Count + 2; i++)
        {
            time = t.StepForward(time);
            visited.Add(time);
        }
        Assert.Equal(distinct, visited.Take(distinct.Count));
        Assert.Equal(t.TotalSec, visited[distinct.Count]);
        Assert.Equal(t.TotalSec, visited[^1]);
    }

    [Fact]
    public void Step_back_goes_to_stroke_start_then_previous_stroke()
    {
        var t = Load();
        var starts = t.Starts.Distinct().ToList();
        var mid = starts[2] + 0.4 * (starts[3] - starts[2]);
        Assert.Equal(starts[2], t.StepBack(mid), 9);
        Assert.Equal(starts[1], t.StepBack(starts[2]), 9);
        Assert.Equal(0, t.StepBack(0));
        Assert.Equal(0, t.StepBack(starts[1]));
    }

    [Fact]
    public void Index_at_a_boundary_is_the_stroke_that_starts_there()
    {
        var t = Load();
        Assert.Equal(0, t.IndexAt(0));
        Assert.Equal(1, t.IndexAt(t.Starts[1]));
        Assert.Equal(t.Strokes.Count - 1, t.IndexAt(t.TotalSec + 5));
    }

    [Fact]
    public void Clicking_a_code_line_finds_the_first_motion_at_or_after_it()
    {
        var t = Load();
        // Line 5 (0-based) is "N6 G1 X100" → its stroke; line 1 (M6) has no motion → the first motion after it.
        var strokeForN6 = t.StrokeForLine(5);
        Assert.True(strokeForN6 >= 0);
        Assert.Equal(5, t.Strokes[strokeForN6].LineIndex);
        var strokeAfterM6 = t.StrokeForLine(1);
        Assert.Equal(3, t.Strokes[strokeAfterM6].LineIndex);
        Assert.Equal(-1, t.StrokeForLine(99));
    }

    [Fact]
    public void Empty_timeline_is_safe()
    {
        var t = NcSimTimeline.Empty;
        Assert.True(t.IsEmpty);
        Assert.Equal(0, t.StepForward(5));
        Assert.Equal(0, t.StepBack(5));
        Assert.Equal(-1, t.IndexAt(0));
        Assert.Equal(0, t.StartOf(3));
    }
}
