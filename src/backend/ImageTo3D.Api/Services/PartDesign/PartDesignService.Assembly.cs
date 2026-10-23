using System.IO.Compression;
using System.Text.Json;
using ImageTo3D.Api.DTOs;
using ImageTo3D.Api.Models;
using ImageTo3D.Core.Geometry;
using ImageTo3D.Core.PartDesign;
using Microsoft.EntityFrameworkCore;

namespace ImageTo3D.Api.Services.PartDesign;

/// <summary>
/// Assembly: relations between parts (edited through the history like parts), connection
/// constraints (fit clearance against the project's limits), collision checks between every pair
/// of active parts with intentional-overlap exceptions, geometry quality, and release comparison.
/// </summary>
public sealed partial class PartDesignService
{
    // ---- Relations ----

    public async Task<PartDesignProjectDto> AddRelationAsync(Guid id, CreateRelationRequest request, CancellationToken cancellationToken)
    {
        var project = await LoadAsync(id, cancellationToken);
        var a = ActivePart(project, request.PartAId!.Value);
        var b = ActivePart(project, request.PartBId!.Value);
        if (a.Id == b.Id)
            throw new ApiException(StatusCodes.Status400BadRequest, "relation_self", "A part cannot be related to itself.");
        if (ActiveRelations(project).Any(r => SamePair(r, a.Id, b.Id)))
            throw new ApiException(StatusCodes.Status409Conflict, "relation_exists", "These two parts are already related; edit that relation instead.");
        if (project.Relations.Count >= options.Value.MaxRelationsPerProject)
            throw new ApiException(StatusCodes.Status409Conflict, "too_many_relations", $"A project can hold at most {options.Value.MaxRelationsPerProject} relations (including history).");

        var relation = new PartRelation
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            PartAId = a.Id,
            PartBId = b.Id,
            CreatedAt = time.GetUtcNow().UtcDateTime,
            Kind = request.Kind!,
            AllowOverlap = request.AllowOverlap,
            OpeningSizeMm = request.OpeningSizeMm,
            Note = Clean(request.Note),
        };
        project.Relations.Add(relation);
        db.PartRelations.Add(relation);
        await RecordAsync(project, "relate", $"Related '{Truncate(a.Name, 40)}' and '{Truncate(b.Name, 40)}' ({relation.Kind})", [], cancellationToken,
            [new RelationChange(relation.Id, null, Snapshot(relation))]);
        return ToDto(project);
    }

    public async Task<PartDesignProjectDto> UpdateRelationAsync(Guid id, Guid relationId, UpdateRelationRequest request, CancellationToken cancellationToken)
    {
        var project = await LoadAsync(id, cancellationToken);
        var relation = ActiveRelation(project, relationId);
        var before = Snapshot(relation);
        var after = before with { Kind = request.Kind!, AllowOverlap = request.AllowOverlap, OpeningSizeMm = request.OpeningSizeMm, Note = Clean(request.Note) };
        if (after == before)
            return ToDto(project);

        Apply(relation, after);
        await RecordAsync(project, "edit_relation", $"Edited relation {RelationLabel(project, relation)}", [], cancellationToken, [new RelationChange(relation.Id, before, after)]);
        return ToDto(project);
    }

    public async Task<PartDesignProjectDto> RemoveRelationAsync(Guid id, Guid relationId, CancellationToken cancellationToken)
    {
        var project = await LoadAsync(id, cancellationToken);
        var relation = ActiveRelation(project, relationId);
        var before = Snapshot(relation);
        var after = before with { IsRemoved = true };
        Apply(relation, after);
        await RecordAsync(project, "unrelate", $"Removed relation {RelationLabel(project, relation)}", [], cancellationToken, [new RelationChange(relation.Id, before, after)]);
        return ToDto(project);
    }

    private sealed record RelationSnapshot(string Kind, bool AllowOverlap, double? OpeningSizeMm, string? Note, bool IsRemoved);

    private sealed record RelationChange(Guid RelationId, RelationSnapshot? Before, RelationSnapshot After);

    private static RelationSnapshot Snapshot(PartRelation r) => new(r.Kind, r.AllowOverlap, r.OpeningSizeMm, r.Note, r.IsRemoved);

    private static void Apply(PartRelation r, RelationSnapshot s)
    {
        r.Kind = s.Kind;
        r.AllowOverlap = s.AllowOverlap;
        r.OpeningSizeMm = s.OpeningSizeMm;
        r.Note = s.Note;
        r.IsRemoved = s.IsRemoved;
    }

    private static List<RelationChange> RelationChanges(PartOperation operation) =>
        string.IsNullOrEmpty(operation.RelationChangesJson) ? [] : JsonSerializer.Deserialize<List<RelationChange>>(operation.RelationChangesJson, Json) ?? [];

    /// <summary>Marks the part's relations removed and returns the changes, to record with the operation that removes the part.</summary>
    private static List<RelationChange> DetachRelations(PartDesignProject project, Guid partId)
    {
        var changes = new List<RelationChange>();
        foreach (var relation in ActiveRelations(project).Where(r => r.PartAId == partId || r.PartBId == partId).ToList())
        {
            var before = Snapshot(relation);
            var after = before with { IsRemoved = true };
            Apply(relation, after);
            changes.Add(new RelationChange(relation.Id, before, after));
        }

        return changes;
    }

    private void DeleteRelation(PartDesignProject project, PartRelation relation)
    {
        project.Relations.Remove(relation);
        db.PartRelations.Remove(relation);
    }

    private static IEnumerable<PartRelation> ActiveRelations(PartDesignProject project) => project.Relations.Where(r => !r.IsRemoved);

    private static bool SamePair(PartRelation r, Guid a, Guid b) => (r.PartAId == a && r.PartBId == b) || (r.PartAId == b && r.PartBId == a);

    private static PartRelation ActiveRelation(PartDesignProject project, Guid relationId)
    {
        var relation = project.Relations.FirstOrDefault(r => r.Id == relationId)
                       ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Relation not found.");
        if (relation.IsRemoved)
            throw new ApiException(StatusCodes.Status409Conflict, "relation_not_active", "This relation has been removed; undo to restore it.");
        return relation;
    }

    private static string RelationLabel(PartDesignProject project, PartRelation r)
    {
        string Name(Guid partId) => Truncate(project.Parts.FirstOrDefault(p => p.Id == partId)?.Name ?? "?", 40);
        return $"'{Name(r.PartAId)}' – '{Name(r.PartBId)}'";
    }

    private List<PartRelationDto> RelationDtos(PartDesignProject project) =>
        ActiveRelations(project).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Select(r => RelationDto(r, project)).ToList();

    private static PartRelationDto RelationDto(PartRelation r, PartDesignProject project)
    {
        var issues = new List<IssueDto>();
        var a = project.Parts.FirstOrDefault(p => p.Id == r.PartAId);
        var b = project.Parts.FirstOrDefault(p => p.Id == r.PartBId);
        FitDto fit;
        if (a?.State != DesignPartState.Active || b?.State != DesignPartState.Active)
        {
            issues.Add(new IssueDto("relation_part_inactive", "warning", "One of the related parts has been deleted or split; remove this relation or undo.", "assembly"));
            fit = new FitDto("unknown", null, null, null, null);
        }
        else
        {
            var result = FitCheck.Check(r.Kind, FitPartOf(a), FitPartOf(b), r.OpeningSizeMm, project.FitAllowanceMinMm, project.FitAllowanceMaxMm);
            issues.AddRange(result.Issues.Select(i => IssueDto.From(i, "assembly")));
            fit = new FitDto(result.Status, result.OpeningMm, result.InsertedMm, result.Clearance, result.Basis);
        }

        return new PartRelationDto(r.Id, r.PartAId, r.PartBId, r.Kind, r.AllowOverlap, r.OpeningSizeMm, r.Note, fit, new ValidationDto(StatusOf(issues), issues));
    }

    /// <summary>The user's sizes first, the model's estimates otherwise.</summary>
    private static FitPart FitPartOf(DesignPart p)
    {
        static FitSize? Size(double? specified, double? estimated) =>
            specified is { } s ? new FitSize(s, true) : estimated is { } e && e > 0 ? new FitSize(e, false) : null;
        var outer = p.Category == PartCategories.Tube ? p.OuterDiameterMm : p.DiameterMm ?? p.OuterDiameterMm;
        return new FitPart(
            p.Category,
            Size(outer, p.EstimatedOuterDiameterMm),
            Size(p.InnerDiameterMm, p.EstimatedInnerDiameterMm),
            Size(p.ThicknessMm, p.EstimatedThicknessMm));
    }

    private static string StatusOf(IEnumerable<IssueDto> issues)
    {
        var list = issues as IReadOnlyCollection<IssueDto> ?? issues.ToList();
        return list.Any(i => i.Severity == "error") ? "invalid" : list.Any(i => i.Severity == "warning") ? "warning" : "valid";
    }

    // ---- Assembly check ----

    public async Task<AssemblyReportDto> AssemblyReportAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = await LoadAsync(id, cancellationToken);
        return await BuildAssemblyAsync(project, new Dictionary<string, Mesh>(StringComparer.Ordinal), cancellationToken);
    }

    /// <summary>Relations, collisions and quality of the active parts; <paramref name="meshes"/> caches part meshes (mm) by geometry file.</summary>
    private async Task<AssemblyReportDto> BuildAssemblyAsync(PartDesignProject project, Dictionary<string, Mesh> meshes, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var tolerance = o.ContactToleranceMm;
        var parts = project.Parts.Where(p => p.State == DesignPartState.Active).OrderBy(p => p.SortOrder).ToList();
        var relations = RelationDtos(project);
        var reports = parts.ToDictionary(p => p.Id, p => JsonSerializer.Deserialize<GeometryReportDto>(p.GeometryReportJson, Json)!);
        var issues = new List<IssueDto>();
        var collisions = new List<CollisionDto>();
        var trees = new Dictionary<string, TriangleBvh>(StringComparer.Ordinal);
        long budget = o.MaxCollisionTriangleTests;
        long tests = 0;
        var pairs = 0;
        var complete = true;

        async Task<TriangleBvh> TreeAsync(DesignPart part)
        {
            if (trees.TryGetValue(part.GeometryFile, out var tree))
                return tree;
            if (!meshes.TryGetValue(part.GeometryFile, out var mesh))
                meshes[part.GeometryFile] = mesh = await LoadPartMeshAsync(project, part, cancellationToken);
            return trees[part.GeometryFile] = new TriangleBvh(mesh);
        }

        PartRelationDto? RelationFor(Guid a, Guid b) => relations.FirstOrDefault(r => (r.PartAId == a && r.PartBId == b) || (r.PartAId == b && r.PartBId == a));

        for (var i = 0; i < parts.Count; i++)
        {
            for (var j = i + 1; j < parts.Count; j++)
            {
                var (a, b) = (parts[i], parts[j]);
                var relation = RelationFor(a.Id, b.Id);
                var allowed = relation is { AllowOverlap: true } || relation?.Kind == RelationKinds.Nested;
                var glued = relation?.Kind == RelationKinds.Glued;
                var near = BoxesMeet(reports[a.Id].Bounds, reports[b.Id].Bounds, tolerance);
                if (!near)
                {
                    if (glued)
                        collisions.Add(Collision(a, b, "gap", null, "glued_not_touching", "warning", "Glued, but the parts do not touch: move one or check the relation.", relation, null));
                    continue;
                }

                pairs++;
                if (a.GeometryFile == b.GeometryFile)
                {
                    // An unmoved duplicate: the same solid twice in the same place.
                    collisions.Add(allowed
                        ? Collision(a, b, "coincident", null, "overlap_allowed", "info", "Same geometry in the same place; the relation allows the overlap.", relation, null)
                        : Collision(a, b, "coincident", null, "duplicate_in_place", "warning",
                            "Both parts are the same geometry in the same place. If this is one part made twice, raise its quantity instead; otherwise move it or allow the overlap.", relation, null));
                    continue;
                }

                if (tests >= budget)
                {
                    complete = false;
                    continue;
                }

                var result = MeshIntersection.Compare(await TreeAsync(a), await TreeAsync(b), tolerance, reports[a.Id].Closed, reports[b.Id].Closed, budget - tests);
                tests += result.TriangleTests;
                complete &= result.Complete;
                var bounds = result.OverlapBounds is { } box ? new BoundsDto(V(box.Min), V(box.Max), V(box.Size)) : null;
                switch (result.Contact)
                {
                    case MeshContact.Intersecting:
                        collisions.Add(allowed
                            ? Collision(a, b, "intersecting", null, "overlap_allowed", "info", "The parts intersect; the relation allows the overlap.", relation, result, bounds)
                            : Collision(a, b, "intersecting", null, "parts_intersect", "warning",
                                $"The parts intersect (intersection curve {result.IntersectionLength:0.#} mm). Move one, split it, or relate them with the overlap allowed if it is intended.", relation, result, bounds));
                        break;
                    case MeshContact.Contained:
                        var inner = result.ContainedPart == "a" ? a : b;
                        collisions.Add(allowed
                            ? Collision(a, b, "contained", inner.Id, "overlap_allowed", "info", $"'{Truncate(inner.Name, 40)}' is inside the other part; the relation allows it.", relation, result, bounds)
                            : Collision(a, b, "contained", inner.Id, "part_contained", "warning",
                                $"'{Truncate(inner.Name, 40)}' lies entirely inside the other part. Relate them as nested if that is intended.", relation, result, bounds));
                        break;
                    case MeshContact.Touching:
                        collisions.Add(glued
                            ? Collision(a, b, "touching", null, "glued_contact", "info", "Glued faces touch.", relation, result, null)
                            : Collision(a, b, "touching", null, "parts_touch", "info", "The parts touch.", relation, result, null));
                        break;
                    default:
                        if (glued)
                            collisions.Add(Collision(a, b, "gap", null, "glued_not_touching", "warning", "Glued, but the parts do not touch: move one or check the relation.", relation, result, null));
                        break;
                }

                if (!result.ContainmentChecked && result.Contact is MeshContact.Clear or MeshContact.Touching && BoxInside(reports[a.Id].Bounds, reports[b.Id].Bounds, reports[b.Id].Bounds, reports[a.Id].Bounds))
                {
                    issues.Add(new IssueDto("containment_not_checked", "info",
                        $"'{Truncate(a.Name, 40)}' and '{Truncate(b.Name, 40)}': one is open (not a closed solid), so whether one lies inside the other was not checked.", "assembly"));
                }
            }
        }

        if (!complete)
        {
            issues.Add(new IssueDto("collision_check_incomplete", "warning",
                $"The collision check stopped after {o.MaxCollisionTriangleTests:N0} triangle tests; some pairs were not checked. Delete fragment parts or split large parts to check the rest.", "assembly"));
        }

        var quality = Quality(project, parts, reports, collisions);
        var all = issues.Concat(relations.SelectMany(r => r.Validation.Issues)).Concat(collisions.Select(c => new IssueDto(c.Code, c.Severity, c.Message, "assembly")));
        var status = Worst(StatusOf(all), quality.Status);
        return new AssemblyReportDto(status, Utc(time.GetUtcNow().UtcDateTime), HistorySequence(project), tolerance, pairs, tests, complete,
            relations, collisions, quality, issues, "mm", CoordinateSystem);
    }

    private static CollisionDto Collision(DesignPart a, DesignPart b, string contact, Guid? contained, string code, string severity, string message, PartRelationDto? relation, MeshContactResult? result, BoundsDto? bounds = null) =>
        new(a.Id, b.Id, contact, contained, code, severity, message, relation?.Id, Round(result?.IntersectionLength ?? 0), result?.CrossingPairs ?? 0, bounds, result?.Complete ?? true);

    private static Vector3Dto V(Vector3D v) => new(Round(v.X), Round(v.Y), Round(v.Z));

    private static bool BoxesMeet(BoundsDto a, BoundsDto b, double margin) =>
        a.Min.X <= b.Max.X + margin && b.Min.X <= a.Max.X + margin &&
        a.Min.Y <= b.Max.Y + margin && b.Min.Y <= a.Max.Y + margin &&
        a.Min.Z <= b.Max.Z + margin && b.Min.Z <= a.Max.Z + margin;

    /// <summary>Either box inside the other.</summary>
    private static bool BoxInside(BoundsDto inner1, BoundsDto outer1, BoundsDto inner2, BoundsDto outer2)
    {
        static bool Inside(BoundsDto i, BoundsDto o) =>
            i.Min.X >= o.Min.X && i.Min.Y >= o.Min.Y && i.Min.Z >= o.Min.Z && i.Max.X <= o.Max.X && i.Max.Y <= o.Max.Y && i.Max.Z <= o.Max.Z;
        return Inside(inner1, outer1) || Inside(inner2, outer2);
    }

    private static string Worst(string a, string b)
    {
        static int Rank(string s) => s switch { "invalid" => 2, "warning" => 1, _ => 0 };
        return Rank(a) >= Rank(b) ? a : b;
    }

    private static QualityReportDto Quality(PartDesignProject project, List<DesignPart> parts, Dictionary<Guid, GeometryReportDto> reports, List<CollisionDto> collisions)
    {
        var rows = parts.Select(p =>
        {
            var g = reports[p.Id];
            var validation = PartDto(p, project).Validation;
            var ready = g.Closed && g.NonManifoldEdges == 0 && g.InconsistentEdges == 0 && g.DegenerateTriangles == 0;
            var collisionCount = collisions.Count(c => c.Severity != "info" && (c.PartAId == p.Id || c.PartBId == p.Id));
            return new PartQualityDto(p.Id, p.Name, validation.Status, g.Closed, g.BoundaryEdges, g.NonManifoldEdges, g.InconsistentEdges, g.DegenerateTriangles,
                g.Components, g.Triangles, g.VolumeMm3, ready, collisionCount,
                validation.Issues.Where(i => i.Severity != "info").Select(i => i.Code).Distinct().ToList());
        }).ToList();

        var volumes = rows.Select(r => r.VolumeMm3).ToList();
        return new QualityReportDto(
            rows.Aggregate("valid", (s, r) => Worst(s, r.Status)),
            rows.Count,
            rows.Count(r => r.Status == "valid"),
            rows.Count(r => r.Status == "warning"),
            rows.Count(r => r.Status == "invalid"),
            rows.Count(r => r.SolidExportReady),
            rows.Sum(r => r.Triangles),
            volumes.Count > 0 && volumes.All(v => v is not null) ? Round(volumes.Sum(v => v!.Value)) : null,
            rows);
    }

    // ---- Release comparison ----

    private const string AssemblyFile = "metadata/assembly.json";

    public async Task<ReleaseDiffDto> CompareReleasesAsync(Guid id, int from, int to, CancellationToken cancellationToken)
    {
        await EnsureProjectAsync(id, cancellationToken);
        var before = await ReadReleaseAsync(id, from, cancellationToken);
        var after = await ReadReleaseAsync(id, to, cancellationToken);
        var notes = new List<string>();

        static string? Geometry(ExportManifestDto m, ManifestPartDto p) =>
            p.GeometrySha256 ?? m.Files.FirstOrDefault(f => f.PartId == p.Id && f.Kind == "stl")?.Sha256;
        static ReleasePartRefDto Ref(ManifestPartDto p) => new(p.Id, p.Name, p.Category, p.Quantity);

        var oldParts = before.Manifest.Parts.ToDictionary(p => p.Id);
        var newParts = after.Manifest.Parts.ToDictionary(p => p.Id);
        var specCompared = before.Manifest.Parts.All(p => p.Spec is not null) && after.Manifest.Parts.All(p => p.Spec is not null);
        if (!specCompared)
            notes.Add("A release made before part specifications were recorded is involved: material, sizes and other spec fields were not compared.");

        var changed = new List<ChangedPartDto>();
        var unchanged = 0;
        foreach (var (partId, now) in newParts)
        {
            if (!oldParts.TryGetValue(partId, out var was))
                continue;
            var changes = new List<string>();
            if (was.Name != now.Name) changes.Add("name");
            if (was.Category != now.Category) changes.Add("category");
            if (was.Quantity != now.Quantity) changes.Add("quantity");
            if (was.ReviewStatus != now.ReviewStatus) changes.Add("reviewStatus");
            if (was.ValidationStatus != now.ValidationStatus) changes.Add("validationStatus");
            if (Geometry(before.Manifest, was) != Geometry(after.Manifest, now)) changes.Add("geometry");
            if (specCompared)
            {
                // Quantity is compared above already.
                foreach (var property in typeof(ManufacturingSpecDto).GetProperties().Where(p => p.Name != nameof(ManufacturingSpecDto.Quantity)))
                {
                    if (!Equals(property.GetValue(was.Spec), property.GetValue(now.Spec)))
                        changes.Add("spec." + JsonNamingPolicy.CamelCase.ConvertName(property.Name));
                }
            }

            if (changes.Count == 0)
                unchanged++;
            else
                changed.Add(new ChangedPartDto(partId, now.Name, changes));
        }

        var relationsCompared = before.Assembly is not null && after.Assembly is not null;
        int added = 0, removed = 0, relationsChanged = 0;
        if (relationsCompared)
        {
            var oldRelations = before.Assembly!.Relations.ToDictionary(r => r.Id);
            var newRelations = after.Assembly!.Relations.ToDictionary(r => r.Id);
            added = newRelations.Keys.Count(k => !oldRelations.ContainsKey(k));
            removed = oldRelations.Keys.Count(k => !newRelations.ContainsKey(k));
            relationsChanged = newRelations.Count(kv => oldRelations.TryGetValue(kv.Key, out var r) &&
                (r.Kind, r.AllowOverlap, r.OpeningSizeMm, r.Note) != (kv.Value.Kind, kv.Value.AllowOverlap, kv.Value.OpeningSizeMm, kv.Value.Note));
        }
        else
        {
            notes.Add("A release made before assembly checks existed is involved: relations were not compared.");
        }

        return new ReleaseDiffDto(
            from,
            to,
            after.Manifest.Parts.Where(p => !oldParts.ContainsKey(p.Id)).Select(Ref).ToList(),
            before.Manifest.Parts.Where(p => !newParts.ContainsKey(p.Id)).Select(Ref).ToList(),
            changed,
            unchanged,
            specCompared,
            relationsCompared,
            added,
            removed,
            relationsChanged,
            before.Manifest.Warnings.Count,
            after.Manifest.Warnings.Count,
            notes);
    }

    private async Task<(ExportManifestDto Manifest, AssemblyReportDto? Assembly)> ReadReleaseAsync(Guid id, int revision, CancellationToken cancellationToken)
    {
        var release = await db.DesignReleases.AsNoTracking().FirstOrDefaultAsync(r => r.ProjectId == id && r.Revision == revision, cancellationToken)
                      ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", $"Release {revision} not found.");
        var zipBytes = await store.ReadAllAsync(id, release.PackageFile, cancellationToken)
                       ?? throw new ApiException(StatusCodes.Status410Gone, "artifact_missing", $"The package of release {revision} is not available.");
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        T? Read<T>(string path) where T : class
        {
            var entry = zip.GetEntry(path);
            if (entry is null)
                return null;
            using var stream = entry.Open();
            return JsonSerializer.Deserialize<T>(stream, ManifestJson);
        }

        var manifest = Read<ExportManifestDto>("manifest.json")
                       ?? throw new ApiException(StatusCodes.Status422UnprocessableEntity, "manifest_missing", $"Release {revision} has no manifest.");
        return (manifest, Read<AssemblyReportDto>(AssemblyFile));
    }
}
