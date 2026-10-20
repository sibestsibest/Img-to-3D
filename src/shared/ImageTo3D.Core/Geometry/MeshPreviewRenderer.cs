using ImageTo3D.Core.Imaging;

namespace ImageTo3D.Core.Geometry;

/// <summary>
/// Tiny CPU rasteriser (orthographic, z-buffer, Lambert shading with vertex colours) used for
/// thumbnails and for verifying generated meshes without a GPU or browser.
/// </summary>
public static class MeshPreviewRenderer
{
    /// <param name="yawDegrees">Rotation about the vertical (Y) axis applied before projecting onto the XY plane.</param>
    public static RgbaImage Render(Mesh mesh, int size, double yawDegrees = 0, double pitchDegrees = 0)
    {
        var yaw = yawDegrees * Math.PI / 180;
        var pitch = pitchDegrees * Math.PI / 180;
        double cy = Math.Cos(yaw), sy = Math.Sin(yaw), cp = Math.Cos(pitch), sp = Math.Sin(pitch);
        var rotated = MeshOperations.Transform(mesh,
        [
            cy, 0, sy,
            sy * sp, cp, -cy * sp,
            -sy * cp, sp, cy * cp,
        ]);

        var bounds = MeshOperations.ComputeBounds(rotated);
        var extent = Math.Max(bounds.Size.X, bounds.Size.Y);
        var scale = extent > 0 ? size * 0.9 / extent : 1;
        var center = bounds.Center;

        var image = new RgbaImage(size, size);
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            image.Pixels[i] = image.Pixels[i + 1] = image.Pixels[i + 2] = 245;
            image.Pixels[i + 3] = 255;
        }

        var depth = new float[size * size];
        Array.Fill(depth, float.NegativeInfinity);
        var p = rotated.Positions;
        var normals = MeshOperations.ComputeVertexNormals(rotated);
        var light = Normalize(0.4, 0.6, 1.0);

        (double X, double Y, double Z) Project(int v) =>
            ((p[v * 3] - center.X) * scale + size / 2.0, size / 2.0 - (p[v * 3 + 1] - center.Y) * scale, p[v * 3 + 2]);

        var idx = rotated.Indices;
        for (var t = 0; t < idx.Length; t += 3)
        {
            var a = Project(idx[t]);
            var b = Project(idx[t + 1]);
            var c = Project(idx[t + 2]);
            var area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (area >= 0)
                continue; // back-facing (screen Y points down, so front faces have negative area)

            var minX = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
            var maxX = Math.Min(size - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
            var minY = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
            var maxY = Math.Min(size - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    var w0 = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) / area;
                    var w1 = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) / area;
                    var w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0)
                        continue;

                    var z = (float)(w0 * a.Z + w1 * b.Z + w2 * c.Z);
                    if (z <= depth[y * size + x])
                        continue;
                    depth[y * size + x] = z;

                    double nx = 0, ny = 0, nz = 0, r = 0, g = 0, bl = 0;
                    var weights = (w0, w1, w2);
                    for (var k = 0; k < 3; k++)
                    {
                        var w = k == 0 ? weights.w0 : k == 1 ? weights.w1 : weights.w2;
                        var v = idx[t + k];
                        nx += w * normals[v * 3];
                        ny += w * normals[v * 3 + 1];
                        nz += w * normals[v * 3 + 2];
                        r += w * (rotated.Colors?[v * 3] ?? 200);
                        g += w * (rotated.Colors?[v * 3 + 1] ?? 200);
                        bl += w * (rotated.Colors?[v * 3 + 2] ?? 200);
                    }

                    var n = Normalize(nx, ny, nz);
                    var shade = 0.35 + 0.65 * Math.Max(0, n.X * light.X + n.Y * light.Y + n.Z * light.Z);
                    var o = (y * size + x) * 4;
                    image.Pixels[o] = (byte)Math.Clamp(r * shade, 0, 255);
                    image.Pixels[o + 1] = (byte)Math.Clamp(g * shade, 0, 255);
                    image.Pixels[o + 2] = (byte)Math.Clamp(bl * shade, 0, 255);
                }
            }
        }

        return image;
    }

    private static (double X, double Y, double Z) Normalize(double x, double y, double z)
    {
        var len = Math.Sqrt(x * x + y * y + z * z);
        return len > 0 ? (x / len, y / len, z / len) : (0, 0, 1);
    }
}
