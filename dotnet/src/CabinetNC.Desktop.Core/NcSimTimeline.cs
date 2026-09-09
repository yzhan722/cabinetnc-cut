using CabinetNC.Domain.Manufacturing;

namespace CabinetNC.Desktop.Core;

/// <summary>
/// Time axis of a G-code replay: where each stroke starts, and the step / seek rules the
/// transport buttons and the code-view click use.
/// </summary>
public sealed class NcSimTimeline
{
    public IReadOnlyList<ToolStroke> Strokes { get; }
    public IReadOnlyList<double> Starts { get; }
    public double TotalSec { get; }

    public NcSimTimeline(IReadOnlyList<ToolStroke> strokes)
    {
        Strokes = strokes;
        var starts = new List<double>(strokes.Count);
        var acc = 0d;
        foreach (var s in strokes)
        {
            starts.Add(acc);
            acc += NcCutSim.DurationSec(s);
        }
        Starts = starts;
        TotalSec = acc;
    }

    public static NcSimTimeline Empty { get; } = new([]);

    public bool IsEmpty => Strokes.Count == 0;

    public double StartOf(int strokeIndex) =>
        Starts.Count == 0 ? 0 : Starts[Math.Clamp(strokeIndex, 0, Starts.Count - 1)];

    const double Eps = 1e-6;

    /// <summary>Stroke whose start is at or before <paramref name="timeSec"/> (last one on ties).</summary>
    public int IndexAt(double timeSec)
    {
        if (IsEmpty) return -1;
        var idx = 0;
        for (var i = 0; i < Starts.Count; i++)
        {
            if (Starts[i] <= timeSec + Eps) idx = i;
            else break;
        }
        return idx;
    }

    /// <summary>Previous distinct stroke start, or 0. Zero-duration strokes never trap the step.</summary>
    public double StepBack(double timeSec)
    {
        if (IsEmpty) return 0;
        for (var i = Starts.Count - 1; i >= 0; i--)
        {
            if (Starts[i] < timeSec - Eps)
                return Starts[i];
        }
        return 0;
    }

    /// <summary>Next distinct stroke start, or the end of the program.</summary>
    public double StepForward(double timeSec)
    {
        if (IsEmpty) return 0;
        foreach (var s in Starts)
        {
            if (s > timeSec + Eps)
                return s;
        }
        return TotalSec;
    }

    /// <summary>
    /// First stroke produced by source line <paramref name="line"/> or a later one; -1 when the
    /// program has no motion at or after that line (the caller then lands on the last stroke).
    /// </summary>
    public int StrokeForLine(int line)
    {
        for (var i = 0; i < Strokes.Count; i++)
        {
            if (Strokes[i].LineIndex >= line)
                return i;
        }
        return -1;
    }
}
