using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Core.Sections;
using M2DPlus.Core.Visibility;
using M2DPlus.Rhino.Sections;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace M2DPlus.Rhino.Tests;

/// <summary>Independent analytic feature scoring. Failures remain in the score; no native output is truth.</summary>
[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class SmallFeatureTests(RhinoHost host, ITestOutputHelper output)
{
    private sealed record Feature(double Length, Point3d[] Samples);
    private sealed record Row(string Model, string Detail, double ToleranceMm, string Units, double OffsetMm,
        string Engine, int Expected, int Correct, bool Gate, bool WrongFill, bool Junk, double Seconds,
        int Unresolved, int Invalid, string[] Notes);

    [RhinoTheory]
    [InlineData(.1)] [InlineData(.01)] [InlineData(.001)]
    public void AnalyticFeaturesAndMetamorphicVariants(double selectedTolerance) => host.Run(() =>
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "small");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var rows = new List<Row>();
        foreach (var model in manifest.RootElement.GetProperty("models").EnumerateArray())
        {
            if(model.GetProperty("toleranceMm").GetDouble()!=selectedTolerance)continue;
            var path = Path.Combine(root, model.GetProperty("path").GetString()!);
            Assert.Equal(model.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
            using var archive = File3dm.Read(path);
            var factor = model.GetProperty("unitsPerMm").GetDouble();
            var offset = model.GetProperty("offsetMm").GetDouble();
            var tol = model.GetProperty("toleranceMm").GetDouble();
            Assert.Equal(tol * factor, archive.Settings.ModelAbsoluteTolerance, 12);
            using var document = RhinoDoc.CreateHeadless(null);
            document.ModelUnitSystem = archive.Settings.ModelUnitSystem;
            document.ModelAbsoluteTolerance = archive.Settings.ModelAbsoluteTolerance;
            foreach (var detail in model.GetProperty("cases").EnumerateArray())
            {
                var name = detail.GetProperty("name").GetString()!;
                var obj = archive.Objects.Single(o => o.Attributes.Name == name);
                Assert.True(obj.Geometry.IsValid, name);
                var origin = detail.GetProperty("originMm").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                // Independent inverse physical transform, before comparison in canonical mm.
                var local = Transform.Translation(-origin[0] - offset, -origin[1] - offset, -offset) * Transform.Scale(Point3d.Origin, 1 / factor);
                var plane = new Plane(new Point3d((origin[0]+offset)*factor, (origin[1]+offset)*factor, offset*factor), Vector3d.ZAxis);
                var profiles = detail.GetProperty("profiles").EnumerateArray().ToArray();
                var features = profiles.SelectMany(Features).ToArray();
                Assert.Equal(detail.GetProperty("edgeLengthsMm").GetArrayLength(), features.Length);
                var probeY = detail.GetProperty("probeY").GetDouble();
                var bands = MaterialBands(profiles, probeY);
                using var probe = new LineCurve(new Point3d((origin[0]+offset-.5)*factor, (origin[1]+offset+probeY)*factor,
                    (offset-3)*factor), new Point3d((origin[0]+offset+3.5)*factor, (origin[1]+offset+probeY)*factor, (offset-3)*factor));
                using var cache = new SceneCache();
                using var batch = BaselineTests.Solve(BaselineTests.Capture(document, cache,
                    [new(obj.Geometry, [], name), new(probe, [], name+"/probe")]), ViewRequest.Parse("Top"));
                var drawing = Assert.Single(batch.Views);
                var curves = drawing.Items.Where(p => p.SourceIdentity == name).Select(p => (p.Class, Curve: p.CopyGeometry())).ToArray();
                var probes = drawing.Items.Where(p => p.SourceIdentity == name+"/probe").Select(p => (p.Class, Curve: p.CopyGeometry())).ToArray();
                try
                {
                    foreach (var (_, g) in curves.Concat(probes)) g.Transform(local);
                    var visible = curves.Where(c => c.Class == LineClass.Visible).Select(c => c.Curve).OfType<Curve>().ToArray();
                    var correct = features.Count(f => Covered(f, visible, tol));
                    var junk = visible.Any(c => Enumerable.Range(0, 33).Any(i => !OnFeatures(c.PointAtNormalizedLength(i/32d), features, tol)));
                    var probeCorrect = ProbeCorrect(probes, bands, tol);
                    var gate = features.Where(f => f.Length >= 2*tol).All(f => Covered(f, visible, tol)) && probeCorrect && !junk && drawing.Error is null && !drawing.Report.Quarantined;
                    Add("native", features.Length+1, correct+(probeCorrect ? 1 : 0), gate, false, junk, drawing.Seconds,
                        drawing.Report.UncertainCount, drawing.Report.InvalidExtractionCount, drawing.Diagnostics.ToArray());
                }
                finally { foreach (var (_, g) in curves.Concat(probes)) g.Dispose(); }

                // This is the isolated section certificate as well as the public region pipeline.
                var timer = Stopwatch.StartNew();
                using var sections = SectionRegionEngine.Compute([new(name, obj.Geometry, SectionStyleSnapshot.Default())], new(plane,tol*factor,.001));
                var expectedRegions = detail.GetProperty("regions").GetInt32();
                var expectedArea = detail.GetProperty("areaMm2").GetDouble();
                var area = sections.Regions.Sum(r => r.Area)/(factor*factor);
                var holes = sections.Regions.Sum(r => r.Loops.Count(l => l.IsHole));
                // Absolute error is bounded independently by perimeter * 0.01 tol, never total scene area.
                var areaError = Math.Max(1e-8, features.Sum(f => f.Length)*tol*.01);
                var boundaries=sections.Regions.SelectMany(r => r.Loops).Select(l => l.Curve.DuplicateCurve()).ToArray();
                bool boundaryGood;
                try
                {
                    foreach(var c in boundaries)c.Transform(local);
                    boundaryGood=expectedRegions==0 ? boundaries.Length==0 : features.All(f => Covered(f,boundaries,tol)) &&
                        boundaries.All(c => Enumerable.Range(0,33).All(i => OnFeatures(c.PointAtNormalizedLength(i/32d),features,tol)));
                }
                finally { foreach(var c in boundaries)c.Dispose(); }
                var wrongFill = expectedRegions == 0 && sections.Regions.Any(r => r.Hatch is not null) ||
                    sections.Regions.Count > 0 && (Math.Abs(area-expectedArea) > areaError || !boundaryGood);
                var sectionCorrect = sections.Regions.Count == expectedRegions && holes == detail.GetProperty("holes").GetInt32() && Math.Abs(area-expectedArea) <= areaError && !wrongFill;
                var sectionNotes = sections.Diagnostics.Select(d => d.Message).ToArray();
                Add("regions",1,sectionCorrect ? 1 : 0,sectionCorrect,wrongFill,false,timer.Elapsed.TotalSeconds,
                    sections.Components.Count(c => c.Status == SectionStatus.Uncertain),0,sectionNotes);
                using var brep = obj.Geometry switch { Brep b => b.DuplicateBrep(), Extrusion e => e.ToBrep(), Mesh m => Brep.CreateFromMesh(m,true), _ => throw new InvalidDataException(name) };
                timer.Restart();
                var certified = StableSectionCertificate.Try(brep,new(plane,tol*factor,.001),out var reason,out var evidence);
                using (evidence)
                {
                    var certificateArea = 0d;
                    if (evidence is not null)
                    {
                        var loopAreas = evidence.Loops.Select(c => { using var m = AreaMassProperties.Compute(c); return m!.Area/(factor*factor); }).OrderDescending().ToArray();
                        certificateArea = loopAreas[0]-loopAreas.Skip(1).Sum();
                    }
                    var good = expectedRegions == 0 ? !certified : certified && Math.Abs(certificateArea-expectedArea) <= areaError;
                    Add("certificate",1,good ? 1 : 0,good,certified && expectedRegions == 0,false,timer.Elapsed.TotalSeconds,certified ? 0 : 1,0,[reason]);
                }

                // Curves are explicitly outside the planar domain, even if IsLinear(tol) approximates them.
                if (profiles.All(p => p.GetProperty("kind").GetString() == "polygon"))
                {
                    timer.Restart();
                    var extraction = new PlanarExtraction();
                    Assert.True(extraction.Add(obj.Geometry,Transform.Identity,Plane.WorldXY,tol*factor,name));
                    Assert.True(extraction.Add(probe,Transform.Identity,Plane.WorldXY,tol*factor,name+"/probe"));
                    var exact = EdgeFaceKernel.Solve(extraction.Faces,extraction.Edges,new(tol*factor));
                    var loci = VisibilityLoci.Create(extraction.Edges,exact,tol*factor);
                    var lines = loci.Select(l => (Class: PlanarExtraction.OutputClass(l.Class), Curve: (GeometryBase)new LineCurve(
                        new Point3d(l.X0,l.Y0,0),new Point3d(l.X1,l.Y1,0)))).ToArray();
                    try
                    {
                        foreach (var (_,g) in lines) g.Transform(local);
                        var outline = lines.Where((_,i) => loci[i].Source == name && lines[i].Class == LineClass.Visible).Select(l => (Curve)l.Curve).ToArray();
                        var good = features.Count(f => Covered(f,outline,tol));
                        var probeGood = ProbeCorrect(lines.Where((_,i) => loci[i].Source == name+"/probe").ToArray(),bands,tol);
                        var junk = outline.Any(c => Enumerable.Range(0,9).Any(i => !OnFeatures(c.PointAtNormalizedLength(i/8d),features,tol)));
                        Add("exact",features.Length+1,good+(probeGood ? 1 : 0),features.Where(f => f.Length>=2*tol).All(f => Covered(f,outline,tol))&&probeGood&&!junk,false,junk,timer.Elapsed.TotalSeconds,
                            exact.Intervals.Count(i => i.Class == VisibilityClass.Unresolved),0,exact.Diagnostics.ToArray());
                    }
                    finally { foreach (var (_,g) in lines) g.Dispose(); }
                }

                void Add(string engine,int expected,int correct,bool gate,bool wrong,bool junk,double seconds,int unresolved,int invalid,string[] notes) => rows.Add(new(
                    Path.GetFileNameWithoutExtension(path),name,tol,model.GetProperty("units").GetString()!,offset,engine,expected,correct,gate,wrong,junk,seconds,unresolved,invalid,notes));
            }
            // Joining two different sources is a separate oracle: area alone cannot detect a bridged gap.
            var pair=archive.Objects.Where(o => o.Attributes.Name is "gap-left" or "gap-right").ToArray();
            var pairPlane=new Plane(new Point3d(offset*factor,offset*factor,offset*factor),Vector3d.ZAxis);
            var pairTimer=Stopwatch.StartNew();
            using var merged=SectionRegionEngine.Compute(pair.Select(o => new SectionSource(o.Attributes.Name,o.Geometry,SectionStyleSnapshot.Default())),
                new(pairPlane,tol*factor,.001,Cuts:CutMode.Merge));
            var gapGood=merged.Regions.Count==2 && Math.Abs(merged.Regions.Sum(r => r.Area)/(factor*factor)-2)<1e-6 && merged.Regions.All(r => r.SourceIds.Count==1);
            rows.Add(new(Path.GetFileNameWithoutExtension(path),"gap-pair-merge",tol,model.GetProperty("units").GetString()!,offset,"regions",1,gapGood?1:0,gapGood,
                merged.Regions.Count==1,false,pairTimer.Elapsed.TotalSeconds,merged.RequiresQuarantine?1:0,0,merged.Diagnostics.Select(d => d.Message).ToArray()));
            // Two real occluders and a rear probe must preserve the *open* 0.01 mm gap.
            var gapOrigin=model.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString()=="gap-left")
                .GetProperty("originMm").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            using var gapProbe=new LineCurve(new Point3d((offset+gapOrigin[0]-.5)*factor,(offset+gapOrigin[1]+.5)*factor,(offset-3)*factor),
                new Point3d((offset+gapOrigin[0]+3.5)*factor,(offset+gapOrigin[1]+.5)*factor,(offset-3)*factor));
            using var gapCache=new SceneCache();
            using var gapBatch=BaselineTests.Solve(BaselineTests.Capture(document,gapCache,
                pair.Select(o => new CaptureInput(o.Geometry,[],o.Attributes.Name)).Append(new(gapProbe,[],"gap-probe"))),ViewRequest.Parse("Top"));
            var gapDrawing=gapBatch.Views[0];
            if(tol>=.01) { Assert.True(gapDrawing.SubToleranceBoundsGaps>0);Assert.NotNull(gapDrawing.ToleranceWarning); }
            var gapLines=gapDrawing.Items.Where(i => i.SourceIdentity=="gap-probe").Select(i => (i.Class,Curve:i.CopyGeometry())).ToArray();
            bool gapVisible;
            try
            {
                var inverse=Transform.Translation(-offset-gapOrigin[0],-offset-gapOrigin[1],0)*Transform.Scale(Point3d.Origin,1/factor);
                foreach(var (_,g) in gapLines)g.Transform(inverse);
                gapVisible=ProbeCorrect(gapLines,[(0,1),(1.01,2.01)],tol);
            }
            finally { foreach(var (_,g) in gapLines)g.Dispose(); }
            rows.Add(new(Path.GetFileNameWithoutExtension(path),"gap-pair-probe",tol,model.GetProperty("units").GetString()!,offset,"native",1,gapVisible?1:0,gapVisible,
                false,false,gapDrawing.Seconds,gapDrawing.Report.UncertainCount,gapDrawing.Report.InvalidExtractionCount,gapDrawing.Diagnostics.ToArray()));
        }
        var metamorphic = rows.GroupBy(r => (r.Detail,r.ToleranceMm,r.Engine)).All(g => g.Select(r => (r.Correct,r.Gate,r.WrongFill,r.Junk,r.Unresolved)).Distinct().Count()==1);
        var scored = rows.ToArray();
        var summary = new { Runtime = System.Environment.Version.ToString(), Rhino = RhinoApp.Version.ToString(),
            Gate = scored.All(r => r.Gate) && metamorphic, Metamorphic = metamorphic,
            Fraction = scored.Sum(r => r.Correct)/(double)scored.Sum(r => r.Expected), Rows = rows };
        var json = JsonSerializer.Serialize(summary,new JsonSerializerOptions { WriteIndented=true });
        var destination = System.Environment.GetEnvironmentVariable("M2D_SMALL_RESULTS");
        if (destination is not null) { Directory.CreateDirectory(destination); File.WriteAllText(Path.Combine(destination,FormattableString.Invariant($"net{System.Environment.Version.Major}-t{selectedTolerance}.json")),json); }
        output.WriteLine($"Small features: fraction={summary.Fraction:P2}; gate={summary.Gate}; metamorphic={metamorphic}; {rows.Count} rows.");
        // Measurements are opt-in strict acceptance, not a fabricated promise about unsupported native behaviour.
        if (selectedTolerance<.1 && System.Environment.GetEnvironmentVariable("M2D_SMALL_STRICT") == "1") Assert.True(summary.Gate,json);
        if(selectedTolerance<.1) Assert.False(rows.Any(r => r.WrongFill),"A known incorrect fill was emitted; see the small-feature score.");
    });

    private static bool Covered(Feature f,Curve[] actual,double tol) => f.Samples.All(p => actual.Any(c => c.ClosestPoint(p,out var t) && DistanceXY(c.PointAt(t),p) <= Math.Min(tol*.25,f.Length/8)+1e-7));
    private static double DistanceXY(Point3d a,Point3d b) => Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));
    private static bool OnFeatures(Point3d p,Feature[] features,double tol) => features.Any(f => Enumerable.Range(1,f.Samples.Length-1).Any(i =>
    {
        var a=f.Samples[i-1];var b=f.Samples[i];var dx=b.X-a.X;var dy=b.Y-a.Y;
        var t=Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/(dx*dx+dy*dy),0,1);
        return DistanceXY(p,new(a.X+t*dx,a.Y+t*dy,0))<=tol*.25+1e-7;
    }));

    private static Feature[] Features(JsonElement p)
    {
        var kind=p.GetProperty("kind").GetString();
        Feature Line(double x,double y,double u,double v) => new(Math.Sqrt(Math.Pow(u-x,2)+Math.Pow(v-y,2)),Enumerable.Range(0,257).Select(i => new Point3d(x+(u-x)*i/256,y+(v-y)*i/256,0)).ToArray());
        Feature Arc(double x,double y,double r,double start,double angle) => new(r*angle,Enumerable.Range(0,257).Select(i => new Point3d(x+r*Math.Cos(start+angle*i/256),y+r*Math.Sin(start+angle*i/256),0)).ToArray());
        if (kind=="polygon")
        {
            var pts=p.GetProperty("points").EnumerateArray().Select(a => a.EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToArray();
            return pts.Select((a,i) => Line(a[0],a[1],pts[(i+1)%pts.Length][0],pts[(i+1)%pts.Length][1])).ToArray();
        }
        var x=p.GetProperty("x").GetDouble(); var y=p.GetProperty("y").GetDouble(); var r=p.GetProperty("r").GetDouble();
        if(kind=="circle") return [Arc(x,y,r,0,2*Math.PI)];
        var w=p.GetProperty("w").GetDouble(); var h=p.GetProperty("h").GetDouble();
        return [Line(x+r,y,x+w-r,y),Arc(x+w-r,y+r,r,-Math.PI/2,Math.PI/2),Line(x+w,y+r,x+w,y+h-r),Arc(x+w-r,y+h-r,r,0,Math.PI/2),
            Line(x+w-r,y+h,x+r,y+h),Arc(x+r,y+h-r,r,Math.PI/2,Math.PI/2),Line(x,y+h-r,x,y+r),Arc(x+r,y+r,r,Math.PI,Math.PI/2)];
    }

    private static (double A,double B)[] MaterialBands(JsonElement[] profiles,double y)
    {
        (double A,double B) Range(JsonElement p)
        {
            if(p.GetProperty("kind").GetString()=="circle") { var x=p.GetProperty("x").GetDouble();var r=p.GetProperty("r").GetDouble();return (x-r,x+r); }
            if(p.GetProperty("kind").GetString()=="rounded") { var x=p.GetProperty("x").GetDouble();return (x,x+p.GetProperty("w").GetDouble()); }
            var pts=p.GetProperty("points").EnumerateArray().Select(a => a.EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToArray();
            var xs=pts.Select((a,i) => (A:a,B:pts[(i+1)%pts.Length])).Where(e => e.A[1]<=y && e.B[1]>y || e.B[1]<=y && e.A[1]>y)
                .Select(e => e.A[0]+(y-e.A[1])*(e.B[0]-e.A[0])/(e.B[1]-e.A[1])).Order().ToArray(); return (xs[0],xs[^1]);
        }
        var outer=Range(profiles[0]); if(profiles.Length==1)return [outer];var hole=Range(profiles[1]);return [(outer.A,hole.A),(hole.B,outer.B)];
    }
    private static bool ProbeCorrect((LineClass Class,GeometryBase Curve)[] actual,(double A,double B)[] bands,double tol)
    {
        // Probe interval truth, bidirectional: no extra, missing, Unset or wrongly classified intervals.
        var intervals=actual.Select(c => {var b=c.Curve.GetBoundingBox(true);return (c.Class,A:b.Min.X,B:b.Max.X);}).OrderBy(c => c.A).ToArray();
        if(intervals.Any(i => i.Class is not (LineClass.Visible or LineClass.Hidden)))return false;
        for(var j=0;j<801;j++)
        {
            var x=-.5+4*j/800d;
            if(bands.Any(b => Math.Abs(x-b.A)<tol*.25+1e-7 || Math.Abs(x-b.B)<tol*.25+1e-7))continue;
            var expected=bands.Any(b => x>b.A && x<b.B) ? LineClass.Hidden : LineClass.Visible;
            if(!intervals.Any(i => i.Class==expected && i.A<=x+1e-7 && i.B>=x-1e-7))return false;
            if(intervals.Any(i => i.Class!=expected && i.A<x-1e-7 && i.B>x+1e-7))return false;
        }
        return intervals.All(i => i.A>=-.500001 && i.B<=3.500001);
    }
}
