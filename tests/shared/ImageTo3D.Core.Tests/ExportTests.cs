using ImageTo3D.Core.Geometry;
using Mesh = ImageTo3D.Core.Geometry.Mesh;
using ModelRoot = SharpGLTF.Schema2.ModelRoot;

namespace ImageTo3D.Core.Tests;

public class ExportTests
{
    [Fact]
    public void Glb_RoundTripsGeometryAndColors()
    {
        var cube = GeometryTestData.UnitCube();

        var glb = GlbWriter.Write(cube);
        var read = GlbReader.Read(glb);

        Assert.Equal(cube.Positions, read.Positions);
        Assert.Equal(cube.Indices, read.Indices);
        Assert.NotNull(read.Colors);
        Assert.Equal(cube.Colors, read.Colors);
    }

    [Fact]
    public void Glb_IsAcceptedByIndependentGltfValidator()
    {
        var glb = GlbWriter.Write(GeometryTestData.UnitCube());

        // SharpGLTF validates the schema, buffer layout and accessor bounds on load.
        var model = ModelRoot.ParseGLB(new ArraySegment<byte>(glb));

        var primitive = Assert.Single(Assert.Single(model.LogicalMeshes).Primitives);
        Assert.Equal(8, primitive.GetVertexAccessor("POSITION").Count);
        Assert.NotNull(primitive.GetVertexAccessor("NORMAL"));
        Assert.NotNull(primitive.GetVertexAccessor("COLOR_0"));
        Assert.Equal(36, primitive.GetIndexAccessor().Count);
    }

    [Fact]
    public void Glb_RejectsEmptyMesh()
    {
        Assert.Throws<InvalidOperationException>(() => GlbWriter.Write(new Mesh([], [])));
    }

    [Fact]
    public void GlbReader_RejectsNonGlbData()
    {
        Assert.Throws<InvalidDataException>(() => GlbReader.Read("solid fake stl"u8.ToArray()));
    }

    [Fact]
    public void Stl_IsRealBinaryStlWithExpectedTriangles()
    {
        var cube = GeometryTestData.UnitCube();

        var stl = StlWriter.Write(cube);

        Assert.Equal(84 + 12 * 50, stl.Length);
        Assert.False(System.Text.Encoding.ASCII.GetString(stl, 0, 5) == "solid", "Binary STL must not start with 'solid'.");
        Assert.NotEqual((byte)'g', stl[0]); // not a renamed GLB ("glTF" magic)

        var read = StlReader.ReadBinary(stl);
        Assert.Equal(12, read.TriangleCount);
        Assert.True(MeshValidator.Validate(read).IsValid);
        Assert.Equal(1.0, MeshOperations.SignedVolume(read), 5);
    }

    [Fact]
    public void Stl_StoredNormalsPointOutward()
    {
        var stl = StlWriter.Write(GeometryTestData.UnitCube());

        // First triangle is on z = 0, so its normal must be -Z.
        var nz = BitConverter.ToSingle(stl, 84 + 8);
        Assert.Equal(-1f, nz, 5);
    }

    [Fact]
    public void Stl_DropsDegenerateTriangles()
    {
        var mesh = new Mesh([0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 1, 2, 0, 1, 1, 0, 2, 3]);

        var stl = StlWriter.Write(mesh);

        Assert.Equal(2, StlReader.ReadBinary(stl).TriangleCount);
    }

    [Fact]
    public void StlReader_RejectsTruncatedFile()
    {
        var stl = StlWriter.Write(GeometryTestData.UnitCube());
        Assert.Throws<InvalidDataException>(() => StlReader.ReadBinary(stl.AsSpan(0, stl.Length - 10)));
    }

    [Fact]
    public void GlbToStl_ConversionPreservesGeometry()
    {
        var glb = GlbWriter.Write(GeometryTestData.UnitCube());

        var fromGlb = GlbReader.Read(glb);
        var stl = StlReader.ReadBinary(StlWriter.Write(fromGlb));

        var size = MeshOperations.ComputeBounds(stl).Size;
        Assert.Equal(new Vector3D(1, 1, 1), size);
        Assert.Equal(12, stl.TriangleCount);
    }
}
