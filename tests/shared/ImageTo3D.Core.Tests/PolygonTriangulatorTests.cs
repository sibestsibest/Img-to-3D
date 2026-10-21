using ImageTo3D.Core.Geometry;

namespace ImageTo3D.Core.Tests;

public class PolygonTriangulatorTests
{
    private sealed class Polygon
    {
        public List<double> X { get; } = [];
        public List<double> Y { get; } = [];

        public List<int> Loop(params (double X, double Y)[] points)
        {
            var loop = new List<int>();
            foreach (var (x, y) in points)
            {
                loop.Add(X.Count);
                X.Add(x);
                Y.Add(y);
            }

            return loop;
        }

        public double Area(List<int> triangles)
        {
            double area = 0;
            for (var t = 0; t < triangles.Count; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                var cross = (X[b] - X[a]) * (Y[c] - Y[a]) - (Y[b] - Y[a]) * (X[c] - X[a]);
                Assert.True(cross > 0, "every triangle must be counter-clockwise and non-degenerate");
                area += cross / 2;
            }

            return area;
        }
    }

    private static (double, double)[] Square(double s) => [(-s, -s), (s, -s), (s, s), (-s, s)];

    private static (double, double)[] Reversed((double, double)[] loop) => loop.Reverse().ToArray();

    [Fact]
    public void Square_TwoTriangles()
    {
        var poly = new Polygon();
        var outer = poly.Loop(Square(1));
        var triangles = new List<int>();

        Assert.True(PolygonTriangulator.TryTriangulate(poly.X, poly.Y, outer, [], triangles, out _));

        Assert.Equal(6, triangles.Count);
        Assert.Equal(4, poly.Area(triangles), 10);
    }

    [Fact]
    public void SquareWithSquareHole_AreaExcludesHole()
    {
        var poly = new Polygon();
        var outer = poly.Loop(Square(10));
        var hole = poly.Loop(Reversed(Square(5)));
        var triangles = new List<int>();

        Assert.True(PolygonTriangulator.TryTriangulate(poly.X, poly.Y, outer, [hole], triangles, out _));

        Assert.Equal(300, poly.Area(triangles), 8);
        Assert.Equal(8 * 3, triangles.Count); // n + 2h - 2 = 8 + 2 - 2
    }

    [Fact]
    public void NonConvexWithTwoHoles()
    {
        var poly = new Polygon();
        var outer = poly.Loop((0, 0), (30, 0), (30, 10), (10, 10), (10, 30), (0, 30));
        var h1 = poly.Loop(Reversed([(2, 2), (6, 2), (6, 6), (2, 6)]));
        var h2 = poly.Loop(Reversed([(2, 20), (6, 20), (6, 26), (2, 26)]));
        var triangles = new List<int>();

        Assert.True(PolygonTriangulator.TryTriangulate(poly.X, poly.Y, outer, [h1, h2], triangles, out _));

        Assert.Equal(500 - 16 - 24, poly.Area(triangles), 8);
    }

    [Fact]
    public void CircleWithCircularHole()
    {
        var poly = new Polygon();
        var outer = poly.Loop(PartFixtures.Circle(5, 48));
        var hole = poly.Loop(Reversed(PartFixtures.Circle(3, 48)));
        var triangles = new List<int>();

        Assert.True(PolygonTriangulator.TryTriangulate(poly.X, poly.Y, outer, [hole], triangles, out _));

        var expected = 0.5 * 48 * Math.Sin(2 * Math.PI / 48) * (25 - 9);
        Assert.Equal(expected, poly.Area(triangles), 8);
    }

    [Fact]
    public void CollinearVertices_AreKeptWithoutDegenerateTriangles()
    {
        var poly = new Polygon();
        var outer = poly.Loop((0, 0), (1, 0), (2, 0), (2, 1), (2, 2), (1, 2), (0, 2), (0, 1));
        var triangles = new List<int>();

        Assert.True(PolygonTriangulator.TryTriangulate(poly.X, poly.Y, outer, [], triangles, out _));

        Assert.Equal(4, poly.Area(triangles), 10);
        Assert.Equal(8, triangles.Distinct().Count()); // every boundary vertex is used: no T-junctions
    }

    [Fact]
    public void ClockwiseOuter_IsRejected()
    {
        var poly = new Polygon();
        var outer = poly.Loop(Reversed(Square(1)));

        Assert.False(PolygonTriangulator.TryTriangulate(poly.X, poly.Y, outer, [], [], out var error));
        Assert.Contains("counter-clockwise", error);
    }
}
