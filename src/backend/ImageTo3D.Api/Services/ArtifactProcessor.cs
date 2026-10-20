using ImageTo3D.Core.Geometry;

namespace ImageTo3D.Api.Services;

public sealed record ScaledArtifacts(byte[] Glb, byte[]? Stl, string? StlError, Vector3D SizeMm);

/// <summary>
/// Converts the raw inference mesh (model units, Y-up) into the downloadable artifacts:
/// GLB in metres (glTF's unit) and binary STL in millimetres, Z-up, resting on Z = 0.
/// Both are produced from the same parsed mesh, so STL is a real conversion, never a renamed GLB.
/// </summary>
public sealed class ArtifactProcessor(ILogger<ArtifactProcessor> logger)
{
    public static Mesh LoadRawMesh(byte[] rawGlb)
    {
        var mesh = GlbReader.Read(rawGlb);
        var validation = MeshValidator.Validate(mesh);
        if (!validation.IsValid)
            throw new InvalidDataException("Generated mesh is invalid: " + string.Join(" ", validation.Errors));
        return mesh;
    }

    /// <param name="rawGlb">Inference output; its textures/materials are carried over unchanged.</param>
    /// <param name="rawMesh">Geometry parsed from <paramref name="rawGlb"/> (node transforms applied).</param>
    public ScaledArtifacts Build(byte[] rawGlb, Mesh rawMesh, double mmPerModelUnit)
    {
        var bounds = MeshOperations.ComputeBounds(rawMesh);
        var grounded = MeshOperations.CenterOnGround(rawMesh);
        var millimetres = MeshOperations.Scale(grounded, mmPerModelUnit);
        var sizeMm = MeshOperations.ComputeBounds(millimetres).Size;

        // GLB: wrap the original scene in a root node (metres = mm / 1000, centred, resting on y = 0)
        // instead of re-encoding, so UV textures and normal maps from engines like SF3D survive.
        var metresPerUnit = mmPerModelUnit * 0.001;
        var center = bounds.Center;
        var glb = GlbTransformer.ApplyUniformTransform(
            rawGlb,
            metresPerUnit,
            new Vector3D(-center.X * metresPerUnit, -bounds.Min.Y * metresPerUnit, -center.Z * metresPerUnit),
            $"AI-generated estimate. Units: metres. Uniformly scaled; longest side {sizeMm.MaxComponent:F2} mm.");
        var glbCheck = MeshValidator.Validate(GlbReader.Read(glb));
        if (!glbCheck.IsValid)
            throw new InvalidDataException("Scaled GLB failed validation: " + string.Join(" ", glbCheck.Errors));

        byte[]? stl = null;
        string? stlError = null;
        try
        {
            stl = StlWriter.Write(MeshOperations.YUpToZUp(millimetres));

            // Validate the bytes we are about to serve, not just the in-memory mesh.
            var check = MeshValidator.Validate(StlReader.ReadBinary(stl));
            if (!check.IsValid)
                throw new InvalidDataException(string.Join(" ", check.Errors));
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            logger.LogWarning(ex, "STL export failed; GLB is kept");
            stl = null;
            stlError = "STL export failed: the generated mesh could not be converted to valid STL geometry. The GLB file is still available.";
        }

        return new ScaledArtifacts(glb, stl, stlError, sizeMm);
    }
}
