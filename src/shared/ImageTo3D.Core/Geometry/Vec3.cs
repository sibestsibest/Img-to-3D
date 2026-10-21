namespace ImageTo3D.Core.Geometry;

/// <summary>Double-precision vector used inside geometry algorithms (mesh storage stays float).</summary>
internal readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);

    public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;

    public Vec3 Cross(Vec3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public Vec3 Normalized()
    {
        var length = Length;
        return length > 0 ? this * (1 / length) : this;
    }

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

    public static Vec3 From(Vector3D v) => new(v.X, v.Y, v.Z);

    public Vector3D ToVector3D() => new(X, Y, Z);

    public static Vec3 At(float[] positions, int vertex) =>
        new(positions[vertex * 3], positions[vertex * 3 + 1], positions[vertex * 3 + 2]);
}
