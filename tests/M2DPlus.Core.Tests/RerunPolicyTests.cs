using M2DPlus.Core;
using M2DPlus.Core.Watch;
using Xunit;

namespace M2DPlus.Core.Tests;

/// <summary>The re-run trigger table (UX §5.1) and the warning severity policy (UX §5.2).</summary>
public sealed class RerunPolicyTests
{
    private static readonly WatchMode Doc = WatchMode.Document, Ref = WatchMode.Referenced;

    [Theory]
    // Referenced Geometry (GH geometry or referenced objects): visibility never re-runs, content does.
    [InlineData(ObjectChange.Visibility, false, true)]
    [InlineData(ObjectChange.ViewportVisibility, false, false)]
    [InlineData(ObjectChange.Other, false, false)]
    [InlineData(ObjectChange.Geometry, true, true)]
    [InlineData(ObjectChange.Deleted, true, true)]
    [InlineData(ObjectChange.Added, true, true)]
    [InlineData(ObjectChange.Layer, true, true)]
    [InlineData(ObjectChange.Colour, true, true)]
    [InlineData(ObjectChange.Section, true, true)]
    [InlineData(ObjectChange.Material, true, true)]
    [InlineData(ObjectChange.Name, true, true)]
    [InlineData(ObjectChange.M2DText, true, true)]
    [InlineData(ObjectChange.Visibility | ObjectChange.ViewportVisibility, false, true)]
    public void DrawnObjectChanges(ObjectChange change, bool referenced, bool document)
    {
        Assert.Equal(referenced, RerunPolicy.Object(Ref, ObjectRole.Scene, change));
        Assert.Equal(document, RerunPolicy.Object(Doc, ObjectRole.Scene, change));
    }

    [Fact]
    public void UnrelatedObjectsNeverReRun()
    {
        foreach (var change in Enum.GetValues<ObjectChange>())
        {
            Assert.False(RerunPolicy.Object(Ref, ObjectRole.Unrelated, change));
            Assert.False(RerunPolicy.Object(Doc, ObjectRole.Unrelated, change));
        }
    }

    [Fact]
    public void CandidatesAlwaysReRunAndGatedSourcesFollowTheirVisibility()
    {
        Assert.True(RerunPolicy.Object(Doc, ObjectRole.Candidate, ObjectChange.Visibility));
        Assert.True(RerunPolicy.Object(Ref, ObjectRole.Candidate, ObjectChange.Added));
        Assert.False(RerunPolicy.Object(Doc, ObjectRole.Candidate, ObjectChange.None));
        // A block part, or an object a layer name selects, is drawn only while visible: in both modes.
        Assert.True(RerunPolicy.Object(Ref, ObjectRole.Gated, ObjectChange.Visibility));
        Assert.False(RerunPolicy.Object(Ref, ObjectRole.Gated, ObjectChange.ViewportVisibility));
    }

    [Fact]
    public void ViewObjectsReRunForTheirSettingsAndShapeOnly()
    {
        Assert.True(RerunPolicy.Object(Ref, ObjectRole.View, ObjectChange.M2DText));
        Assert.True(RerunPolicy.Object(Ref, ObjectRole.View, ObjectChange.Geometry));
        Assert.True(RerunPolicy.Object(Doc, ObjectRole.View, ObjectChange.Name));
        Assert.False(RerunPolicy.Object(Doc, ObjectRole.View, ObjectChange.Visibility));
        Assert.False(RerunPolicy.Object(Doc, ObjectRole.View, ObjectChange.Colour));
        Assert.False(RerunPolicy.Object(Ref, ObjectRole.View, ObjectChange.Layer));
    }

