using ImageTo3D.Core.Geometry;

namespace ImageTo3D.Core.Tests;

public class MeshOperationsTests
{
    [Theory]
    [InlineData(ScaleAxis.X, 100, 2.0)]
    [InlineData(ScaleAxis.Y, 100, 1.0)]
    [InlineData(ScaleAxis.Z, 100, 4.0)]
    [InlineData(ScaleAxis.Longest, 50, 0.5)]
    public void ScaleCalculator_ComputesUniformFactor(ScaleAxis axis, double target, double expected)
    {
        var size = new Vector3D(50, 100, 25);

        var factor = ScaleCalculator.ComputeUniformFactor(size, axis, target);

        Assert.Equal(expected, factor, 10);
        var scaled = ScaleCalculator.Apply(size, factor);
        Assert.Equal(target, scaled[axis == ScaleAxis.Longest ? 1 : (int)axis], 10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(0.01)]
    [InlineData(20_000)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ScaleCalculator_RejectsInvalidTarget(double target)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScaleCalculator.ComputeUniformFactor(new Vector3D(1, 1, 1), ScaleAxis.X, target));
    }

    [Fact]
    public void ScaleCalculator_RejectsZeroDimension()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ScaleCalculator.ComputeUniformFactor(new Vector3D(0, 1, 1), ScaleAxis.X, 10));
    }

    [Fact]
    public void Scale_IsUniformAndKeepsWinding()
    {
        var cube = GeometryTestData.UnitCube();

        var scaled = MeshOperations.Scale(cube, 25.4);

        Assert.Equal(new Vector3D(25.4, 25.4, 25.4), Round(MeshOperations.ComputeBounds(scaled).Size));
        Assert.Equal(25.4 * 25.4 * 25.4, MeshOperations.SignedVolume(scaled), 2);
    }

    [Fact]
    public void YUpToZUp_MapsHeightToZ()
    {
        var mesh = MeshOperations.Scale(GeometryTestData.UnitCube(), 1);
        var tall = MeshOperations.Transform(mesh, [1, 0, 0, 0, 3, 0, 0, 0, 2]); // width 1, height 3, depth 2 (Y-up)

        var zUp = MeshOperations.YUpToZUp(tall);

        var size = Round(MeshOperations.ComputeBounds(zUp).Size);
        Assert.Equal(new Vector3D(1, 2, 3), size);
        Assert.True(MeshOperations.SignedVolume(zUp) > 0);
    }

    [Fact]
    public void Transform_WithReflection_FlipsWindingToStayOutward()
    {
        var mirrored = MeshOperations.Transform(GeometryTestData.UnitCube(), [-1, 0, 0, 0, 1, 0, 0, 0, 1]);

        Assert.True(MeshOperations.SignedVolume(mirrored) > 0);
    }

    [Fact]
    public void CenterOnGround_PlacesModelOnYZero()
    {
        var moved = MeshOperations.Transform(GeometryTestData.UnitCube(), [1, 0, 0, 0, 1, 0, 0, 0, 1], new Vector3D(5, 7, -3));

        var b = MeshOperations.ComputeBounds(MeshOperations.CenterOnGround(moved));

        Assert.Equal(0, b.Min.Y, 6);
        Assert.Equal(0, b.Center.X, 6);
        Assert.Equal(0, b.Center.Z, 6);
    }

    [Fact]
    public void Validator_FlagsNonFiniteAndOutOfRange()
    {
        var nan = new Mesh([0, 0, 0, 1, 0, float.NaN, 0, 1, 0], [0, 1, 2]);
        var badIndex = new Mesh([0, 0, 0, 1, 0, 0, 0, 1, 0], [0, 1, 5]);
        var flat = new Mesh([0, 0, 0, 1, 0, 0, 0, 1, 0], [0, 1, 2]);

        Assert.Contains(MeshValidator.Validate(nan).Errors, e => e.Contains("non-finite"));
        Assert.Contains(MeshValidator.Validate(badIndex).Errors, e => e.Contains("outside"));
        Assert.Contains(MeshValidator.Validate(flat).Errors, e => e.Contains("zero extent"));
        Assert.True(MeshValidator.Validate(GeometryTestData.UnitCube()).IsValid);
    }

    private static Vector3D Round(Vector3D v) => new(Math.Round(v.X, 4), Math.Round(v.Y, 4), Math.Round(v.Z, 4));
}
