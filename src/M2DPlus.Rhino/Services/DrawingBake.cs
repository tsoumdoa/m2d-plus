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

/// <param name="Layout">The bake layer layout (UX §8.1); null takes the drawing's resolved <c>bake</c> setting.</param>
public sealed record BakeDrawing(ViewDrawing Drawing, string Name, BakeLayout? Layout = null)
{
    public BakeLayout EffectiveLayout => Layout ?? Drawing.Settings?.Bake ?? BakeLayout.ViewLayer;
}
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

    /// <summary>Model objects a bake may own (never block-definition geometry).</summary>
    private static ObjectEnumeratorSettings Baked() => new() { NormalObjects = true, LockedObjects = true, HiddenObjects = true };
    /// <summary>Everything that keeps a layer in use, for the emptied-layer check.</summary>
    private static ObjectEnumeratorSettings Everything(int layer) => new()
    {
        NormalObjects = true, LockedObjects = true, HiddenObjects = true, IdefObjects = true, ReferenceObjects = true,
        IncludeLights = true, IncludeGrips = false, LayerIndexFilter = layer
    };

    /// <summary>Drawing names already baked in this document by any component, read from the bake markers: the drawing is
    /// not at a fixed layer level in every layout (UX §8.1).</summary>
    public static IReadOnlyList<string> Names(RhinoDoc doc) => doc.Objects.GetObjectList(Baked())
        .Select(o => BakeTags.TryParseOwner(o.Attributes.GetUserString(BakeTags.Bake), out _, out var drawing) ? drawing : null)
        .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The editable style compared to tell an unchanged M2D+ layer from one the user changed.</summary>
    public static BakeLayerStyle Style(Layer layer) => new(layer.Color.ToArgb(), layer.PlotColor.ToArgb(), layer.PlotWeight,
        layer.LinetypeIndex, layer.RenderMaterialIndex);

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
                    var layout = drawing.EffectiveLayout;
                    // The previous bake of this drawing is found by its markers wherever it is, so a bake with another
                    // layout replaces it as well (UX §8.2).
                    var previous = doc.Objects.GetObjectList(Baked())
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
                            var path = BakeNames.Layer(layout, drawing.Name, item.Class, item.Style.LayerPath);
                            var layer = EnsureLayer(path, layout, item.Class, weights, item.Style);
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
                        var emptied = new HashSet<Guid>();
                        foreach (var id in plan.Delete)
                        {
                            var obj = doc.Objects.FindId(Guid.Parse(id));
                            if (obj is null) continue;
                            emptied.Add(doc.Layers[obj.Attributes.LayerIndex].Id);
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
                        var removed = RemoveEmptied(emptied);
                        if (removed > 0) messages.Add($"{drawing.Name}: removed {removed} empty M2D+ layer{(removed == 1 ? "" : "s")}");
                    }
                    finally { foreach (var p in pending) p.Attributes.Dispose(); }
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
            // Rhino 8 returns a stock linetype with index -1 for a name not in this document's table: add it then.
            var found = doc.Linetypes.FindName(name);
            if (found is not null && found.Index >= 0) return found.Index;
            using var type = new Linetype { Name = name };
            foreach (var segment in segments) type.AppendSegment(Math.Abs(segment), segment >= 0);
            return doc.Linetypes.Add(type);
        }
        // Creates the missing levels of a bake layer. Styled levels (the line type, UX §8.1) get the preset width, the Hidden
        // linetype and m2d.weights; grouping levels stay plain. Each created layer records its style (m2d.style), so a later
        // cleanup can tell it from a layer the user changed.
        Layer EnsureLayer(BakeLayerPath path, BakeLayout layout, LineClass kind, LineWeightPreset weights, SourceSection source)
        {
            var parent = Guid.Empty; var full = "";
            for (var i = 0; i < path.Segments.Length; i++)
            {
                full += (i == 0 ? "" : "::") + path.Segments[i];
                var styled = path.IsStyled(i);
                var index = doc.Layers.FindByFullPath(full, -1); var isNew = index < 0;
                if (isNew)
                {
                    using var layer = NewLayer(path.Segments[i], parent, styled, layout, kind, weights, source);
                    layer.SetUserString(BakeLayerStyle.Created, component.ToString());
                    layer.SetUserString(BakeLayerStyle.Tag, Style(layer).Signature);
                    index = doc.Layers.Add(layer);
                    if (index < 0) throw new InvalidOperationException($"Could not create layer {full}.");
                    // Record the style as the document holds it, in case Rhino normalised a value on Add.
                    var stored = doc.Layers[index];
                    if (stored.GetUserString(BakeLayerStyle.Tag) != Style(stored).Signature)
                    {
                        using var copy = Copy(stored); copy.SetUserString(BakeLayerStyle.Tag, Style(stored).Signature); doc.Layers.Modify(copy, index, true);
                    }
                }
                var actual = doc.Layers[index]; parent = actual.Id;
                if (styled && !isNew)
                {
                    var decision = BakeLayerWidths.Decide(kind, actual.PlotWeight, actual.GetUserString(BakeTags.Weights), weights);
                    if (decision.Update)
                    {
                        // Keep the signature in step with M2D+'s own width change, unless the user had already changed the layer.
                        var unchanged = actual.GetUserString(BakeLayerStyle.Tag) == Style(actual).Signature;
                        using var copy = Copy(actual); copy.PlotWeight = decision.Width; copy.SetUserString(BakeTags.Weights, decision.WeightsTag);
                        if (unchanged) copy.SetUserString(BakeLayerStyle.Tag, Style(copy).Signature);
                        doc.Layers.Modify(copy, index, true);
                    }
                    else if (decision.Message.Contains("edited", StringComparison.Ordinal)) messages.Add(full + ": " + decision.Message);
                }
            }
            return doc.Layers.FindId(parent);
        }
        // A layer as M2D+ creates it. The classic layout keeps beta.3's colours on every level it creates; the others colour
        // only the styled (line type) level, so grouping layers stay plain.
        Layer NewLayer(string name, Guid parent, bool styled, BakeLayout layout, LineClass kind, LineWeightPreset weights, SourceSection? source)
        {
            var coloured = styled || layout == BakeLayout.ViewType;
            var layer = new Layer
            {
                Name = name, ParentLayerId = parent,
                Color = coloured && kind == LineClass.Unresolved ? Color.FromArgb(224, 122, 0)
                    : coloured && weights.UseSourceLayers && source is not null ? Argb(source.LayerColour) : Color.Black
            };
            if (styled)
            {
                layer.PlotWeight = weights.UseSourceLayers && source is not null ? source.LayerWidth : weights[kind].PrintWidthMillimetres;
                layer.SetUserString(BakeTags.Weights, $"1:{weights.Scale:R}");
                if (kind == LineClass.Hidden) layer.LinetypeIndex = Linetype("Hidden", new[] { 2d, -1d });
            }
            return layer;
        }
        // Removes the layers a re-bake emptied, then their emptied parents, when M2D+ created them and the user has not
        // changed them (BakeLayerCleanup). Layers from beta.3 and earlier have no m2d.style: they are compared with the style
        // the classic layout created them with.
        int RemoveEmptied(HashSet<Guid> emptied)
        {
            var removed = 0;
            var candidates = new Dictionary<Guid, int>();
            foreach (var id in emptied)
                for (var layer = doc.Layers.FindId(id); layer is not null && !layer.IsDeleted;
                     layer = layer.ParentLayerId == Guid.Empty ? null : doc.Layers.FindId(layer.ParentLayerId))
                    candidates.TryAdd(layer.Id, layer.FullPath.Split("::").Length);
            foreach (var id in candidates.OrderByDescending(c => c.Value).Select(c => c.Key))
            {
                var layer = doc.Layers.FindId(id);
                if (layer is null || layer.IsDeleted) continue;
                var tag = layer.GetUserString(BakeLayerStyle.Tag);
                var state = new BakeLayerState(layer.FullPath, layer.GetUserString(BakeLayerStyle.Created) is not null,
                    !doc.Objects.GetObjectList(Everything(layer.Index)).Any(),
                    doc.Layers.Any(l => !l.IsDeleted && l.ParentLayerId == layer.Id), layer.IsVisible,
                    layer.IsLocked || locked.ContainsKey(layer.Id), tag, Style(layer).Signature, tag is null ? Classic(layer) : null);
                if (BakeLayerCleanup.Removable(state) && doc.Layers.Delete(layer.Index, true)) removed++;
            }
            return removed;
        }
        // The style the classic layout (beta.3) created a layer with, assuming weights = auto; null when it cannot tell.
        string? Classic(Layer layer)
        {
            var tag = layer.GetUserString(BakeTags.Weights);
            if (BakeNames.ClassicCategory(layer.FullPath) is { } kind)
            {
                if (tag?.StartsWith("1:", StringComparison.Ordinal) != true ||
                    !double.TryParse(tag[2..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale) ||
                    !double.IsFinite(scale) || scale <= 0) return null;
                using var styled = NewLayer(layer.Name, Guid.Empty, true, BakeLayout.ViewType, kind, new LineWeightPreset(scale), null);
                return Style(styled).Signature;
            }
            if (tag is not null) return null;
            using var plain = NewLayer(layer.Name, Guid.Empty, false, BakeLayout.ViewType, LineClass.Visible, new LineWeightPreset(100), null);
            return Style(plain).Signature;
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
