using System.Globalization;
using M2DPlus.Core.Bake;
using M2DPlus.Core.Placement;
using M2DPlus.Core.Styling;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class BakeTests
{
    private static readonly Guid Component = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Plane3 South = new(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ);

    // Owner decision 2026-11-16 (UX §8.1): four layouts; the line type is the leaf except in the classic layout.
    [Theory]
    [InlineData(BakeLayout.ViewLayer, "M2D+::Section A::Arch::Walls::Visible", "M2D+::Section A::Grasshopper (no layer)::Visible", 4)]
    [InlineData(BakeLayout.LayerView, "M2D+::Arch::Walls::Section A::Visible", "M2D+::Grasshopper (no layer)::Section A::Visible", 4)]
    [InlineData(BakeLayout.Layer, "M2D+::Arch::Walls::Visible", "M2D+::Grasshopper (no layer)::Visible", 3)]
    [InlineData(BakeLayout.ViewType, "M2D+::Section A::Visible::Arch::Walls", "M2D+::Section A::Visible", 2)]
    public void EachLayoutPlacesDrawingSourceLayersAndLineType(BakeLayout layout, string nested, string grasshopper, int styledFrom)
    {
        var path = BakeNames.Layer(layout, "Section A", LineClass.Visible, "Arch::Walls");
        Assert.Equal(nested, path.FullPath);
        Assert.Equal(styledFrom, path.StyledFrom);
        // Only the line-type level is styled, except classic, which styles the type layer and its source sublayers.
        Assert.Equal(layout == BakeLayout.ViewType ? [false, false, true, true, true] : Enumerable.Range(0, path.Segments.Length).Select(i => i == path.Segments.Length - 1),
            Enumerable.Range(0, path.Segments.Length).Select(path.IsStyled));
        Assert.Equal(grasshopper, BakeNames.Layer(layout, "Section A", LineClass.Visible).FullPath);
        Assert.Equal(grasshopper, BakeNames.Layer(layout, "Section A", LineClass.Visible, "").FullPath);
    }

    [Theory]
    [InlineData(LineClass.Visible, "Visible")]
    [InlineData(LineClass.Profile, "Visible")]
    [InlineData(LineClass.Hidden, "Hidden")]
    [InlineData(LineClass.Beyond, "Beyond")]
    [InlineData(LineClass.FarClip, "Far clip")]
    [InlineData(LineClass.Cut, "Cut")]
    [InlineData(LineClass.Hatch, "Hatch")]
    [InlineData(LineClass.Unresolved, "Unresolved")]
    [InlineData(LineClass.Ignored, "Ignored")]
    public void TheLineTypeIsAlwaysTheLeafAndDeepSourcePathsNest(LineClass lineClass, string type)
    {
        Assert.Equal($"M2D+::Section A::{type}::Arch::Walls", BakeNames.LayerPath("Section A", lineClass, "Arch::Walls"));
        Assert.Equal($"M2D+::Section A::{type}", BakeNames.LayerPath("Section A", lineClass));
        const string source = "Site::Building A::Level 2::Walls";
        Assert.Equal($"M2D+::Plan +3.00::Site::Building A::Level 2::Walls::{type}", BakeNames.Layer(BakeLayout.ViewLayer, "Plan +3.00", lineClass, source).FullPath);
        Assert.Equal($"M2D+::Site::Building A::Level 2::Walls::Plan +3.00::{type}", BakeNames.Layer(BakeLayout.LayerView, "Plan +3.00", lineClass, source).FullPath);
        Assert.Equal($"M2D+::Site::Building A::Level 2::Walls::{type}", BakeNames.Layer(BakeLayout.Layer, "Plan +3.00", lineClass, source).FullPath);
        Assert.Equal($"M2D+::Plan +3.00::{type}::Site::Building A::Level 2::Walls", BakeNames.Layer(BakeLayout.ViewType, "Plan +3.00", lineClass, source).FullPath);
        foreach (var layout in BakeNames.Layouts)
        {
            var path = BakeNames.Layer(layout, "Plan +3.00", lineClass, source);
            Assert.Equal("M2D+", path.Segments[0]);
            if (layout != BakeLayout.ViewType) Assert.Equal(type, path.Segments[^1]);
        }
        Assert.Equal(lineClass == LineClass.Profile ? LineClass.Visible : lineClass,
            BakeNames.ClassicCategory(BakeNames.LayerPath("Plan", lineClass, source)));
    }

    [Fact]
    public void TheLayerLayoutMergesDrawingsAndTheOthersKeepThemApart()
    {
        string P(BakeLayout l, string d) => BakeNames.Layer(l, d, LineClass.Cut, "Arch::Walls").FullPath;
        Assert.Equal(P(BakeLayout.Layer, "Section A"), P(BakeLayout.Layer, "Section B"));
        foreach (var layout in new[] { BakeLayout.ViewLayer, BakeLayout.LayerView, BakeLayout.ViewType })
            Assert.NotEqual(P(layout, "Section A"), P(layout, "Section B"));
        Assert.Throws<ArgumentException>(() => BakeNames.Layer(BakeLayout.Layer, "A::B", LineClass.Visible));
        Assert.Throws<ArgumentException>(() => BakeNames.Layer(BakeLayout.LayerView, "A", LineClass.Visible, "Arch::::Walls"));
        Assert.Throws<ArgumentException>(() => BakeNames.Layer(BakeLayout.ViewLayer, "A", LineClass.Visible, "Arch:: "));
        Assert.Null(BakeNames.ClassicCategory("M2D+::Section A"));
        Assert.Null(BakeNames.ClassicCategory("Other::Section A::Visible"));
        Assert.Null(BakeNames.ClassicCategory("M2D+::Arch::Walls::Visible"));
    }

    [Theory]
    [InlineData("view-layer", BakeLayout.ViewLayer)]
    [InlineData("Layer-View", BakeLayout.LayerView)]
    [InlineData(" layer ", BakeLayout.Layer)]
    [InlineData("VIEW-TYPE", BakeLayout.ViewType)]
    public void BakeSettingParsesEveryLayoutDefaultsToViewLayerAndIsNeverAVisibilityKey(string text, BakeLayout expected)
    {
        var view = new ViewSettingsContext("a", "A", ViewSourceKind.Plane, true, 20, .001);
        var resolved = Settings.Resolve([view], [[$"bake = {text}"]], DocumentUnits.Metres).Views[0];
        Assert.Equal(expected, resolved.Bake);
        Assert.Contains($"bake = {BakeNames.LayoutText(expected)} (set by input)", resolved.Describe());
        var plain = Settings.Resolve([view], [], DocumentUnits.Metres).Views[0];
        Assert.Equal(BakeLayout.ViewLayer, plain.Bake);
        Assert.Equal(plain.VisibilityKey(), resolved.VisibilityKey());
        Assert.DoesNotContain("bake", Settings.VisibilityKeys);
        var wrong = Settings.Resolve([view], [["bake = layers"]], DocumentUnits.Metres);
        Assert.Contains(wrong.Diagnostics, d => d.Key == "bake" && d.Message.Contains("Did you mean 'layer'?", StringComparison.Ordinal));
        Assert.Equal(BakeLayout.ViewLayer, wrong.Views[0].Bake);
    }

    [Fact]
    public void RunTextLeavesOutOnlyBakeLines()
    {
        Assert.True(Settings.IsBakeTimeLine("bake = layer"));
        Assert.True(Settings.IsBakeTimeLine("  BAKE=view-type   # classic"));
        Assert.False(Settings.IsBakeTimeLine("# bake = layer"));
        Assert.False(Settings.IsBakeTimeLine("[Section A]"));
        Assert.False(Settings.IsBakeTimeLine("name = bake = 2"));
        Assert.False(Settings.IsBakeTimeLine("baked = on"));
        Assert.Equal(Settings.RunText(["hidden = off", "[Section A]", "fade = on"]),
            Settings.RunText(["hidden = off", "bake = layer", "[Section A]", "bake=view-type", "fade = on"]));
        Assert.NotEqual(Settings.RunText(["hidden = off"]), Settings.RunText(["hidden = on"]));
        Assert.Equal(Settings.RunText(new SettingsRow([KeyValuePair.Create("scale", "1:50")])),
            Settings.RunText(new SettingsRow([KeyValuePair.Create("scale", "1:50"), KeyValuePair.Create("bake", "layer")])));
    }

    [Fact]
    public void OwnerMarkersAreTellableFromALayoutSetting()
    {
        Assert.True(BakeTags.TryParseOwner(BakeTags.Owner(Component, "Plan/Roof"), out var component, out var drawing));
        Assert.Equal(Component, component); Assert.Equal("Plan/Roof", drawing);
        Assert.False(BakeTags.TryParseOwner("layer", out _, out _));
        Assert.False(BakeTags.TryParseOwner("view-layer", out _, out _));
        Assert.False(BakeTags.TryParseOwner($"{Guid.Empty:D}/A", out _, out _));
        Assert.False(BakeTags.TryParseOwner($"{Component:D}/", out _, out _));
        Assert.False(BakeTags.TryParseOwner(null, out _, out _));
    }

    [Fact]
    public void CleanupRemovesOnlyEmptyUnchangedLayersM2DCreated()
    {
        var style = new BakeLayerStyle(-16777216, -16777216, .18, -1, -1).Signature;
        var empty = new BakeLayerState("M2D+::Section A::Visible::Arch", true, true, false, true, false, style, style);
        Assert.True(BakeLayerCleanup.Removable(empty));
        Assert.False(BakeLayerCleanup.Removable(empty with { CreatedByM2D = false }));
        Assert.False(BakeLayerCleanup.Removable(empty with { Empty = false }));
        Assert.False(BakeLayerCleanup.Removable(empty with { HasChildren = true }));
        Assert.False(BakeLayerCleanup.Removable(empty with { Visible = false }));
        Assert.False(BakeLayerCleanup.Removable(empty with { Locked = true }));
        Assert.False(BakeLayerCleanup.Removable(empty with { Signature = new BakeLayerStyle(-16777216, -16777216, .42, -1, -1).Signature }));
        Assert.False(BakeLayerCleanup.Removable(empty with { FullPath = "M2D+" }));
        Assert.False(BakeLayerCleanup.Removable(empty with { FullPath = "Arch::Walls" }));
        // Layers from beta.3 have no stored signature: compared with what the classic layout created, else kept.
        Assert.True(BakeLayerCleanup.Removable(empty with { StyleTag = null, ClassicSignature = style }));
        Assert.False(BakeLayerCleanup.Removable(empty with { StyleTag = null, ClassicSignature = null }));
        Assert.False(BakeLayerCleanup.Removable(empty with { StyleTag = null, ClassicSignature = new BakeLayerStyle(0, 0, .18, -1, -1).Signature }));
        Assert.NotEqual(style, new BakeLayerStyle(-16777216, -16777216, .18, 3, -1).Signature);
        Assert.NotEqual(style, new BakeLayerStyle(-16777216, -1, .18, -1, -1).Signature);
    }

    [Fact]
    public void DrawingNamesUseSourceOverrideAndGeometricDescriptions()
    {
        var plan = new DrawingNameRequest(ViewSourceKind.Rectangle, Plane3.WorldXY.AtOrigin(new(3, 4, 1.2)), true);
        Assert.Equal("Plan +1.20", BakeNames.Infer(plan, DocumentUnits.Metres));
        Assert.Equal("Section 3", BakeNames.Infer(new(ViewSourceKind.Plane, South, true, SectionNumber: 3), DocumentUnits.Metres));
        Assert.Equal("Elevation South", BakeNames.Infer(new(ViewSourceKind.Direction, South, false), DocumentUnits.Metres));
        var sw = AnchorRules.FromDirection(new(1, 1, -1), Vector3.Zero);
        Assert.Equal("Axo SW", BakeNames.Infer(new(ViewSourceKind.Vector, sw, false), DocumentUnits.Metres));
        Assert.Equal("Roof", BakeNames.Infer(plan with { Source = ViewSourceKind.NamedView, SourceName = "Roof" }, DocumentUnits.Metres));
        Assert.Equal("Section A", BakeNames.Infer(plan with { Source = ViewSourceKind.ClippingPlane, SourceName = "Section A" }, DocumentUnits.Metres));
        Assert.Equal("Custom", BakeNames.Infer(plan with { SourceName = "Roof", OverrideName = "Custom" }, DocumentUnits.Metres));
    }

    [Theory]
    [InlineData(1200, DocumentUnits.Millimetres, "+1200 mm")]
    [InlineData(120, DocumentUnits.Centimetres, "+120.00 cm")]
    [InlineData(-1.2, DocumentUnits.Metres, "-1.20")]
    [InlineData(4.125, DocumentUnits.Feet, "+4.13 ft")]
    [InlineData(6, DocumentUnits.Inches, "+6.00 in")]
    [InlineData(-.00001, DocumentUnits.Metres, "+0.00")]
    public void AutomaticPlanLevelIsUnitAwareAndCultureIndependent(double value, DocumentUnits units, string expected)
    {
        var old = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.Equal(expected, BakeNames.FormatElevation(value, units)); }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void DuplicatesAndAddCopyRespectReservedNamesAndCase()
    {
        Assert.Equal(new[] { "A", "a (2)", "A (3)", "B" }, BakeNames.Unique(["A", "a", "A", "B"]));
        Assert.Equal(new[] { "Section A (3)" }, BakeNames.Unique(["Section A"], ["Section A", "Section A (2)"]));
        Assert.Throws<ArgumentException>(() => BakeNames.LayerPath("A::B", LineClass.Visible));
        Assert.Throws<ArgumentException>(() => BakeNames.LayerPath("A", LineClass.Visible, "Arch::::Walls"));
    }

    [Theory]
    [InlineData(LineClass.FarClip, "farclip")]
    [InlineData(LineClass.Profile, "visible")]
    [InlineData(LineClass.Unresolved, "unresolved")]
    public void UserStringsCarryStableOwnershipSourceClassHashAndOriginalLayer(LineClass lineClass, string expected)
    {
        var tags = BakeTags.Create(Component, "Section A", "source-id", lineClass, "sha256", "layer-path");
        Assert.Equal($"{Component:D}/Section A", tags["m2d.bake"]);
        Assert.Equal("source-id", tags["m2d.source"]); Assert.Equal(expected, tags["m2d.class"]);
        Assert.Equal("sha256", tags["m2d.hash"]); Assert.Equal("layer-path", tags["m2d.layer"]);
    }

    [Fact]
    public void ReplaceDeletesOnlyUnchangedOwnedObjectsAndAddsNewCompleteDrawing()
    {
        var owner = BakeTags.Owner(Component, "Section A");
        var previous = new[]
        {
            new BakedRecord("unchanged", owner, "a", "a", "original", "original"),
            new BakedRecord("edited", owner, "b", "new", "original", "original"),
            new BakedRecord("moved", owner, "c", "c", "original", "custom"),
            new BakedRecord("missing-provenance", owner, "d", "d", "", "original"),
            new BakedRecord("other-drawing", BakeTags.Owner(Component, "Other"), "a", "a", "original", "original"),
            new BakedRecord("other-component", BakeTags.Owner(Guid.NewGuid(), "Section A"), "a", "a", "original", "original")
        };
        var fresh = new[] { new NewBakeRecord("new1", "layer", "hash", "source", LineClass.Visible),
            new NewBakeRecord("new2", "layer", "hash2", "source", LineClass.Cut) };
        var result = BakePlan.Replace(Component, "Section A", previous, fresh);
        Assert.Equal(new[] { "unchanged" }, result.Delete);
        Assert.Equal(new[] { "edited", "moved", "missing-provenance" }, result.Keep);
        Assert.Equal(fresh, result.Add);
        Assert.Equal("Baked Section A: 2 objects (replaced 1 · kept 3 you edited)", result.Summary);
        Assert.Equal("a", previous[0].CurrentHash);
        Assert.Empty(BakePlan.Replace(Component, "Section A (2)", previous, fresh).Delete);
    }

    [Fact]
    public void EmptyNewDrawingStillDeletesOldUneditedObjectsAndDuplicateRecordsFail()
    {
        var old = new BakedRecord("id", BakeTags.Owner(Component, "A"), "a", "a", "layer", "layer");
        var result = BakePlan.Replace(Component, "A", [old], []);
        Assert.Single(result.Delete); Assert.Empty(result.Add);
        Assert.Throws<ArgumentException>(() => BakePlan.Replace(Component, "A", [old, old], []));
        var fresh = new NewBakeRecord("id", "layer", "a", "source", LineClass.Visible);
        Assert.Throws<ArgumentException>(() => BakePlan.Replace(Component, "A", [], [fresh, fresh]));
    }

    [Fact]
    public void ReBakeUpdatesOnlyExactlyUnchangedM2DWidths()
    {
        var next = new LineWeightPreset(50);
        var unchanged = BakeLayerWidths.Decide(LineClass.Cut, .50, "1:100", next);
        Assert.True(unchanged.Update); Assert.Equal(.70, unchanged.Width); Assert.Equal("1:50", unchanged.WeightsTag);
        var edited = BakeLayerWidths.Decide(LineClass.Cut, .51, "1:100", next);
        Assert.False(edited.Update); Assert.Equal(.51, edited.Width); Assert.Contains("you edited", edited.Message);
        Assert.False(BakeLayerWidths.Decide(LineClass.Cut, double.BitIncrement(.50), "1:100", next).Update);
        Assert.False(BakeLayerWidths.Decide(LineClass.Cut, .50, null, next).Update);
        Assert.False(BakeLayerWidths.Decide(LineClass.Cut, .50, "junk", next).Update);
        Assert.False(BakeLayerWidths.Decide(LineClass.Cut, .50, "1:0", next).Update);
        Assert.False(BakeLayerWidths.Decide(LineClass.Cut, .50, "1:100", new(50, WeightMode.Layers)).Update);
        var created = BakeLayerWidths.Decide(LineClass.Visible, 0, null, next, true);
        Assert.True(created.Update); Assert.Equal(.25, created.Width);
        Assert.False(BakeLayerWidths.Decide(LineClass.Visible, .25, "1:50", next).Update);
    }
}
