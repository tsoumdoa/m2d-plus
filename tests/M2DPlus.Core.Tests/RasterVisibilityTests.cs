using System.Text.Json;
using M2DPlus.Core.Experimental.RasterVisibility;
using Xunit;
using static M2DPlus.Core.Experimental.RasterVisibility.RasterBuffer;

namespace M2DPlus.Core.Tests;

public sealed class RasterVisibilityTests
{
    private static EffectiveSettings Resolve(params string[] lines) => Settings.Resolve(
        [new("v", "Top", ViewSourceKind.Direction, false, 100, .001)], [lines], DocumentUnits.Millimetres).Views[0];
    [Fact] public void RasterIsOptInAndKeysItsPrecisionPolicyThroughSettingsAndTransport()
    {
        var hld = Resolve(); var raster = Resolve("visibility=RASTER");
        Assert.Equal(VisibilityMode.Raster, raster.Visibility);
        Assert.Equal(hld.VisibilityKey(), Resolve("visibility=hld").VisibilityKey());
        Assert.DoesNotContain("raster", hld.VisibilityKey());
        Assert.Contains(RasterPolicy.Identity, raster.VisibilityKey());
        Assert.Contains(RasterPolicy.Identity, Settings.RunText(["visibility=raster"]));
        Assert.Contains(RasterPolicy.Identity, Settings.RunText(new SettingsRow([KeyValuePair.Create("visibility", "raster")])));
        var options = new JsonSerializerOptions(); options.Converters.Add(new WorkerSettingsConverter());
        var restored = JsonSerializer.Deserialize<EffectiveSettings>(JsonSerializer.Serialize(raster, options), options)!;
        Assert.Equal(raster.VisibilityKey(), restored.VisibilityKey());
    }
    [Theory] [InlineData(1)] [InlineData(4)]
    public void RaysUseTriangleCoverageAndDepthWithNoSlopeOrFaceIdShortcut(int threads)
    {
        Triangle[] triangles = [new(new(0,0,10), new(2,0,2), new(0,2,10), 7)];
        var grid = new RasterBuffer(0,0,2,2,1,triangles,threads);
        Assert.False(grid.Visible(new(.5,.5,7), .01)); // Actual surface is z=8.
        Assert.True(grid.Visible(new(.5,.5,9), .01));
        Assert.True(grid.Visible(new(1.9,1.9,0), .01)); // Plane extrapolation would wrongly hide it.
        Assert.False(grid.Visible(new(.5,.5,7.98), .01)); // No pixel/slope allowance.
        Assert.True(grid.Visible(new(.5,.5,7.995), .01)); // Declared tessellation allowance.
    }
    [Fact] public void SubpixelOccludersNeedNotCoverAPixelCentre()
    {
        Triangle[] triangles = [new(new(.1,.1,2),new(.2,.1,2),new(.1,.2,2),3)];
        var grid = new RasterBuffer(0,0,1,1,1,triangles,1);
        Assert.False(grid.Visible(new(.125,.125,1), .001));
        Assert.True(grid.Visible(new(.3,.3,1), .001));
    }
    [Fact] public void FoldedFaceCannotExposeItsOwnRearCandidateAndWindingIsIrrelevant()
    {
        Triangle[] triangles = [new(new(0,0,2),new(1,1,2),new(1,0,2),7), new(new(0,0,2),new(0,1,2),new(1,1,2),7)];
        foreach (var threads in new[] {1,4})
        {
            var grid = new RasterBuffer(0,0,1,1,64,triangles,threads);
            Assert.False(grid.Visible(new(.5,.5,1), .01));
            Assert.True(grid.Visible(new(.5,.5,3), .01));
            Assert.True(grid.Visible(new(-1,.5,1), .01));
        }
    }
    [Fact] public void MemoryAdmissionIncludesTrianglesAndClampsImageAndCancellationIsObserved()
    {
        Assert.True(RasterPolicy.Reservation(new(1000,1000,1000,100)) > RasterPolicy.Reservation(new(1000,1,1,1)));
        Triangle[] triangles = [new(new(0,0,2),new(1,0,2),new(0,1,2),1)];
        Assert.Throws<NotSupportedException>(()=>new RasterBuffer(0,0,1,1,8192,triangles,1,1024));
        var grid = new RasterBuffer(0,0,1,1,8192,triangles,1,128*RasterPolicy.MiB);
        Assert.True(grid.Width < 8192); Assert.True(grid.EstimatedBytes <= 128*RasterPolicy.MiB);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(()=>new RasterBuffer(0,0,1,1,64,triangles,1,token:cancel.Token));
    }
    [Fact] public void LastEffectiveVisibilityControlsRunPolicyIncludingPerViewRows()
    {
        Assert.DoesNotContain(RasterPolicy.Identity, Settings.RunText(["visibility=raster", "visibility=hld"]));
        Assert.Contains(RasterPolicy.Identity, Settings.RunText(["visibility=hld", "visibility=raster"]));
        Assert.Contains(RasterPolicy.Identity, Settings.RunText(["[Top]", "visibility=raster", "[Front]", "visibility=hld"]));
        Assert.DoesNotContain(RasterPolicy.Identity, Settings.RunText(["visibility=raster", "visibility=pipes # last valid"]));
    }
    [Fact] public void ReservationWeightsCurvedFacesAndReusesExistingWorkerHeadroom()
    {
        var planar = new SceneCost(1000, 1000, 0, 100);
        var curved = planar with { CurvedBrepFaces = 500, CurvedBrepEdges = 1500 };
        Assert.Equal(2000, RasterPolicy.EstimatedTriangles(planar));
        Assert.True(RasterPolicy.ProjectionEstimate(curved) > RasterPolicy.ProjectionEstimate(planar));
        Assert.Equal(0, RasterPolicy.WorkerExtra(planar));
        var covered = new SceneCost(1000, 10_000, 0, 100) { CurvedBrepFaces = 5000, CurvedBrepEdges = 50_000 };
        Assert.Equal(0, RasterPolicy.Reservation(covered));
        Assert.Equal(0, RasterPolicy.WorkerExtra(covered));
        var coveredProjection = RasterPolicy.ProjectionReservation(covered, 1, covered.Estimate(1));
        Assert.Equal(RasterPolicy.ProjectionEstimate(covered), coveredProjection);
        Assert.Equal(0, RasterPolicy.AdditionalReservation(covered, coveredProjection));
        var budget = planar.Estimate(2) + RasterPolicy.MinimumReservation(planar) - 1;
        Assert.Equal(0, RasterPolicy.ProjectionReservation(planar, 2, budget));
        budget += 64 * RasterPolicy.MiB;
        var reservation = RasterPolicy.ProjectionReservation(planar, 2, budget);
        Assert.Equal(budget, planar.Estimate(2) + reservation);
        var grid = new RasterBuffer(0, 0, 1, 1, 8192, [], 1, reservation);
        Assert.True(grid.EstimatedBytes <= reservation);
    }
    [Theory] [InlineData(0)] [InlineData(1_000_000)]
    public void EdgeOnSurveySliversUseRelativeAreaWhileOrdinaryTrianglesStillOcclude(double offset)
    {
        Triangle sliver = new(new(offset,offset,10), new(offset+100_000,offset,10), new(offset+50_000,offset+1e-8,10), 1);
        Assert.True(sliver.Degenerate);
        var grid = new RasterBuffer(offset,offset,100_000,1,64,[sliver],1);
        Assert.True(grid.Visible(new(offset+50_000,offset+2e-9,0), .001));
        Triangle ordinary = new(new(offset,offset,10), new(offset+100_000,offset,10), new(offset,offset+1,10), 2);
        Assert.False(ordinary.Degenerate);
        Assert.False(new RasterBuffer(offset,offset,100_000,1,64,[ordinary],1).Visible(new(offset+100,offset+.1,0), .001));
    }

    [Fact] public void CurvedEstimateHasNoDownwardDensityThresholds()
    {
        long previous = 0;
        for (var faces = 0; faces <= 10_000; faces++)
        {
            var triangles = RasterPolicy.EstimatedTriangles(new(1000, faces, 0, 1) { CurvedBrepFaces = faces });
            Assert.True(triangles >= previous); previous = triangles;
        }
    }

}
