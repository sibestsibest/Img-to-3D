namespace ImageTo3D.Core.Geometry;

public enum ScaleAxis
{
    /// <summary>Width (X).</summary>
    X,

    /// <summary>Height (Y, up in the viewer; Z in the exported STL).</summary>
    Y,

    /// <summary>Depth (Z).</summary>
    Z,

    /// <summary>Whichever bounding-box side is longest.</summary>
    Longest,
}

public static class ScaleCalculator
{
    public const double MinTargetMm = 0.1;
    public const double MaxTargetMm = 10_000;

    /// <summary>
    /// Returns the uniform factor that makes the chosen bounding-box side equal to <paramref name="targetMm"/>.
    /// <paramref name="currentSizeMm"/> is the current bounding-box size in millimetres.
    /// </summary>
    public static double ComputeUniformFactor(Vector3D currentSizeMm, ScaleAxis axis, double targetMm)
    {
        if (!double.IsFinite(targetMm) || targetMm < MinTargetMm || targetMm > MaxTargetMm)
            throw new ArgumentOutOfRangeException(nameof(targetMm), $"Target length must be between {MinTargetMm} and {MaxTargetMm} mm.");

        var current = axis switch
        {
            ScaleAxis.X => currentSizeMm.X,
            ScaleAxis.Y => currentSizeMm.Y,
            ScaleAxis.Z => currentSizeMm.Z,
            ScaleAxis.Longest => currentSizeMm.MaxComponent,
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };

        if (!(current > 0))
            throw new InvalidOperationException("The selected dimension is zero; the model cannot be scaled along it.");

        return targetMm / current;
    }

    public static Vector3D Apply(Vector3D sizeMm, double factor) => new(sizeMm.X * factor, sizeMm.Y * factor, sizeMm.Z * factor);
}
