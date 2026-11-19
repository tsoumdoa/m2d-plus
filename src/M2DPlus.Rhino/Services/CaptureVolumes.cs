using M2DPlus.Core;
using M2DPlus.Core.Placement;
using M2DPlus.Core.Views;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

/// <summary>Host capture's conservative union. Geometry/style capture is unnecessary for a rejected box.</summary>
internal sealed class CaptureVolumes
{
    private sealed record Volume(ViewSourceVolume Bounds, CutFrame? Cut, double Tolerance);
    private readonly Volume[] _volumes;
    internal string Revision { get; }
    internal bool Enabled => _volumes.Length > 0;

    internal CaptureVolumes(IReadOnlyList<ViewRequest> views, BoundingBox scene, double tolerance)
    {
        var volumes = new List<Volume>();
        foreach (var input in views)
        {
            var view = ViewSourceCull.ResolveView(input, scene, tolerance);
            // Only authored boxes and rectangles with explicit depth are independent of
            // scene bounds. Named cameras move to the scene front even with a crop.
            if (view.Error is not null || input.SceneDerivedFrame || input.Kind is not (ViewSourceKind.Box or ViewSourceKind.Rectangle) ||
                view.Cut is not { Crop: not null, SourceDepth: >= 0 } ||
                !double.IsFinite(view.Cut.SourceDepth.Value) ||
                input.Settings?["depth"].AuthoredValue is DistanceSetting { Mode: DistanceMode.Auto } &&
                    (input.Kind != ViewSourceKind.Box || input.Cut?.SourceDepth is null))
            { volumes.Clear(); break; }
            var frame = view.Resolve(scene, view.Settings?.Tolerance ?? tolerance);
            var crop = view.Cut?.Crop ?? (view.Named is { Crop: true } ? frame : (Rectangle3d?)null);
            var depth = view.Cut is { FarClip: not FarClipMode.None, SourceDepth: >= 0 } cut ? cut.SourceDepth : null;
            var plane = frame.Plane;
            if (!plane.IsValid || crop is { IsValid: false }) { volumes.Clear(); break; }
            ViewSourceVolume volume;
            // Core's plane/frame checks are stricter than Rhino's IsValid: a frame they reject keeps the whole scene.
            try
            {
                volume = new ViewSourceVolume(new(new(plane.OriginX, plane.OriginY, plane.OriginZ),
                    new(plane.XAxis.X, plane.XAxis.Y, plane.XAxis.Z), new(plane.YAxis.X, plane.YAxis.Y, plane.YAxis.Z)),
                    crop is { } c ? new Frame2(c.X.Min, c.Y.Min, c.X.Max, c.Y.Max) : null, depth);
            }
            catch (ArgumentException) { volumes.Clear(); break; }
            if (volume.IsUnbounded) { volumes.Clear(); break; }
            volumes.Add(new(volume, view.Cut, view.Settings?.Tolerance ?? tolerance));
        }
        _volumes = volumes.ToArray();
        Revision = Enabled ? WorkerCodec.Hash(WorkerCodec.Encode(new
        {
            Volumes = _volumes.Select(v => new
            {
                v.Bounds, v.Tolerance, v.Cut?.ScopeEnabled, v.Cut?.Exclusion, v.Cut?.Objects, v.Cut?.Layers
            }).ToArray(),
            // Authored auto and explicit zero must never share a narrowed capture key.
            FarSettings = views.Select(v => new
            {
                ClipsFar = v.Settings is { FarClip: not FarClipMode.None },
                Depth = v.Settings is { FarClip: not FarClipMode.None } s ? (double?)s.Depth : null,
                AuthoredDepth = v.Settings is { FarClip: not FarClipMode.None } authored ? authored["depth"].Text : null
            }).ToArray()
        })) : "unbounded";
    }

    // A wholly retained instance needs no per-leaf tests. Requiring the strictest
    // (participating) volume here is safe for every descendant participation scope.
    internal bool Contains(BoundingBox world) => world.IsValid && _volumes.Any(v => world.GetCorners().All(p =>
    {
        var local = v.Bounds.Plane.Coordinates(new(p.X, p.Y, p.Z));
        return v.Bounds.Crop is { } c && local.X >= c.MinX && local.X <= c.MaxX && local.Y >= c.MinY && local.Y <= c.MaxY &&
            local.Z <= 0 && (v.Bounds.Depth is not { } depth || local.Z >= -depth);
    }));

    internal bool Outside(BoundingBox world, SourceSection? parent, Guid? objectId, Guid? layerId, bool instance)
    {
        if (!Enabled || !world.IsValid) return false;
        var bounds = new Bounds3(new(world.Min.X, world.Min.Y, world.Min.Z), new(world.Max.X, world.Max.Y, world.Max.Z));
        foreach (var v in _volumes)
        {
            // A selected ancestor in an inclusion scope makes all its descendants
            // participate. Otherwise a scoped instance may contain mixed leaves:
            // test only its profile, then use each leaf's complete participation chain.
            var participates = true;
            if (v.Cut is { ScopeEnabled: true } cut)
            {
                var selected = objectId is { } id && cut.Objects.Contains(id) || layerId is { } layer && cut.Layers.Contains(layer) ||
                    parent?.ObjectIds.Any(cut.Objects.Contains) == true || parent?.LayerIds.Any(cut.Layers.Contains) == true;
                participates = instance ? selected && !cut.Exclusion : selected != cut.Exclusion;
            }
            if (!v.Bounds.Outside(bounds, v.Tolerance, participates)) return false;
        }
        return true;
    }
}
