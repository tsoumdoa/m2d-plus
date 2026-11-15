// MIT. Manual component acceptance; invoked only in the owned one-view STA host.
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using M2DPlus.Rhino.Services;
using Rhino;

namespace M2DPlus.Render;

internal static class ViewCaseComponent
{
    public static void Run(string modelPath, string viewName, string ghaPath, string output)
    {
        using var rhino = RhinoDoc.OpenHeadless(modelPath);
        RhinoDoc.ActiveDoc = rhino; // This newly created process owns this document.
        GH_Document.EnableSolutions = true;
        using var gh = new GH_Document { Enabled = true };
        var gha = Assembly.LoadFrom(ghaPath);
        var component = (GH_Component)Activator.CreateInstance(gha.GetType("M2DPlus.Grasshopper.DrawingComponent", true)!)!;
        component.CreateAttributes(); gh.AddObject(component, false);
        var view = rhino.Objects.GetObjectList(global::Rhino.DocObjects.ObjectType.AnyObject).Single(o => o.Name == "View box " + viewName);
        ((Param_GenericObject)component.Params.Input[1]).PersistentData.Append(new GH_Guid(view.Id));
        var timer = Stopwatch.StartNew();
        try
        {
            gh.NewSolution(true, GH_SolutionMode.Silent);
            while (component.Params.Output[8].VolatileDataCount != 1 && timer.Elapsed.TotalSeconds < 300)
            {
                RhinoApp.Wait(); System.Windows.Forms.Application.DoEvents();
                if (gh.ScheduleDelay >= 0) gh.NewSolution(false, GH_SolutionMode.Silent);
                if (component.RuntimeMessages(GH_RuntimeMessageLevel.Error).Count > 0) break;
                Thread.Sleep(5);
            }
            var info = component.Params.Output[8].VolatileData.AllData(true).OfType<GH_String>().Select(x => x.Value).ToArray();
            var drawing = (DrawingBatch?)component.GetType().GetField("_drawing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component);
            if (drawing is not null)
                File.WriteAllText(Path.Combine(output, "component.svg"), Program.Render(drawing.Views.Single(), Path.Combine(output, "component.png")));
            var cuts = component.Params.Output[4].VolatileData; var hatches = component.Params.Output[5].VolatileData;
            bool aligned = cuts.PathCount == hatches.PathCount && cuts.Paths.Select(p => p.ToString()).SequenceEqual(hatches.Paths.Select(p => p.ToString()));
            var result = new { model = modelPath, view = viewName, seconds = timer.Elapsed.TotalSeconds,
                status = info.Length == 1 && drawing is not null && aligned ? "pass" : "fail", hatchPathsAligned = aligned,
                inputs = component.Params.Input.Count, outputs = component.Params.Output.Count,
                counts = component.Params.Output.Select(p => p.VolatileDataCount).ToArray(), info,
                errors = component.RuntimeMessages(GH_RuntimeMessageLevel.Error),
                route = "real GH DrawingComponent; persisted view-box GUID; document-mode geometry" };
            File.WriteAllText(Path.Combine(output, "component.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { component.RemovedFromDocument(gh); SceneCache.Shared.Clear(); }
    }
}
