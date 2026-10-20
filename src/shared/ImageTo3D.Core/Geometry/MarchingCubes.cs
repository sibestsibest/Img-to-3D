namespace ImageTo3D.Core.Geometry;

/// <summary>
/// Classic marching cubes (Lorensen &amp; Cline, tables from Paul Bourke) over a regular scalar grid.
/// The field is laid out x-major: index = (x * ny + y) * nz + z, which matches a NumPy
/// meshgrid with indexing="ij" flattened in C order.
/// Points with value &gt; isoLevel are considered inside the surface.
/// Output vertices are in grid-index coordinates (0..n-1 per axis); vertices on a shared
/// grid edge are welded, so a closed iso-surface yields a closed mesh.
/// </summary>
public static class MarchingCubes
{
    // Corner offsets in Bourke's ordering.
    private static readonly int[,] CornerOffsets =
    {
        { 0, 0, 0 }, { 1, 0, 0 }, { 1, 1, 0 }, { 0, 1, 0 },
        { 0, 0, 1 }, { 1, 0, 1 }, { 1, 1, 1 }, { 0, 1, 1 },
    };

    // Corner pairs for each of the 12 cube edges.
    private static readonly int[,] EdgeCorners =
    {
        { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 0 },
        { 4, 5 }, { 5, 6 }, { 6, 7 }, { 7, 4 },
        { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 },
    };

    public static Mesh Extract(
        ReadOnlySpan<float> field,
        int nx,
        int ny,
        int nz,
        float isoLevel,
        CancellationToken cancellationToken = default)
    {
        if (nx < 2 || ny < 2 || nz < 2)
            throw new ArgumentException("Grid must have at least 2 samples per axis.");
        if (field.Length != (long)nx * ny * nz)
            throw new ArgumentException("Field length does not match grid dimensions.", nameof(field));

        var positions = new List<float>();
        var indices = new List<int>();
        var edgeVertex = new Dictionary<long, int>();

        Span<float> values = stackalloc float[8];
        Span<int> cubeEdgeVertex = stackalloc int[12];

        for (var x = 0; x < nx - 1; x++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var y = 0; y < ny - 1; y++)
            {
                for (var z = 0; z < nz - 1; z++)
                {
                    var cubeIndex = 0;
                    for (var c = 0; c < 8; c++)
                    {
                        var v = field[(int)Index(x + CornerOffsets[c, 0], y + CornerOffsets[c, 1], z + CornerOffsets[c, 2], ny, nz)];
                        values[c] = v;
                        if (!(v > isoLevel))
                            cubeIndex |= 1 << c;
                    }

                    var edgeMask = MarchingCubesTables.EdgeTable[cubeIndex];
                    if (edgeMask == 0)
                        continue;

                    for (var e = 0; e < 12; e++)
                    {
                        if ((edgeMask & (1 << e)) == 0)
                            continue;

                        var a = EdgeCorners[e, 0];
                        var b = EdgeCorners[e, 1];
                        var ax = x + CornerOffsets[a, 0];
                        var ay = y + CornerOffsets[a, 1];
                        var az = z + CornerOffsets[a, 2];
                        var bx = x + CornerOffsets[b, 0];
                        var by = y + CornerOffsets[b, 1];
                        var bz = z + CornerOffsets[b, 2];

                        // Key the vertex by the edge's lower corner and its axis so neighbours share it.
                        var lx = Math.Min(ax, bx);
                        var ly = Math.Min(ay, by);
                        var lz = Math.Min(az, bz);
                        var axis = ax != bx ? 0 : ay != by ? 1 : 2;
                        var key = Index(lx, ly, lz, ny, nz) * 3 + axis;

                        if (!edgeVertex.TryGetValue(key, out var vertexId))
                        {
                            var va = values[a];
                            var vb = values[b];
                            var denominator = vb - va;
                            var t = Math.Abs(denominator) < 1e-12f ? 0.5f : (isoLevel - va) / denominator;
                            t = Math.Clamp(t, 0f, 1f);
                            vertexId = positions.Count / 3;
                            positions.Add(ax + t * (bx - ax));
                            positions.Add(ay + t * (by - ay));
                            positions.Add(az + t * (bz - az));
                            edgeVertex.Add(key, vertexId);
                        }

                        cubeEdgeVertex[e] = vertexId;
                    }

                    var row = cubeIndex * 16;
                    for (var i = 0; MarchingCubesTables.TriTable[row + i] != -1; i += 3)
                    {
                        // With bits marking corners outside the surface, Bourke's table order is
                        // counter-clockwise viewed from outside (verified by the sphere volume test).
                        indices.Add(cubeEdgeVertex[MarchingCubesTables.TriTable[row + i]]);
                        indices.Add(cubeEdgeVertex[MarchingCubesTables.TriTable[row + i + 1]]);
                        indices.Add(cubeEdgeVertex[MarchingCubesTables.TriTable[row + i + 2]]);
                    }
                }
            }
        }

        return new Mesh(positions.ToArray(), indices.ToArray());
    }

    private static long Index(int x, int y, int z, int ny, int nz) => ((long)x * ny + y) * nz + z;
}
