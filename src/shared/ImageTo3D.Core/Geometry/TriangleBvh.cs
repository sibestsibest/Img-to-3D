namespace ImageTo3D.Core.Geometry;

/// <summary>
/// Bounding-volume hierarchy over the triangles of a mesh (axis-aligned boxes, median split on the
/// longest axis). Used to find candidate triangle pairs between two meshes and ray hits without
/// testing every triangle. Built once per mesh; read-only afterwards.
/// </summary>
public sealed class TriangleBvh
{
    private const int LeafSize = 8;

    // Node arrays: box per node; a leaf has Count > 0 and covers Order[Start .. Start + Count).
    private readonly List<double> _boxes = [];
    private readonly List<int> _left = [];
    private readonly List<int> _right = [];
    private readonly List<int> _start = [];
    private readonly List<int> _count = [];
    private readonly int[] _order;

    public TriangleBvh(Mesh mesh)
    {
        Mesh = mesh;
        var triangles = mesh.TriangleCount;
        _order = new int[triangles];
        var centroids = new double[triangles * 3];
        var p = mesh.Positions;
        var idx = mesh.Indices;
        for (var t = 0; t < triangles; t++)
        {
            _order[t] = t;
            for (var axis = 0; axis < 3; axis++)
                centroids[t * 3 + axis] = (p[idx[t * 3] * 3 + axis] + p[idx[t * 3 + 1] * 3 + axis] + p[idx[t * 3 + 2] * 3 + axis]) / 3.0;
        }

        if (triangles > 0)
            Build(0, triangles, centroids);
    }

    public Mesh Mesh { get; }

    public int NodeCount => _left.Count;

    /// <summary>Bounds of the whole mesh (all zero for an empty mesh).</summary>
    public BoundingBox Bounds => NodeCount == 0 ? default : Box(0);

    private int Build(int start, int count, double[] centroids)
    {
        var node = _left.Count;
        _left.Add(-1);
        _right.Add(-1);
        _start.Add(start);
        _count.Add(count);
        var box = TrianglesBox(start, count);
        _boxes.AddRange([box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z]);
        if (count <= LeafSize)
            return node;

        // Split on the longest axis of the centroid spread, at the median.
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        for (var i = start; i < start + count; i++)
        {
            var t = _order[i];
            minX = Math.Min(minX, centroids[t * 3]);
            maxX = Math.Max(maxX, centroids[t * 3]);
            minY = Math.Min(minY, centroids[t * 3 + 1]);
            maxY = Math.Max(maxY, centroids[t * 3 + 1]);
            minZ = Math.Min(minZ, centroids[t * 3 + 2]);
            maxZ = Math.Max(maxZ, centroids[t * 3 + 2]);
        }

        var spreadX = maxX - minX;
        var spreadY = maxY - minY;
        var spreadZ = maxZ - minZ;
        if (Math.Max(spreadX, Math.Max(spreadY, spreadZ)) <= 0)
            return node; // all centroids coincide: keep as one (large) leaf

        var axis = spreadX >= spreadY && spreadX >= spreadZ ? 0 : spreadY >= spreadZ ? 1 : 2;
        Array.Sort(_order, start, count, Comparer<int>.Create((a, b) => centroids[a * 3 + axis].CompareTo(centroids[b * 3 + axis])));
        var half = count / 2;

        _count[node] = 0;
        var left = Build(start, half, centroids);
        var right = Build(start + half, count - half, centroids);
        _left[node] = left;
        _right[node] = right;
        return node;
    }

    private BoundingBox TrianglesBox(int start, int count)
    {
        var p = Mesh.Positions;
        var idx = Mesh.Indices;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        for (var i = start; i < start + count; i++)
        {
            var t = _order[i];
            for (var k = 0; k < 3; k++)
            {
                var v = idx[t * 3 + k] * 3;
                minX = Math.Min(minX, p[v]);
                maxX = Math.Max(maxX, p[v]);
                minY = Math.Min(minY, p[v + 1]);
                maxY = Math.Max(maxY, p[v + 1]);
                minZ = Math.Min(minZ, p[v + 2]);
                maxZ = Math.Max(maxZ, p[v + 2]);
            }
        }

        return new BoundingBox(new Vector3D(minX, minY, minZ), new Vector3D(maxX, maxY, maxZ));
    }

