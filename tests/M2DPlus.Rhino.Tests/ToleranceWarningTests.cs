using System.Reflection;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category","RhinoIntegration")]
public sealed class ToleranceWarningTests(RhinoHost host)
{
    [RhinoFact]
    public void ShortFeatureRiskIsCountedAndGeometrySurvivesUnitAndOffsetTransforms() => host.Run(() =>
    {
        foreach(var factor in new[]{1d,.001})
        foreach(var offset in new[]{0d,1000000})
        {
            using var doc=RhinoDoc.CreateHeadless(null);
            doc.ModelUnitSystem=factor==1 ? UnitSystem.Millimeters : UnitSystem.Meters;
            doc.ModelAbsoluteTolerance=.01*factor;
            var inputs=new List<CaptureInput>();var owned=new List<LineCurve>();
            try
            {
                foreach(var length in new[]{.015,.02,.025})
                {
                    var line=new LineCurve(new Point3d(offset*factor,length*10*factor,0),new Point3d((offset+length)*factor,length*10*factor,0));
                    owned.Add(line);inputs.Add(new(line,[],length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
                using var cache=new SceneCache();
                using var batch=BaselineTests.Solve(BaselineTests.Capture(doc,cache,inputs),ViewRequest.Parse("Top"));
                var view=Assert.Single(batch.Views);
                Assert.Equal(0,view.ToleranceDroppedPieces);Assert.Equal(2,view.SubToleranceSourceEdges);
                Assert.Equal(new[]{"0.015","0.02","0.025"},view.Items.Select(p => p.SourceIdentity).Order().ToArray());
                Assert.NotNull(view.ToleranceWarning);Assert.Contains("source edges at or below 2 x tolerance=2",view.Info);
                var request=ViewRequest.Parse("Top");
                using var presentation=DrawingPresentation.Apply(batch,batch.Identity,[request],[]);
                Assert.Equal(2,presentation.Views[0].SubToleranceSourceEdges);
            }
            finally { foreach(var line in owned)line.Dispose(); }
        }
    });

    [RhinoFact]
    public void GrasshopperShowsScaleAndDetailWarningsAndTheOverrideClearsThem() => host.Run(() =>
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(new Guid("b45a29b1-4343-4035-989e-044e8580d9cf")));
        using var doc=RhinoDoc.CreateHeadless(null);RhinoDoc.ActiveDoc=doc;
        doc.ModelUnitSystem=UnitSystem.Millimeters;doc.ModelAbsoluteTolerance=.1;
        doc.Objects.AddLine(Point3d.Origin,new Point3d(.15,0,0));
        using var gh=new GH_Document { Enabled=true };
        var assembly=Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory,"M2DPlus.Grasshopper.gha"));
        var component=(GH_Component)Activator.CreateInstance(assembly.GetType("M2DPlus.Grasshopper.DrawingComponent",true)!)!;
        gh.AddObject(component,false);
        ((Param_GenericObject)component.Params.Input[1]).PersistentData.Append(new GH_String("Top"));
        var settings=(Param_String)component.Params.Input[3];
        try
        {
            Solve("scale=1:5");
            Assert.Contains(component.RuntimeMessages(GH_RuntimeMessageLevel.Warning),w => w.Contains("too coarse for scale 1:5",StringComparison.Ordinal));
            // Severity policy (UX §5.2): a tolerance too coarse for the scale is a warning; the detail census is a remark.
            Assert.Contains(component.RuntimeMessages(GH_RuntimeMessageLevel.Remark),w => w.Contains("source edges at or below 2 x tolerance",StringComparison.Ordinal));
            Assert.DoesNotContain(component.RuntimeMessages(GH_RuntimeMessageLevel.Warning),w => w.Contains("source edges at or below 2 x tolerance",StringComparison.Ordinal));
            var info=((GH_String)Assert.Single(component.Params.Output[8].VolatileData.AllData(true))).Value;
            Assert.Contains("source edges at or below 2 x tolerance=1",info);
            Solve("scale=1:5\ntolerance=.001 mm");
            Assert.DoesNotContain(component.RuntimeMessages(GH_RuntimeMessageLevel.Warning),w => w.Contains("tolerance",StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(component.RuntimeMessages(GH_RuntimeMessageLevel.Remark),w => w.Contains("source edges at or below 2 x tolerance",StringComparison.Ordinal));
            Assert.Equal(1,component.Params.Output[0].VolatileDataCount);
            Assert.Equal(.1,doc.ModelAbsoluteTolerance);
        }
        finally { component.RemovedFromDocument(gh);SceneCache.Shared.Clear(); }

        void Solve(string text)
        {
            settings.PersistentData.Clear();settings.PersistentData.Append(new GH_String(text));settings.ExpireSolution(false);component.ExpireSolution(false);
            gh.NewSolution(false,GH_SolutionMode.Silent);
            GrasshopperTests.PumpUntil(() => component.Params.Output[8].VolatileDataCount==1,() => component.Message,
                () => { if(gh.ScheduleDelay>=0)gh.NewSolution(false,GH_SolutionMode.Silent); });
        }
    });
}
