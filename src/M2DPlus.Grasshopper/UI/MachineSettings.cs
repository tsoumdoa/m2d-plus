using Grasshopper.Kernel;
using M2DPlus.Core;
using M2DPlus.Grasshopper.UI.Model;
using M2DPlus.Rhino.Services;

namespace M2DPlus.Grasshopper.UI;

/// <summary>Per-user machine preferences, outside .gh state. Only touched on the GH UI thread.</summary>
internal static class MachineSettings
{
    private const string ParallelViewsKey = "ParallelViews";
    private static readonly Lazy<GH_SettingsServer> Store = new(() =>
    {
        var server = new GH_SettingsServer("M2DPlus");
        ParallelViewsPreference.SetPersistedValue(server.GetValue(ParallelViewsKey, 0));
        return server;
    });

    // A settings-store failure must never stop the plug-in loading or the menu opening: fall back to Auto.
    public static void Initialize() { try { _ = Store.Value; } catch (Exception) { } }
    public static ParallelViewsChoice Current { get { Initialize(); return ParallelViewsPreference.Current; } }

    public static void Execute(string value)
    {
        var choice = Current;
        if (choice.EnvironmentLocked) return;
        var count = 0; // Auto, also the missing-value default.
        if (value != "auto" && !ParallelViewsPolicy.TryValidate(value, choice.CpuLimit, out count, out _)) return;
        ParallelViewsPreference.SetPersistedValue(count);
        try { Store.Value.SetValue(ParallelViewsKey, count); Store.Value.WritePersistentSettings(); }
        catch (Exception) { } // Still applies for this session if the store can't be written.
        // No solution expiration: the shared pool reads this value at each batch. Existing drawings stay cached.
    }
}