    [Fact]
    public void LayerChanges()
    {
        // Document mode: a layer holding drawable objects is a gate; with Geometry given only block-part layers are.
        Assert.True(RerunPolicy.Layer(LayerRole.Gate, LayerChange.Visibility));
        Assert.False(RerunPolicy.Layer(LayerRole.Used, LayerChange.Visibility));
        Assert.False(RerunPolicy.Layer(LayerRole.Used | LayerRole.Gate | LayerRole.Pattern, LayerChange.ViewportVisibility));
        Assert.False(RerunPolicy.Layer(LayerRole.Used | LayerRole.Gate, LayerChange.Other));
        Assert.True(RerunPolicy.Layer(LayerRole.Used, LayerChange.Colour));
        Assert.True(RerunPolicy.Layer(LayerRole.Used, LayerChange.Section));
        Assert.True(RerunPolicy.Layer(LayerRole.Used, LayerChange.Print));
        Assert.False(RerunPolicy.Layer(LayerRole.None, LayerChange.Colour | LayerChange.Name | LayerChange.Visibility));
        Assert.True(RerunPolicy.Layer(LayerRole.Pattern, LayerChange.Added));
        Assert.True(RerunPolicy.Layer(LayerRole.Pattern, LayerChange.Name));
        Assert.False(RerunPolicy.Layer(LayerRole.Gate, LayerChange.Added));
    }

    [Fact]
    public void TableMatchesThePolicyItDocuments()
    {
        var rows = RerunPolicy.Table.ToDictionary(r => r.Change);
        Assert.Equal((false, true), (rows["Object hidden or shown (Hide / Show)"].Referenced, rows["Object hidden or shown (Hide / Show)"].Document));
        Assert.Equal((false, false), (rows["Per-viewport / per-detail visibility"].Referenced, rows["Per-viewport / per-detail visibility"].Document));
        Assert.Equal((false, true), (rows["Layer on / off"].Referenced, rows["Layer on / off"].Document));
        Assert.Equal((false, false), (rows["Layer on / off in one viewport or detail"].Referenced, rows["Layer on / off in one viewport or detail"].Document));
        Assert.True(rows["Geometry modified, moved, deleted or undeleted"].Referenced);
    }

    private static ViewOutcome Outcome(int unresolved = 0, bool quarantined = false, Dictionary<string, int>? occluding = null,
        Dictionary<string, int>? harmless = null, string[]? missing = null, string? risk = null, string? error = null) =>
        new("Section A", error, unresolved, quarantined, occluding ?? new(), harmless ?? new(), missing ?? [], risk);

    [Fact]
    public void ANormalRunIsNeverAWarning()
    {
        var notices = NoticePolicy.For(Outcome(unresolved: 12, harmless: new() { ["TextDot"] = 3 }, risk: "Detail may be lost…"));
        Assert.Equal(3, notices.Count);
        Assert.All(notices, n => Assert.Equal(NoticeLevel.Remark, n.Level));
        Assert.Contains(notices, n => n.Text.Contains("12 unresolved pieces") && n.Text.Contains("Unresolved output"));
        Assert.Equal(NoticeLevel.Remark, NoticePolicy.Worst(notices));
        Assert.Empty(NoticePolicy.For(Outcome()));
        Assert.Null(NoticePolicy.Worst([]));
    }

    [Fact]
    public void WarningsAreForThingsTheUserMustAct()
    {
        var quarantined = NoticePolicy.For(Outcome(unresolved: 400, quarantined: true, occluding: new() { ["PointCloud mesh"] = 2 }));
        Assert.Equal(2, quarantined.Count(n => n.Level == NoticeLevel.Warning));
        // The quarantine warning says it all: no separate unresolved remark.
        Assert.DoesNotContain(quarantined, n => n.Text.Contains("unresolved piece"));
        var missing = Assert.Single(NoticePolicy.For(Outcome(missing: ["Wals"])));
        Assert.Equal(NoticeLevel.Warning, missing.Level); Assert.Contains("'Wals'", missing.Text);
        var error = Assert.Single(NoticePolicy.For(Outcome(error: "no clipping plane named X", unresolved: 3)));
        Assert.Equal(NoticeLevel.Error, error.Level);
        Assert.Equal("Section A: 1 unresolved piece in the Unresolved output (orange in the preview, own layer when baked). Check them before issuing.",
            Assert.Single(NoticePolicy.For(Outcome(unresolved: 1))).Text);
    }
}