    private BoundingBox Box(int node)
    {
        var b = node * 6;
        return new BoundingBox(new Vector3D(_boxes[b], _boxes[b + 1], _boxes[b + 2]), new Vector3D(_boxes[b + 3], _boxes[b + 4], _boxes[b + 5]));
    }

    private bool Overlaps(int node, TriangleBvh other, int otherNode, double margin)
    {
        var a = node * 6;
        var b = otherNode * 6;
        for (var axis = 0; axis < 3; axis++)
        {
            if (_boxes[a + axis] > other._boxes[b + 3 + axis] + margin || other._boxes[b + axis] > _boxes[a + 3 + axis] + margin)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Calls <paramref name="visit"/> for every pair of triangles (one from each mesh) whose boxes
    /// overlap when grown by <paramref name="margin"/>. Return false from the callback to stop.
    /// </summary>
    /// <returns>False when the callback stopped the traversal.</returns>
    public bool VisitCandidatePairs(TriangleBvh other, double margin, Func<int, int, bool> visit)
    {
        if (NodeCount == 0 || other.NodeCount == 0)
            return true;
        var stack = new Stack<(int A, int B)>();
        stack.Push((0, 0));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (!Overlaps(a, other, b, margin))
                continue;

            var leafA = _count[a] > 0 || _left[a] < 0;
            var leafB = other._count[b] > 0 || other._left[b] < 0;
            if (leafA && leafB)
            {
                for (var i = _start[a]; i < _start[a] + _count[a]; i++)
                {
                    for (var j = other._start[b]; j < other._start[b] + other._count[b]; j++)
                    {
                        if (!visit(_order[i], other._order[j]))
                            return false;
                    }
                }

                continue;
            }

            // Descend into the larger node (or the only inner one).
            if (leafB || (!leafA && Volume(a) >= other.Volume(b)))
            {
                stack.Push((_left[a], b));
                stack.Push((_right[a], b));
            }
            else
            {
                stack.Push((a, other._left[b]));
                stack.Push((a, other._right[b]));
            }
        }

        return true;
    }

    private double Volume(int node)
    {
        var b = node * 6;
        return (_boxes[b + 3] - _boxes[b]) * (_boxes[b + 4] - _boxes[b + 1]) * (_boxes[b + 5] - _boxes[b + 2]);
    }

    /// <summary>Triangles whose box the ray (origin + t·direction, t &gt; 0) passes through.</summary>
    public void VisitRayCandidates(Vector3D origin, Vector3D direction, Action<int> visit)
    {
        if (NodeCount == 0)
            return;
        var inverse = new Vector3D(1 / direction.X, 1 / direction.Y, 1 / direction.Z);
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!RayHitsBox(node, origin, inverse))
                continue;
            if (_count[node] > 0 || _left[node] < 0)
            {
                for (var i = _start[node]; i < _start[node] + _count[node]; i++)
                    visit(_order[i]);
                continue;
            }

            stack.Push(_left[node]);
            stack.Push(_right[node]);
        }
    }

    private bool RayHitsBox(int node, Vector3D origin, Vector3D inverse)
    {
        var b = node * 6;
        double tMin = 0, tMax = double.MaxValue;
        for (var axis = 0; axis < 3; axis++)
        {
            var o = origin[axis];
            var inv = inverse[axis];
            var t1 = (_boxes[b + axis] - o) * inv;
            var t2 = (_boxes[b + 3 + axis] - o) * inv;
            if (double.IsNaN(t1) || double.IsNaN(t2))
                continue; // ray parallel to and on the slab boundary: treat as inside
            if (t1 > t2)
                (t1, t2) = (t2, t1);
            tMin = Math.Max(tMin, t1);
            tMax = Math.Min(tMax, t2);
            if (tMin > tMax)
                return false;
        }

        return true;
    }
}
