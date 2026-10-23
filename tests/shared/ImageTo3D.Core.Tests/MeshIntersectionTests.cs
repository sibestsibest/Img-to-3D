using ImageTo3D.Core.Geometry;
using ImageTo3D.Core.PartDesign;

namespace ImageTo3D.Core.Tests;

/// <summary>Phase 7: triangle-triangle classification, BVH pair search, mesh contact and containment.</summary>
public class MeshIntersectionTests
{
    private const double Tolerance = 0.05;

    private static Vector3D[] T(params double[] c) => [new(c[0], c[1], c[2]), new(c[3], c[4], c[5]), new(c[6], c[7], c[8])];

    private static MeshContactResult Compare(Mesh a, Mesh b, long budget = long.MaxValue) =>
        MeshIntersection.Compare(new TriangleBvh(a), new TriangleBvh(b), Tolerance, MeshTopology.Analyze(a).IsClosed, MeshTopology.Analyze(b).IsClosed, budget);

    private static Mesh Rod(double diameter, double length, char axis, Vector3D center = default) =>
        PrimitiveBuilder.Build(new PrimitiveSpec(PrimitiveKinds.Rod, LengthMm: length, DiameterMm: diameter, Axis: axis, Center: center));

    private static Mesh Tube(double outer, double inner, double length, char axis, Vector3D center = default) =>
        PrimitiveBuilder.Build(new PrimitiveSpec(PrimitiveKinds.Tube, LengthMm: length, OuterDiameterMm: outer, InnerDiameterMm: inner, Axis: axis, Center: center));

    private static Mesh Plate(double length, double width, double thickness, Vector3D center = default) =>
        PrimitiveBuilder.Build(new PrimitiveSpec(PrimitiveKinds.Plate, LengthMm: length, WidthMm: width, ThicknessMm: thickness, Center: center));

    // ---- Triangle pairs ----

    [Fact]
    public void Triangles_CrossingEachOther_AreCrossing()
    {
        var flat = T(-1, 0, -1, 1, 0, -1, 0, 0, 1);
        var upright = T(0, -1, 0, 0, 1, 0.2, 0, 1, -0.2);
        Assert.Equal(TriangleContact.Crossing, MeshIntersection.Classify(flat, upright, Tolerance));
    }

    [Fact]
    public void Triangles_FarApart_AreNone()
    {
        Assert.Equal(TriangleContact.None, MeshIntersection.Classify(T(0, 0, 0, 1, 0, 0, 0, 1, 0), T(0, 0, 5, 1, 0, 5, 0, 1, 5), Tolerance));
    }

    [Fact]
    public void Triangles_CoplanarOverlapping_AreTouching_AndCoplanarApartAreNone()
    {
        var a = T(0, 0, 0, 2, 0, 0, 0, 2, 0);
        Assert.Equal(TriangleContact.Touching, MeshIntersection.Classify(a, T(0.5, 0.5, 0, 3, 0.5, 0, 0.5, 3, 0), Tolerance));
        Assert.Equal(TriangleContact.None, MeshIntersection.Classify(a, T(5, 5, 0, 6, 5, 0, 5, 6, 0), Tolerance));
    }

    [Fact]
    public void Triangles_ParallelWithinTolerance_AreTouching()
    {
        Assert.Equal(TriangleContact.Touching, MeshIntersection.Classify(T(0, 0, 0, 2, 0, 0, 0, 2, 0), T(0, 0, 0.02, 2, 0, 0.02, 0, 2, 0.02), Tolerance));
        Assert.Equal(TriangleContact.None, MeshIntersection.Classify(T(0, 0, 0, 2, 0, 0, 0, 2, 0), T(0, 0, 0.2, 2, 0, 0.2, 0, 2, 0.2), Tolerance));
    }

    [Fact]
    public void Triangles_MeetingAlongAnEdgeOrPoint_AreTouching()
    {
        var floor = T(-2, 0, -2, 2, 0, -2, 0, 0, 2);
        // A wall standing on the floor (one edge on its plane) and a spike touching it at one point.
        Assert.Equal(TriangleContact.Touching, MeshIntersection.Classify(floor, T(-1, 0, 0, 1, 0, 0, 0, 1, 0), Tolerance));
        Assert.Equal(TriangleContact.Touching, MeshIntersection.Classify(floor, T(0, 0, 0, 1, 1, 0, 0, 1, 1), Tolerance));
    }

    [Fact]
    public void Triangles_PenetratingLessThanTheTolerance_AreTouching()
    {
        Assert.Equal(TriangleContact.Touching, MeshIntersection.Classify(T(-2, 0, -2, 2, 0, -2, 0, 0, 2), T(-1, -0.01, 0, 1, -0.01, 0, 0, 1, 0), Tolerance));
    }

