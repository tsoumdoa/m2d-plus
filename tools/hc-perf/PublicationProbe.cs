using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using M2DPlus.Rhino.Services;
using M2DPlus.Core.Output;
using Rhino;

namespace M2DPlus.HcPerf;

/// <summary>Actual component Emit and GH volatile output trees in the owned headless process.
/// Excludes GH scheduling, downstream components and viewport redraw.</summary>
public sealed class PublicationProbe
{
    private readonly object _component;
    private readonly object _access;
    private readonly MethodInfo _emit;
    public PublicationProbe()
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(@"C:\Program Files\Rhino 8\Plug-ins\Grasshopper", name.Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        if (RhinoDoc.ActiveDoc is null) throw new InvalidOperationException("Publication requires the owned STA RhinoCore host");
        {
            var gh = Assembly.LoadFrom(@"C:\Program Files\Rhino 8\Plug-ins\Grasshopper\Grasshopper.dll");
            var assembly = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(typeof(PublicationProbe).Assembly.Location)!,"M2DPlus.Grasshopper.gha"));
            _component = Activator.CreateInstance(assembly.GetType("M2DPlus.Grasshopper.DrawingComponent",true)!)!;
            _emit = _component.GetType().GetMethod("Emit",BindingFlags.NonPublic | BindingFlags.Instance)!;
            _access = DispatchProxy.Create(gh.GetType("Grasshopper.Kernel.IGH_DataAccess",true)!,typeof(OutputAccess));
            ((OutputAccess)_access).Component = _component;
        }
    }
    public void Publish(DrawingBatch batch, IReadOnlyDictionary<string, SourceGroupInfo> groups)
    {
        double before = batch.Views.Sum(v => v.Phases.Seconds.GetValueOrDefault("GH publication"));
        var timer = Stopwatch.StartNew();
        var sources = Activator.CreateInstance(_emit.GetParameters()[2].ParameterType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [GeometryInputKind.Document, groups], null)!;
        _emit.Invoke(_component,[_access,batch,sources]);
        double measured = batch.Views.Sum(v => v.Phases.Seconds.GetValueOrDefault("GH publication")) - before;
        foreach (var view in batch.Views) view.Phases.Add("GH publication",(timer.Elapsed.TotalSeconds-measured)/batch.Views.Count);
    }
    public class OutputAccess : DispatchProxy
    {
        public object? Component { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "SetDataTree")
            {
                dynamic component = Component!;
                component.Params.Output[(int)args![0]!].ClearData();
                component.Params.Output[(int)args[0]!].AddVolatileDataTree((dynamic)args[1]!);
            }
            return targetMethod?.ReturnType == typeof(bool) ? true : null;
        }
    }
}

