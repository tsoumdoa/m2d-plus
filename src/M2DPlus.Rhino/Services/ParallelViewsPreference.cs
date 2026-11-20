using M2DPlus.Core;

namespace M2DPlus.Rhino.Services;

/// <summary>The UI publishes its persisted machine preference; background batches read only plain values.</summary>
public static class ParallelViewsPreference
{
    private static int _persistedValue;
    public static void SetPersistedValue(int value) => Volatile.Write(ref _persistedValue, value);
    public static ParallelViewsChoice Current => ParallelViewsPolicy.Resolve(Environment.ProcessorCount,
        Environment.GetEnvironmentVariable(ParallelViewsPolicy.EnvironmentVariable), Volatile.Read(ref _persistedValue));
}
