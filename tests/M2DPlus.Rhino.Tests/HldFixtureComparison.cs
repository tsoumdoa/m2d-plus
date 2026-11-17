using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;
using Point = Rhino.Geometry.Point;
using Xunit;
namespace M2DPlus.Rhino.Tests;

/// <summary>Fixture-level bidirectional classed locus comparison, independent of fragment order/ownership.
/// Not a universal HLD equality guarantee. Transport of one realization is tested separately.</summary>
internal static class HldFixtureComparison
{
    private static bool SameRegion(M2DPlus.Rhino.Sections.SectionRegion a,M2DPlus.Rhino.Sections.SectionRegion b,double tolerance)
    {
        if(a.Loops.Count!=b.Loops.Count || Math.Abs(a.Area-b.Area)>tolerance*Math.Max(1,a.Area))return false;
        bool Covered(M2DPlus.Rhino.Sections.SectionRegion from,M2DPlus.Rhino.Sections.SectionRegion to)=>
            from.Loops.All(loop=>Enumerable.Range(0,33).All(i=>
            {
                var point=loop.Curve.PointAtNormalizedLength(i/32d);
                return to.Loops.Any(other=>other.IsHole==loop.IsHole&&other.Curve.ClosestPoint(point,out var at)&&other.Curve.PointAt(at).DistanceTo(point)<=tolerance);
            }));
        return Covered(a,b)&&Covered(b,a);
    }
    internal static void Equal(ViewDrawing expected, ViewDrawing actual)
    {
        Assert.Null(expected.Error);Assert.Null(actual.Error);
        Assert.Equal(expected.MultipleThreads,actual.MultipleThreads);
        Assert.Equal(expected.Report.Quarantined,actual.Report.Quarantined);
        var tolerance=Math.Max(expected.Tolerance,actual.Tolerance);
        Assert.True(tolerance>0);
        Check(expected,actual);Check(actual,expected);
        Assert.Equal(expected.Regions.Count,actual.Regions.Count);
        var remaining=actual.Regions.ToList();
        foreach(var region in expected.Regions)
        {
            var entry=remaining.FirstOrDefault(r=>SameRegion(region.Region,r.Region,tolerance));
            Assert.NotNull(entry);remaining.Remove(entry);
            var match=entry.Region;
            Assert.Equal(region.Region.Loops.Count,match.Loops.Count);
            Assert.Equal(region.Region.Loops.Count(l=>l.IsHole),match.Loops.Count(l=>l.IsHole));
            Assert.InRange(Math.Abs(region.Region.Area-match.Area),0,tolerance*Math.Max(1,region.Region.Area));
            Assert.Equal(region.Region.Hatch is null,match.Hatch is null);
        }
        void Check(ViewDrawing from,ViewDrawing to)
        {
            foreach(var item in from.Items)
            {
                using var geometry=item.CopyGeometry();
                var targets=to.Items.Where(p=>p.Class==item.Class).Select(p=>p.CopyGeometry()).ToArray();
                try
                {
                    var points=geometry is Curve curve?Enumerable.Range(0,33).Select(i=>curve.PointAtNormalizedLength(i/32d)):
                        geometry is Point nativePoint?new[]{nativePoint.Location}:Array.Empty<Point3d>();
                    foreach(var point in points) Assert.True(targets.Any(g=>g is Curve c&&c.ClosestPoint(point,out var at)&&c.PointAt(at).DistanceTo(point)<=tolerance ||
                        g is Point p&&p.Location.DistanceTo(point)<=tolerance),$"Missing {item.Class} locus at {point}; tolerance={tolerance:R}");
                }
                finally{foreach(var target in targets)target.Dispose();}
            }
        }
    }
}
