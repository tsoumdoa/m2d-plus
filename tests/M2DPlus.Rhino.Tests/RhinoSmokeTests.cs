using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true, MaxParallelThreads = 1)]

namespace M2DPlus.Rhino.Tests;

/// <summary>Opt-in at discovery time: ordinary dotnet test never initializes Rhino or its resolver.</summary>
public sealed class RhinoFactAttribute : FactAttribute
{
    public RhinoFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("M2D_RHINO_TESTS") != "1")
            Skip = "Licensed Rhino.Inside integration: set M2D_RHINO_TESTS=1 explicitly (see README.md).";
    }
}

public sealed class RhinoSmokeTests
{
    [RhinoFact]
    [Trait("Category", "RhinoIntegration")]
    public void LicensedRhinoInsideCreatesBrepAndReadsFrozenFixture()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var resolverPath = Environment.GetEnvironmentVariable("M2D_RHINO_INSIDE")
                    ?? throw new InvalidOperationException("Set M2D_RHINO_INSIDE to a runtime-compatible Rhino.Inside resolver DLL.");
                var systemDirectory = Environment.GetEnvironmentVariable("M2D_RHINO_SYSTEM")
                    ?? @"C:\Program Files\Rhino 8\System";
                var resolver = Assembly.LoadFrom(Path.GetFullPath(resolverPath));
                resolver.GetType("RhinoInside.Resolver", throwOnError: true)!
                    .GetMethod("Initialize", new[] { typeof(string) })!
                    .Invoke(null, new object[] { systemDirectory });
                RunRhino();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    // Prevent JIT from loading RhinoCommon before the resolver is initialized.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunRhino()
    {
        using var core = new RhinoCore(new[] { "/nosplash", "/notemplate" }, WindowStyle.NoWindow);
        using var brep = Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(1, 2, 3)));
        Assert.NotNull(brep);
        Assert.True(brep.IsValid);
        Assert.True(brep.IsSolid);
        using var mass = VolumeMassProperties.Compute(brep);
        Assert.Equal(6, mass.Volume, 8);
        using var fixture = global::Rhino.FileIO.File3dm.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "brep_box.3dm"));
        Assert.NotNull(fixture);
        Assert.Equal(global::Rhino.UnitSystem.Meters, fixture.Settings.ModelUnitSystem);
        Assert.Contains(fixture.Objects, o => o.Geometry is Brep b && b.IsValid && b.IsSolid);
    }
}
