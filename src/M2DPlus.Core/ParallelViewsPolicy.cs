using System.Globalization;

namespace M2DPlus.Core;

public sealed record ParallelViewsChoice(int Limit, int CpuLimit, int AutoLimit, int? PersistedValue, bool EnvironmentLocked);

/// <summary>Machine execution preference only; never part of drawing settings or cache identity.</summary>
public static class ParallelViewsPolicy
{
    public const string EnvironmentVariable = "M2DPLUS_HLD_WORKERS";

    public static ParallelViewsChoice Resolve(int logicalCores, string? environmentValue, int? persistedValue)
    {
        var cpu = WorkerMemoryPolicy.CpuLimit(logicalCores);
        var auto = WorkerMemoryPolicy.ThreadedHldLimit(logicalCores);
        var valid = persistedValue is >= 1 && persistedValue <= cpu ? persistedValue : null;
        // Presence locks the preference, even for malformed overrides (which retain Auto).
        var limit = environmentValue is not null ? WorkerMemoryPolicy.ThreadedHldLimit(logicalCores, environmentValue) : valid ?? auto;
        return new(limit, cpu, auto, valid, environmentValue is not null);
    }

    public static bool TryValidate(string text, int cpuLimit, out int value, out string? error)
    {
        var valid = int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1 && value <= cpuLimit;
        error = valid ? null : $"Enter a whole number from 1 to {cpuLimit}.";
        return valid;
    }
}
