using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Rhino.Runtime.InProcess;
using Xunit;

namespace M2DPlus.Rhino.Tests;

[CollectionDefinition("Rhino")]
public sealed class RhinoCollection : ICollectionFixture<RhinoHost>;

/// <summary>One licensed RhinoCore and one STA host thread for the entire serial collection.</summary>
public sealed class RhinoHost : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread? _thread;
    private Exception? _startupError;
    public RhinoHost()
    {
        if (Environment.GetEnvironmentVariable("M2D_RHINO_TESTS") != "1") return;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            try
            {
                var resolver = Assembly.LoadFrom(Environment.GetEnvironmentVariable("M2D_RHINO_INSIDE")
                    ?? throw new InvalidOperationException("Set M2D_RHINO_INSIDE to the compatible resolver."));
                resolver.GetType("RhinoInside.Resolver", true)!.GetMethod("Initialize", new[] { typeof(string) })!
                    .Invoke(null, new object[] { Environment.GetEnvironmentVariable("M2D_RHINO_SYSTEM") ?? @"C:\Program Files\Rhino 8\System" });
                System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
                {
                    var path = Path.Combine(@"C:\Program Files\Rhino 8\Plug-ins\Grasshopper", name.Name + ".dll");
                    return File.Exists(path) ? Assembly.LoadFrom(path) : null;
                };
                var gha = Path.Combine(AppContext.BaseDirectory, "M2DPlus.Grasshopper.gha");
                System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
                    name.Name == "M2DPlus.Grasshopper" ? Assembly.LoadFrom(gha) : null;
                HostLoop(ready);
            }
            catch (Exception ex) { _startupError = ex; ready.Set(); }
        });
        _thread.SetApartmentState(ApartmentState.STA); _thread.Start(); ready.Wait();
        if (_startupError is not null) ExceptionDispatchInfo.Capture(_startupError).Throw();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void HostLoop(ManualResetEventSlim ready)
    {
        using var core = new RhinoCore(new[] { "/nosplash", "/notemplate" }, WindowStyle.NoWindow);
        using var primaryDocument = global::Rhino.RhinoDoc.CreateHeadless(null);
        global::Rhino.RhinoDoc.ActiveDoc = primaryDocument;
        InitializeGrasshopperContext();
        ready.Set(); foreach (var action in _queue.GetConsumingEnumerable()) action();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InitializeGrasshopperContext()
    {
        // GH retains a process-wide RhinoDocument witness. Headless NewDocument notifications
        // are absent, so keep its initial document alive for the full test host lifetime.
        global::Grasshopper.Kernel.GH_Document.EnableSolutions = true;
    }
    public void Run(Action action, TimeSpan? timeout = null)
    {
        Exception? failure = null; using var complete = new ManualResetEventSlim();
        _queue.Add(() => { try { action(); } catch (Exception ex) { failure = ex; } finally { complete.Set(); } });
        Assert.True(complete.Wait(timeout ?? TimeSpan.FromSeconds(90)), "Rhino test timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    public void Dispose() { _queue.CompleteAdding(); _thread?.Join(); _queue.Dispose(); }
}
