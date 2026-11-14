using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Core.Bake;
using M2DPlus.Core.Sections;
using M2DPlus.Core.Styling;
using M2DPlus.Rhino.Sections;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

public sealed record BakeDrawing(ViewDrawing Drawing, string Name);
public sealed record BakeResult(IReadOnlyList<Guid> Added, IReadOnlyList<string> Messages);

/// <summary>Host-only mutation. A batch, including replacement deletes and pattern/layer insertion, is one undo.</summary>
public static class DrawingBake
{
    private sealed record Item(GeometryBase Geometry, string Source, SourceSection Style, LineClass Class,
        double Intensity, bool Profile = false, SectionStyleSnapshot? Section = null) : IDisposable
    { public void Dispose() => Geometry.Dispose(); }

    public static Color Blend(Color ink, Color background, double intensity) => Color.FromArgb(
        (int)Math.Round(ink.R * intensity + background.R * (1 - intensity)),
        (int)Math.Round(ink.G * intensity + background.G * (1 - intensity)),
        (int)Math.Round(ink.B * intensity + background.B * (1 - intensity)));

    public static IReadOnlyList<string> Names(RhinoDoc doc) => doc.Layers.Where(l => !l.IsDeleted &&
        l.FullPath.StartsWith("M2D+::", StringComparison.Ordinal)).Select(l => l.FullPath.Split("::"))
        .Where(p => p.Length >= 2).Select(p => p[1]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static BakeResult Bake(RhinoDoc doc, Guid component, IReadOnlyList<BakeDrawing> drawings)
    {
        var added = new List<Guid>(); var messages = new List<string>();
        var locked = new Dictionary<Guid, bool>(); var hidden = new HashSet<string>(StringComparer.Ordinal);
        var serial = doc.BeginUndoRecord($"M2D+ Bake {drawings.Count} drawings");
        try
        {
            foreach (var drawing in drawings)
            {
                if (drawing.Drawing.Error is not null) { messages.Add($"Skipped {drawing.Name}: {drawing.Drawing.Error}"); continue; }
                var items = new List<Item>();
                try
                {
                    foreach (var p in drawing.Drawing.Items)
                        items.Add(new(p.CopyGeometry(), p.SourceIdentity, p.Source, p.Class, p.Intensity, p.Profile));
                    foreach (var r in drawing.Drawing.Regions)
                    {
                        var region = r.Region; var source = string.Join("|", region.SourceIds);
                        if (region.Style.Boundary.Visible)
                            foreach (var loop in region.Loops) items.Add(new(loop.Curve.DuplicateCurve(), source, r.Source, LineClass.Cut, 1, Section: region.Style));
                        if (region.Hatch is { } hatch) items.Add(new(hatch.Duplicate(), source, r.Source, LineClass.Hatch, 1, Section: region.Style));
                    }
                    var owner = BakeTags.Owner(component, drawing.Name);
                    var previous = doc.Objects.GetObjectList(new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = true })
                        .Where(o => o.Attributes.GetUserString(BakeTags.Bake) == owner).ToArray();
                    var records = previous.Select(o => new BakedRecord(o.Id.ToString(), owner,
                        o.Attributes.GetUserString(BakeTags.Hash) ?? "", Fingerprint(o.Geometry, o.Attributes),
                        o.Attributes.GetUserString(BakeTags.Layer) ?? "", doc.Layers[o.Attributes.LayerIndex].Id.ToString())).ToArray();
                    var pending = new List<(Item Item, ObjectAttributes Attributes, string Hash, string Layer)>();
                    try
                    {
                        foreach (var item in items)
                        {
                            var weights = drawing.Drawing.Weights;
                            var path = BakeNames.LayerPath(drawing.Name, item.Class, item.Style.LayerPath);
                            var layer = EnsureLayer(path, item.Class, weights, item.Style);
                            UnlockAncestors(layer);
                            var attributes = new ObjectAttributes { LayerIndex = layer.Index };
                            pending.Add((item, attributes, "", layer.Id.ToString()));
                            var kind = item.Profile && item.Class == LineClass.Visible ? LineClass.Profile : item.Class;
                            var intensity = ViewRange.QuantizeForBake(item.Intensity) * weights[item.Class].InkIntensity;
                            if (weights.UseSourceLayers || intensity < 1 || item.Class == LineClass.Unresolved)
                            {
                                var ink = item.Class == LineClass.Unresolved ? Color.FromArgb(224, 122, 0) : weights.UseSourceLayers ? Argb(item.Style.LayerColour) : layer.Color;
                                var printInk = item.Class == LineClass.Unresolved || weights.UseSourceLayers ? ink : attributes.ComputedPlotColor(doc);
                                attributes.ColorSource = ObjectColorSource.ColorFromObject;
                                attributes.PlotColorSource = ObjectPlotColorSource.PlotColorFromObject;
                                attributes.ObjectColor = Blend(ink, Color.White, intensity);
                                attributes.PlotColor = Blend(printInk, Color.White, intensity);
                            }
                            if (item.Profile || weights.UseSourceLayers || item.Section is not null)
                            {
                                attributes.PlotWeightSource = ObjectPlotWeightSource.PlotWeightFromObject;
                                attributes.PlotWeight = weights.PrintWidth(kind, item.Style.LayerWidth, item.Section?.Boundary.WidthScale ?? 1);
                            }
                            if (item.Section is { } style)
                            {
                                attributes.ColorSource = ObjectColorSource.ColorFromObject;
                                attributes.PlotColorSource = ObjectPlotColorSource.PlotColorFromObject;
                                attributes.ObjectColor = Argb(item.Class == LineClass.Cut ? style.Boundary.Colour : style.Pattern is null ? style.BackgroundColour : style.PatternColour);
                                attributes.PlotColor = Argb(item.Class == LineClass.Cut ? style.Boundary.PrintColour : style.Pattern is null ? style.BackgroundPrintColour : style.PatternPrintColour);
                                if (item.Geometry is Hatch h)
                                {
                                    h.PatternIndex = Pattern(style.Pattern ?? new("Solid", true, Array.Empty<PatternLine>()));
                                    if (style.Background != BackgroundMode.None) attributes.HatchBackgroundFillColor = Argb(style.BackgroundColour);
                                    attributes.SetUserString("M2D.SectionStyle", JsonSerializer.Serialize(style));
                                }
                                else if (style.Boundary.LinetypeSegments.Count != 0)
                                {
                                    attributes.LinetypeSource = ObjectLinetypeSource.LinetypeFromObject;
                                    attributes.LinetypeIndex = Linetype("M2D+ " + style.Boundary.LinetypeName, style.Boundary.LinetypeSegments);
                                }
                            }
                            var hash = Fingerprint(item.Geometry, attributes);
                            pending[^1] = (item, attributes, hash, layer.Id.ToString());
                        }
                        var plan = BakePlan.Replace(component, drawing.Name, records, pending.Select((p, i) =>
                            new NewBakeRecord(i.ToString(System.Globalization.CultureInfo.InvariantCulture), p.Layer, p.Hash, p.Item.Source, p.Item.Class)));
                        foreach (var id in plan.Delete)
                        {
                            var obj = doc.Objects.FindId(Guid.Parse(id));
                            if (obj is null) continue;
                            UnlockAncestors(doc.Layers[obj.Attributes.LayerIndex]);
                            if (!doc.Objects.Delete(obj, true)) throw new InvalidOperationException($"Could not replace baked object {id}.");
                        }
                        foreach (var p in pending)
                        {
                            foreach (var tag in BakeTags.Create(component, drawing.Name, p.Item.Source, p.Item.Class, p.Hash, p.Layer)) p.Attributes.SetUserString(tag.Key, tag.Value);
                            var id = doc.Objects.Add(p.Item.Geometry, p.Attributes);
                            if (id == Guid.Empty) throw new InvalidOperationException("Rhino refused a baked object.");
                            added.Add(id);
                        }
                        messages.Add(plan.Summary);
                    }
                    finally { foreach (var p in pending) p.Attributes.Dispose(); }
                    // Only remove empty, explicitly owned descendants. Preserve the drawing/category layers.
                    foreach (var layer in doc.Layers.Where(l => !l.IsDeleted && l.GetUserString("m2d.created") == component.ToString() &&
                        l.FullPath.StartsWith($"M2D+::{drawing.Name}::", StringComparison.Ordinal) && l.FullPath.Split("::").Length > 3)
                        .OrderByDescending(l => l.FullPath.Split("::").Length).ToArray())
                        if (!doc.Objects.FindByLayer(layer).Any() && !doc.Layers.Any(l => !l.IsDeleted && l.ParentLayerId == layer.Id)) doc.Layers.Delete(layer.Index, true);
                }
                finally { foreach (var item in items) item.Dispose(); }
            }
        }
        finally
        {
            foreach (var pair in locked)
            {
                var layer = doc.Layers.FindId(pair.Key);
                if (layer is null || layer.IsDeleted) continue;
                using var copy = Copy(layer); copy.IsLocked = true; copy.IsVisible = pair.Value; doc.Layers.Modify(copy, layer.Index, true);
            }
            if (serial != 0) doc.EndUndoRecord(serial);
        }
        if (hidden.Count > 0) messages.Add("Hidden layers kept hidden: " + string.Join(", ", hidden.Order(StringComparer.Ordinal)));
        doc.Views.Redraw();
        return new(added, messages.Distinct().ToArray());

        int Linetype(string name, IReadOnlyList<double> segments)
        {
            var found = doc.Linetypes.FindName(name);
            if (found is not null) return found.Index;
            using var type = new Linetype { Name = name };
            foreach (var segment in segments) type.AppendSegment(Math.Abs(segment), segment >= 0);
            return doc.Linetypes.Add(type);
        }
        Layer EnsureLayer(string path, LineClass kind, LineWeightPreset weights, SourceSection source)
        {
            var parts = path.Split("::"); var parent = Guid.Empty; var full = "";
            for (var i = 0; i < parts.Length; i++)
            {
                full += (i == 0 ? "" : "::") + parts[i];
                var index = doc.Layers.FindByFullPath(full, -1); var isNew = index < 0;
                if (isNew)
                {
                    using var layer = new Layer { Name = parts[i], ParentLayerId = parent,
                        Color = kind == LineClass.Unresolved ? Color.FromArgb(224, 122, 0) : weights.UseSourceLayers ? Argb(source.LayerColour) : Color.Black };
                    layer.SetUserString("m2d.created", component.ToString());
                    if (i >= 2)
                    {
                        layer.PlotWeight = weights.UseSourceLayers ? source.LayerWidth : weights[kind].PrintWidthMillimetres;
                        layer.SetUserString(BakeTags.Weights, $"1:{weights.Scale:R}");
                        if (kind == LineClass.Hidden) layer.LinetypeIndex = Linetype("Hidden", new[] { 2d, -1d });
                    }
                    index = doc.Layers.Add(layer);
                    if (index < 0) throw new InvalidOperationException($"Could not create layer {full}.");
                }
                var actual = doc.Layers[index]; parent = actual.Id;
                if (i >= 2 && !isNew)
                {
                    var decision = BakeLayerWidths.Decide(kind, actual.PlotWeight, actual.GetUserString(BakeTags.Weights), weights);
                    if (decision.Update)
                    {
                        using var copy = Copy(actual); copy.PlotWeight = decision.Width; copy.SetUserString(BakeTags.Weights, decision.WeightsTag);
                        doc.Layers.Modify(copy, index, true);
                    }
                    else if (decision.Message.Contains("edited", StringComparison.Ordinal)) messages.Add(full + ": " + decision.Message);
                }
            }
            return doc.Layers.FindId(parent);
        }
        void UnlockAncestors(Layer layer)
        {
            while (layer is not null)
            {
                if (!layer.IsVisible) hidden.Add(layer.FullPath);
                if (layer.IsLocked)
                {
                    locked.TryAdd(layer.Id, layer.IsVisible); using var copy = Copy(layer); copy.IsLocked = false; copy.IsVisible = locked[layer.Id]; doc.Layers.Modify(copy, layer.Index, true);
                }
                if (layer.ParentLayerId == Guid.Empty) break;
                layer = doc.Layers.FindId(layer.ParentLayerId);
            }
        }
        int Pattern(PatternDefinition definition)
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(definition))));
            var found = doc.HatchPatterns.FirstOrDefault(p => !p.IsDeleted && p.GetUserString("m2d.pattern") == key);
            if (found is not null) return found.Index;
            using var pattern = SectionHatches.CreatePattern(definition);
            pattern.Name = "M2D+ " + definition.Name + " " + key[..12]; pattern.SetUserString("m2d.pattern", key);
            var index = doc.HatchPatterns.Add(pattern);
            if (index < 0) throw new InvalidOperationException("Could not add hatch pattern.");
            return index;
        }
    }
    private static Layer Copy(Layer layer) => (Layer)global::Rhino.Runtime.CommonObject.FromJSON(layer.ToJSON(new global::Rhino.FileIO.SerializationOptions()));
    private static Color Argb(uint colour) => Color.FromArgb(unchecked((int)colour));
    // Include editable appearance as well as geometry. Bake provenance itself is excluded.
    private static string Fingerprint(GeometryBase geometry, ObjectAttributes a) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        GeometryFingerprint.Compute(geometry) + JsonSerializer.Serialize(new { a.ColorSource, a.ObjectColor, a.PlotColorSource, a.PlotColor,
            a.PlotWeightSource, a.PlotWeight, a.LinetypeSource, a.LinetypeIndex, a.HatchBackgroundFillColor, a.Name, a.Mode }))));
}
