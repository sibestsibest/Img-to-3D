using System.Numerics;
using ImageTo3D.Core.Imaging;

namespace ImageTo3D.Core.Reconstruction;

/// <param name="Severity">"info", "warning" or "blocker" (reconstruction cannot start).</param>
/// <param name="Photo">Index of the photo the finding is about; null for the whole set.</param>
public sealed record PhotoSetFinding(string Code, string Severity, string Message, int? Photo = null);

/// <summary>
/// Checks a photo set before photogrammetry. Only the photo count can block: everything else is a
/// warning, because Meshroom may still succeed and only a run can tell. Thresholds are rules of
/// thumb from photogrammetry practice, not measurements of this application.
/// </summary>
public static class PhotoSetCheck
{
    public const int MinPhotos = 3;
    public const int RecommendedPhotos = 20;
    public const int MinShortSide = 1000;

    /// <summary>Laplacian variance at 1024 px below which a photo is almost certainly out of focus.</summary>
    public const double BlurryBelow = 15;

    /// <summary>A photo this much less sharp than the set's median is flagged (sets of 5 or more).</summary>
    public const double RelativeBlur = 0.35;

    /// <summary>dHash bits (of 64): at or below this two photos are near-identical (no new viewpoint).</summary>
    public const int DuplicateDistance = 2;

    public static IReadOnlyList<PhotoSetFinding> Check(IReadOnlyList<PhotoFacts> photos)
    {
        var findings = new List<PhotoSetFinding>();
        if (photos.Count < MinPhotos)
            findings.Add(new("too_few_photos", "blocker", $"At least {MinPhotos} photos are needed; {photos.Count} given."));
        else if (photos.Count < RecommendedPhotos)
            findings.Add(new("few_photos", "warning", $"{photos.Count} photos: photogrammetry usually needs {RecommendedPhotos} or more overlapping views all around the object (each spot visible in at least 3 photos)."));

        var median = Median(photos.Select(p => p.Sharpness));
        for (var i = 0; i < photos.Count; i++)
        {
            var p = photos[i];
            if (Math.Min(p.Width, p.Height) < MinShortSide)
                findings.Add(new("low_resolution", "warning", $"Only {p.Width} x {p.Height} px: fine detail will not be found. Use full-resolution camera photos.", i));
            if (p.Sharpness < BlurryBelow)
                findings.Add(new("blurry", "warning", "The photo looks out of focus or motion-blurred; blurred photos add noise or fail to match.", i));
            else if (photos.Count >= 5 && p.Sharpness < RelativeBlur * median)
                findings.Add(new("blurrier_than_others", "warning", "Much less sharp than the other photos; check focus.", i));

            // Near-featureless pictures (plain backgrounds) all hash alike; do not call them duplicates.
            if (BitOperations.PopCount(p.DHash) is > 4 and < 60)
            {
                for (var j = 0; j < i; j++)
                {
                    if (ImageHash.Distance(p.DHash, photos[j].DHash) <= DuplicateDistance)
                    {
                        findings.Add(new("near_duplicate", "warning", $"Almost the same as photo {j + 1}: it adds no new viewpoint.", i));
                        break;
                    }
                }
            }
        }

        if (photos.Count > 0 && photos.All(p => !p.HasExif))
            findings.Add(new("no_camera_metadata", "warning", "No photo has EXIF camera data, so the focal length is guessed; use the camera's original files, not screenshots or exports."));
        var sizes = photos.Select(p => (Math.Max(p.Width, p.Height), Math.Min(p.Width, p.Height))).Distinct().Count();
        if (sizes > 1)
            findings.Add(new("mixed_sizes", "info", $"{sizes} different image sizes: probably several cameras or crops. That works, but one camera with fixed zoom is more reliable."));
        findings.Add(new("surface_limits", "info", "Shiny, transparent, very dark or plain single-colour surfaces reconstruct poorly; matt surfaces with texture work best."));
        return findings;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
            return 0;
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
