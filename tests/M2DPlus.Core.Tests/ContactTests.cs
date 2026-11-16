using M2DPlus.Core.Visibility;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class ContactTests
{
    private static PlanarFace Face(string source, params Vertex[] points) => new([points]) { Source = source, OutwardNormal = new(0, 0, 1) };
    private static (PlanarFace[] Faces, CandidateEdge[] Edges) Panels(double z = 0) =>
        ([Face("A", new(0,0,0), new(2,0,0), new(2,1,0), new(0,1,0)),
          Face("B", new(0,0,z), new(0,-1,z), new(2,-1,z), new(2,0,z))],
         [new(new(0,0,0), new(2,0,0), "A") { AdjacentFaces = [0] }]);

    [Fact] public void FlushPanelsAreVisibleByDefaultAndDroppedOnlyWithJointsOff()
    {
        var (faces, edges) = Panels();
        var on = EdgeFaceKernel.Solve(faces, edges, new(.001));
        Assert.Equal(ContactKind.Flush, Assert.Single(on.Contacts).Kind);
        Assert.Equal(VisibilityClass.Visible, Assert.Single(on.Intervals).Class);
        Assert.Empty(EdgeFaceKernel.Solve(faces, edges, new(.001) { Joints = false }).Intervals);
    }

    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void CompleteHiddenCoverageStopsBudgetedOcclusionButStillFindsFlushContacts(bool joints, bool flush)
    {
        var (panels, edges) = Panels();
        var faces = new List<PlanarFace> { panels[0] };
        for (int i = 1; i <= 10; i++) faces.Add(Face("slab-" + i, new(-1,-1,i), new(3,-1,i), new(3,2,i), new(-1,2,i)));
        if (flush) faces.Add(panels[1]);
        var result = EdgeFaceKernel.Solve(faces, edges, new(.001) { Joints=joints, MaxFacesPerEdge=4 });
        Assert.Equal(1, result.FaceTests);
        if (!joints && flush) Assert.Empty(result.Intervals);
        else Assert.Equal(VisibilityClass.Hidden, Assert.Single(result.Intervals).Class);
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("budget exceeded", StringComparison.Ordinal));
    }

    [Theory] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void MissingOutwardOrientationNeverFallsBackToWindingForFlush(bool ownMissing, bool foreignMissing)
    {
        var (faces, edges) = Panels();
        if (ownMissing) faces[0] = faces[0] with { OutwardNormal=null };
        if (foreignMissing) faces[1] = faces[1] with { OutwardNormal=null };
        var result = EdgeFaceKernel.Solve(faces, edges, new(.001) { Joints=false });
        Assert.Equal(VisibilityClass.Visible, Assert.Single(result.Intervals).Class);
        Assert.Equal(ContactKind.Seated, Assert.Single(result.Contacts).Kind);
        Assert.False(new ExactVisibilityOracle.AllPairsPoints(faces,.001).IsFlushContact(edges[0],.5,1));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ApproximateAndExactFlushInOneCellCannotDropOrPromoteUncertainty(bool joints)
    {
        var (faces, edges) = Panels();
        var approximate = Panels(.0005).Faces[1] with { Source="C" };
        var result = EdgeFaceKernel.Solve([..faces, approximate], edges, new(.001) { Joints=joints });
        Assert.Contains(result.Contacts, c => c.Kind==ContactKind.Flush);
        Assert.Contains(result.Contacts, c => c.Kind==ContactKind.Approximate);
        Assert.Equal(VisibilityClass.Unresolved, Assert.Single(result.Intervals).Class);
        var occluder = Face("opaque", new(-1,-1,1),new(3,-1,1),new(3,2,1),new(-1,2,1));
        result = EdgeFaceKernel.Solve([..faces, approximate, occluder], edges, new(.001) { Joints=joints });
        Assert.All(result.Intervals, i => Assert.Equal(VisibilityClass.Hidden,i.Class));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void WorldContactPlanarityCannotCauseAnUnrelatedWholeViewFailure(bool near)
    {
        var (faces, edges) = Panels();
        var z = near ? 0 : 10;
        faces[1] = faces[1] with { WorldLoops=[[new(0,0,z),new(0,-1,z),new(2,-1,z),new(2,0,z+1)]], WorldOutwardNormal=new(0,0,1) };
        edges[0] = edges[0] with { WorldA=edges[0].A,WorldB=edges[0].B };
        var result = EdgeFaceKernel.Solve(faces, edges, new(.001));
        Assert.Equal(near ? VisibilityClass.Unresolved : VisibilityClass.Visible, Assert.Single(result.Intervals).Class);
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("Nonplanar face",StringComparison.Ordinal));
        if (!near) Assert.Empty(result.Contacts);
    }

    [Fact] public void ExactFarFromOriginContactsRetainTheSameRuleAndDiscloseSameSourceTies()
    {
        var (faces, edges) = Panels();
        Vertex Move(Vertex p) => new(p.X+5e6,p.Y+5e6,p.Z+5e6);
        faces=faces.Select(f=>f with {Loops=f.Loops.Select(l=>l.Select(Move).ToArray()).ToArray()}).ToArray();
        edges=edges.Select(e=>e with {A=Move(e.A),B=Move(e.B)}).ToArray();
        var result=EdgeFaceKernel.Solve(faces,edges,new(.001) {Joints=false});
        Assert.Empty(result.Intervals);Assert.Equal(ContactKind.Flush,Assert.Single(result.Contacts).Kind);
        faces[1]=faces[1] with {Source="A"};
        result=EdgeFaceKernel.Solve(faces,edges,new(.001) {Joints=false});
        Assert.Equal(VisibilityClass.Visible,Assert.Single(result.Intervals).Class);Assert.Empty(result.Contacts);
        Assert.Contains(result.Diagnostics,d=>d.Contains("Same-source/self-face depth ties retain the tolerance rule",StringComparison.Ordinal));
    }

    [Theory] [InlineData("exact", false, false)] [InlineData("exact", true, true)]
    [InlineData("fast", false, false)] [InlineData("fast", true, true)]
    public void JointsKeysOnlyActivePlanarVisibility(string engine, bool cutOnly, bool equal)
    {
        EffectiveSettings Resolve(bool joints) => Settings.Resolve([new("v","v",ViewSourceKind.Direction,false,10,.001)],
            [[$"engine={engine}",$"cutonly={(cutOnly ? "on" : "off")}",$"joints={(joints ? "on" : "off")}"]],DocumentUnits.Metres).Views[0];
        Assert.Equal(equal,Resolve(true).VisibilityKey()==Resolve(false).VisibilityKey());
    }

    [Fact] public void WallOnSlabIsSeatedEvenWithACoplanarBottomFaceAndStillOccludesNormally()
    {
        PlanarFace[] faces = [
            Face("wall", new(0,0,0),new(2,0,0),new(2,1,0),new(0,1,0)) with { OutwardNormal = new(0,0,-1) },
            Face("wall", new(0,0,0),new(2,0,0),new(2,0,2),new(0,0,2)) with { OutwardNormal = new(0,-1,0) },
            Face("slab", new(-1,-1,0),new(3,-1,0),new(3,2,0),new(-1,2,0))];
        CandidateEdge[] edges = [new(new(0,0,0),new(2,0,0),"wall") { AdjacentFaces = [0,1] }];
        var result = EdgeFaceKernel.Solve(faces,edges,new(.001) { Joints = false });
        Assert.All(result.Contacts, c => Assert.Equal(ContactKind.Seated,c.Kind));
        Assert.Equal(VisibilityClass.Visible,Assert.Single(result.Intervals).Class);
        result = EdgeFaceKernel.Solve([..faces,Face("occluder",new(.5,-1,1),new(1.5,-1,1),new(1.5,1,1),new(.5,1,1))],edges,new(.001));
        Assert.Equal(new[]{VisibilityClass.Visible,VisibilityClass.Hidden,VisibilityClass.Visible},result.Intervals.Select(i=>i.Class));
    }

    [Fact] public void CoincidentBlocksAndFoldedFacesAreNotFlushContinuations()
    {
        var (faces,edges)=Panels(); faces[1]=faces[0] with { Source="B" };
        var result=EdgeFaceKernel.Solve(faces,edges,new(.001) { Joints=false });
        Assert.Equal(ContactKind.Seated,Assert.Single(result.Contacts).Kind);
        Assert.Equal(VisibilityClass.Visible,Assert.Single(result.Intervals).Class);
    }

    [Fact] public void AnExactAngledFaceIsSeatedEvenWhenItIsThinnerThanTolerance()
    {
        PlanarFace[] faces=[Face("A",new(0,0,0),new(2,0,0),new(0,.00001,.00001)) with {OutwardNormal=new(0,-1,1)},
            Face("B",new(-1,-1,0),new(3,-1,0),new(3,2,0),new(-1,2,0))];
        CandidateEdge[] edges=[new(new(0,0,0),new(2,0,0),"A") {AdjacentFaces=[0]}];
        var result=EdgeFaceKernel.Solve(faces,edges,new(.001) {Joints=false});
        Assert.Equal(ContactKind.Seated,Assert.Single(result.Contacts).Kind);
        Assert.Equal(VisibilityClass.Visible,Assert.Single(result.Intervals).Class);
    }

    [Fact] public void TJunctionSplitsAtExactOverlapAndPreservesTheRemainder()
    {
        var (faces,edges)=Panels();
        faces[1]=Face("B",new(.5,0,0),new(.5,-1,0),new(1.5,-1,0),new(1.5,0,0));
        var result=EdgeFaceKernel.Solve(faces,edges,new(.001) { Joints=false });
        Assert.Equal(new[]{(0d,.25),(.75,1d)},result.Intervals.Select(i=>(i.Start,i.End)));
    }

    [Fact] public void NonDyadicOverlapEndpointsRetainTheirDrawingErrorBudget()
    {
        var (faces,edges)=Panels();
        edges=[edges[0] with { B=new(3,0,0) }];
        faces[0]=Face("A",new(0,0,0),new(3,0,0),new(3,1,0),new(0,1,0));
        faces[1]=Face("B",new(1,0,0),new(1,-1,0),new(2,-1,0),new(2,0,0));
        var result=EdgeFaceKernel.Solve(faces,edges,new(.001) { Joints=false });
        Assert.Equal(new[]{(0d,1d/3),(2d/3,1d)},result.Intervals.Select(i=>(i.Start,i.End)));
    }

    [Fact] public void ATransverseCrossingHasNoPositiveLengthContactInterval()
    {
        PlanarFace[] faces=[Face("A",new(0,0,0),new(3,0,0),new(3,1,0),new(0,1,0)),
            Face("B",new(1,-1,-1),new(1,2,-1),new(1,2,1),new(1,-1,1)) with {OutwardNormal=new(1,0,0)}];
        CandidateEdge[] edges=[new(new(0,0,0),new(3,0,0),"A") {AdjacentFaces=[0]}];
        var result=EdgeFaceKernel.Solve(faces,edges,new(.001));
        Assert.Empty(result.Contacts);
        Assert.DoesNotContain(result.Intervals,i=>i.Class!=VisibilityClass.Visible && i.Start<.3);
        Assert.DoesNotContain(result.Contacts,c=>c.Start<.3||c.End>.4);
    }

    [Theory] [InlineData(.0005,VisibilityClass.Unresolved)] [InlineData(.001,VisibilityClass.Unresolved)]
    [InlineData(.00101,VisibilityClass.Hidden)] [InlineData(-.00101,VisibilityClass.Visible)]
    public void NearFlushIsUncertainInsideToleranceAndOrdinaryOcclusionOutside(double gap, VisibilityClass expected)
    {
        var (faces,edges)=Panels(gap);
        var result=EdgeFaceKernel.Solve(faces,edges,new(.001) { Joints=false });
        Assert.Equal(expected,Assert.Single(result.Intervals).Class);
    }

    [Theory] [InlineData(1,0)] [InlineData(1000,0)] [InlineData(1,1)] [InlineData(1000,2)]
    public void UnitsAndExactRotationsPreserveTheContactRule(double scale,int rotation)
    {
        var (faces,edges)=Panels();
        Vertex Map(Vertex p)=>rotation switch { 1=>new(p.X*scale,p.Z*scale,-p.Y*scale),2=>new(p.Z*scale,p.X*scale,p.Y*scale),_=>new(p.X*scale,p.Y*scale,p.Z*scale) };
        faces=faces.Select(f=>f with{Loops=f.Loops.Select(l=>l.Select(Map).ToArray()).ToArray(),OutwardNormal=Map(f.OutwardNormal!.Value)}).ToArray();
        edges=edges.Select(e=>e with{A=Map(e.A),B=Map(e.B)}).ToArray();
        var indexed=EdgeFaceKernel.Solve(faces,edges,new(.001*scale,4) { Joints=false });
        var allPairs=EdgeFaceKernel.Solve(faces,edges,new(.001*scale,1,false) { Joints=false });
        Assert.Equal(allPairs.Contacts,indexed.Contacts); Assert.Equal(allPairs.Intervals,indexed.Intervals);
        Assert.Empty(indexed.Intervals); Assert.Equal(ContactKind.Flush,Assert.Single(indexed.Contacts).Kind);
    }

    [Fact] public void JointsUsesNormalPrecedenceVisibilityCacheAndWorkerRoundTrip()
    {
        var view=new ViewSettingsContext("v","v",ViewSourceKind.Direction,false,10,.001);
        EffectiveSettings Resolve(string[] input,EditorSettings? editor=null)=>Settings.Resolve([view],[input],DocumentUnits.Metres,editor).Views[0];
        var defaults=Resolve([]);Assert.True(defaults.Joints);
        var row=new SettingsRow([KeyValuePair.Create("joints","off")]);
        Assert.False(Resolve([],new(row)).Joints);
        Assert.True(Resolve(["joints=on"],new(row)).Joints);
        var off=Resolve(["joints=off"]);Assert.NotEqual(defaults.VisibilityKey(),off.VisibilityKey());
        var options=new System.Text.Json.JsonSerializerOptions();options.Converters.Add(new WorkerSettingsConverter());
        var copy=System.Text.Json.JsonSerializer.Deserialize<EffectiveSettings>(System.Text.Json.JsonSerializer.Serialize(off,options),options)!;
        Assert.False(copy.Joints);Assert.Equal(off.VisibilityKey(),copy.VisibilityKey());
        Assert.Contains("joints = off",Settings.CopyAsText([off]));
    }

    [Fact] public void IndependentGateRequiresForeignExactIncidenceInsteadOfAToleranceTie()
    {
        var (faces,edges)=Panels();
        ExactVisibilityOracle.PointResult Check(PlanarFace[] input)
        {
            var oracle=new ExactVisibilityOracle.AllPairsPoints(input,.001);
            return oracle.OnEdge(edges[0],.5,oracle.Prepare(0,0,2,0));
        }
        var exact=Check(faces);Assert.Equal(VisibilityClass.Visible,exact.Class);
        Assert.Contains(exact.Contacts,c=>c.Foreign&&c.ExactIncidence);
        Assert.True(new ExactVisibilityOracle.AllPairsPoints(faces,.001).IsFlushContact(edges[0],.5,1));
        Assert.False(new ExactVisibilityOracle.AllPairsPoints([faces[0],faces[0] with {Source="B"}],.001).IsFlushContact(edges[0],.5,1));
        Assert.False(new ExactVisibilityOracle.AllPairsPoints([faces[0],faces[1] with {OutwardNormal=new(0,0,-1)}],.001).IsFlushContact(edges[0],.5,1));
        var self=Check([faces[0]]);Assert.DoesNotContain(self.Contacts,c=>c.Foreign);
        faces=Panels(.0005).Faces;var near=Check(faces);
        Assert.Equal(VisibilityClass.Unresolved,near.Class);
        Assert.DoesNotContain(near.Contacts,c=>c.Foreign&&c.ExactIncidence);
    }

    [Theory] [InlineData(1)] [InlineData(1000)]
    public void RoundedObliqueProjectionUsesTheExactWorldContact(double scale)
    {
        var (faces,edges)=Panels();
        Vertex Scale(Vertex p)=>new(p.X*scale,p.Y*scale,p.Z*scale);
        Vertex Project(Vertex p)
        {
            double c=Math.Cos(.37),s=Math.Sin(.37),u=Math.Cos(.53),v=Math.Sin(.53);
            return new(p.X*c+p.Y*s,-p.X*s*u+p.Y*c*u+p.Z*v,p.X*s*v-p.Y*c*v+p.Z*u);
        }
        faces=faces.Select(f=>f with {WorldLoops=f.Loops.Select(l=>l.Select(Scale).ToArray()).ToArray(),WorldOutwardNormal=f.OutwardNormal,
            Loops=f.Loops.Select(l=>l.Select(Scale).Select(Project).ToArray()).ToArray(),OutwardNormal=Project(f.OutwardNormal!.Value)}).ToArray();
        edges=edges.Select(e=>e with {WorldA=Scale(e.A),WorldB=Scale(e.B),A=Project(Scale(e.A)),B=Project(Scale(e.B))}).ToArray();
        var on=EdgeFaceKernel.Solve(faces,edges,new(.001*scale));
        Assert.Equal(ContactKind.Flush,Assert.Single(on.Contacts).Kind);
        Assert.Equal(VisibilityClass.Visible,Assert.Single(on.Intervals).Class);
        Assert.Empty(EdgeFaceKernel.Solve(faces,edges,new(.001*scale) {Joints=false}).Intervals);
        Assert.True(new ExactVisibilityOracle.AllPairsPoints(faces,.001*scale).IsFlushContact(edges[0],.5,1));
    }
}
