namespace CabinetNC.Domain.Tests;

using CabinetNC.Domain.Nesting;

public class NfpGeometryTests
{
    [Fact]
    public void Rectangle_is_convex_and_stays_one_piece()
    {
        var rect = NfpGeometry.ToPath([(0, 0), (10, 0), (10, 8), (0, 8)]);
        Assert.True(NfpGeometry.IsConvex(rect));
        var pieces = NfpGeometry.DecomposeConvex(rect);
        Assert.Single(pieces);
        Assert.True(NfpGeometry.IsConvex(pieces[0]));
    }

    [Fact]
    public void L_shape_decomposes_into_two_convex_pieces()
    {
        var l = NfpGeometry.ToPath(LPts());
        Assert.False(NfpGeometry.IsConvex(l));
        var pieces = NfpGeometry.DecomposeConvex(l);
        Assert.True(pieces.Count is 2 or 3, $"expected 2–3 pieces, got {pieces.Count}");
        Assert.All(pieces, p => Assert.True(NfpGeometry.IsConvex(p)));
    }

    [Fact]
    public void Concave_nfp_still_forbids_overlapping_L_origins()
    {
        var l = NfpGeometry.ToPath(LPts());
        var nfp = NfpGeometry.ComputeNfp(l, l);
        Assert.NotEmpty(nfp);
        Assert.True(NfpGeometry.ReferenceForbidden(0, 0, nfp));
        Assert.True(NfpGeometry.ReferenceForbidden(20, 20, nfp));
        Assert.False(NfpGeometry.ReferenceForbidden(190, 0, nfp));
        Assert.False(NfpGeometry.ReferenceForbidden(0, 190, nfp));
    }

    [Fact]
    public void Candidate_references_include_edge_slide_samples()
    {
        var a = NfpGeometry.ToPath([(0, 0), (40, 0), (40, 40), (0, 40)]);
        var b = NfpGeometry.ToPath([(0, 0), (20, 0), (20, 20), (0, 20)]);
        var nfp = NfpGeometry.ComputeNfp(a, b);
        var pts = NfpGeometry.CandidateReferences(nfp, 0, 0, 80).ToList();
        Assert.True(pts.Count >= 8, $"expected slide samples, got {pts.Count}");
    }

    static (double X, double Y)[] LPts() =>
    [
        (0, 0), (180, 0), (180, 60),
        (60, 60), (60, 180), (0, 180),
    ];
}
