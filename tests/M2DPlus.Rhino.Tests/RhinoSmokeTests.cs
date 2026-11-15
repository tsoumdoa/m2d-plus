using System.Runtime.CompilerServices;
using Rhino.Geometry;
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

[Collection("Rhino")]
public sealed class RhinoSmokeTests(RhinoHost host)
{
    [RhinoFact]
    [Trait("Category", "RhinoIntegration")]
    public void LicensedRhinoInsideCreatesBrepAndReadsFrozenFixture()
    {
        host.Run(RunRhino);
    }

    // Prevent JIT from loading RhinoCommon before the resolver is initialized.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunRhino()
    {
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