    [Fact]
    public void Compare_RejectsNoTolerance()
    {
        var box = new TriangleBvh(PartFixtures.Box(1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshIntersection.Compare(box, box, 0, true, true));
    }

    // ---- BVH ----

    [Fact]
    public void Bvh_FindsEveryOverlappingPair_LikeBruteForce()
    {
        // Two finely divided cylinders side by side, overlapping in a thin sliver.
        var a = PartFixtures.Prism(PartFixtures.Circle(5, 48), 30, levels: 8);
        var b = PartFixtures.Prism(PartFixtures.Circle(5, 48).Select(p => (p.X + 9, p.Y)).ToArray(), 30, levels: 8);
        var bvhPairs = new HashSet<(int, int)>();
        new TriangleBvh(a).VisitCandidatePairs(new TriangleBvh(b), Tolerance, (i, j) => bvhPairs.Add((i, j)) || true);

        // Every pair that really crosses or touches must be among the candidates.
        for (var i = 0; i < a.TriangleCount; i++)
        {
            for (var j = 0; j < b.TriangleCount; j++)
            {
                if (MeshIntersection.Classify(Tri(a, i), Tri(b, j), Tolerance) != TriangleContact.None)
                    Assert.Contains((i, j), bvhPairs);
            }
        }

        Assert.True(bvhPairs.Count < a.TriangleCount * b.TriangleCount / 4, "the BVH should prune most pairs");
    }

    private static Vector3D[] Tri(Mesh m, int t) =>
        [.. Enumerable.Range(0, 3).Select(k => new Vector3D(m.Positions[m.Indices[t * 3 + k] * 3], m.Positions[m.Indices[t * 3 + k] * 3 + 1], m.Positions[m.Indices[t * 3 + k] * 3 + 2]))];

    [Fact]
    public void Bvh_OfAnEmptyMeshVisitsNothing()
    {
        var empty = new TriangleBvh(new Mesh([], []));
        var visited = 0;
        Assert.True(empty.VisitCandidatePairs(new TriangleBvh(PartFixtures.Box(1, 1, 1)), Tolerance, (_, _) => ++visited > 0));
        Assert.Equal(0, visited);
    }

    // ---- Meshes ----

    [Fact]
    public void RodThroughPlate_Intersects_WithTheIntersectionCurveAroundTheRod()
    {
        var plate = Plate(40, 40, 2);
        var rod = Rod(6, 30, 'y');

        var result = Compare(plate, rod);

        Assert.Equal(MeshContact.Intersecting, result.Contact);
        Assert.True(result.Complete);
        // Two rings (top and bottom face of the plate) around a 48-gon of diameter 6.
        var perimeter = 48 * 6 * Math.Sin(Math.PI / 48);
        Assert.Equal(2 * perimeter, result.IntersectionLength, 1);
        var box = result.OverlapBounds!.Value;
        Assert.Equal(6, box.Size.X, 1);
        Assert.Equal(2, box.Size.Y, 2);
    }

    [Fact]
    public void PlateOnBlock_Touches_AndNeverIntersects()
    {
        var block = PartFixtures.Box(20, 20, 10, -10, -10, -10); // z from -10 to 0
        var plate = PartFixtures.Box(10, 10, 1, -5, -5, 0); // resting on z = 0

        var result = Compare(block, plate);

        Assert.Equal(MeshContact.Touching, result.Contact);
        Assert.Equal(0, result.CrossingPairs);
        Assert.True(result.TouchingPairs > 0);
    }

    [Fact]
    public void SeparatedParts_AreClear()
    {
        var result = Compare(PartFixtures.Box(10, 10, 10), PartFixtures.Box(10, 10, 10, 20, 0, 0));
        Assert.Equal(MeshContact.Clear, result.Contact);
        Assert.Equal(0, result.TriangleTests);
    }

    [Fact]
    public void SmallBlockInsideLargeOne_IsContained_EitherWay()
    {
        var large = PartFixtures.Box(20, 20, 20, -10, -10, -10);
        var small = PartFixtures.Box(4, 4, 4, -2, -2, -2);

        var inside = Compare(small, large);
        Assert.Equal((MeshContact.Contained, "a"), (inside.Contact, inside.ContainedPart));
        var around = Compare(large, small);
        Assert.Equal((MeshContact.Contained, "b"), (around.Contact, around.ContainedPart));
    }

    [Fact]
    public void RodInsideATubeBore_IsClear_NotContained()
    {
        // The bore is empty space: a rod with clearance in it touches nothing and is not inside the tube's solid.
        var result = Compare(Tube(10, 6, 40, 'x'), Rod(5, 60, 'x'));
        Assert.Equal(MeshContact.Clear, result.Contact);
        Assert.True(result.ContainmentChecked);
    }

    [Fact]
    public void RodLargerThanTheBore_Intersects()
    {
        Assert.Equal(MeshContact.Intersecting, Compare(Tube(10, 6, 40, 'x'), Rod(7, 60, 'x')).Contact);
    }

    [Fact]
    public void OpenMeshes_AreNotTestedForContainment()
    {
        var large = PartFixtures.Without(PartFixtures.Box(20, 20, 20, -10, -10, -10), (_, _, z) => z > 9.9);
        var small = PartFixtures.Box(4, 4, 4, -2, -2, -2);

        var result = Compare(small, large);

        Assert.Equal(MeshContact.Clear, result.Contact);
        Assert.False(result.ContainmentChecked);
    }

    [Fact]
    public void Budget_StopsTheCheckAndSaysSo()
    {
        var result = Compare(Plate(40, 40, 2), Rod(6, 30, 'y'), budget: 3);
        Assert.False(result.Complete);
        Assert.Equal(3, result.TriangleTests);
    }

    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(9.9, 9.9, 9.9, true)]
    [InlineData(10.5, 0, 0, false)]
    [InlineData(0, -30, 0, false)]
    public void ContainsPoint_UsesRayParity(double x, double y, double z, bool inside)
    {
        var box = new TriangleBvh(PartFixtures.Box(20, 20, 20, -10, -10, -10));
        Assert.Equal(inside, MeshIntersection.ContainsPoint(box, new Vector3D(x, y, z)));
    }
}
