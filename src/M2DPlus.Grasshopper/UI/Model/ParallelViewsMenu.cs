using System.Collections.Immutable;
using System.Globalization;
using M2DPlus.Core;

namespace M2DPlus.Grasshopper.UI.Model;

public sealed record ParallelViewsTextEntry(string Text, int CpuLimit) : IMenuTextEntry
{
    public string PromptSuffix => " (whole number; Enter)";
    public bool TryAccept(string text, out string? command, out string? error)
    {
        var valid = ParallelViewsPolicy.TryValidate(text, CpuLimit, out var value, out error);
        command = valid ? ParallelViewsMenu.Command(value) : null;
        return valid;
    }
}

public static class ParallelViewsMenu
{
    public const string CommandPrefix = "parallel-views:";
    public const string Help = "Machine setting shared by all M2D+ components; applies to the next run without restarting Rhino. " +
        "Helps drawing sets of 2+ views only. More views run at once if memory allows; the memory check may start fewer. " +
        "Each view uses its own background Rhino with a full copy of the scene. Each view gets somewhat slower, but the whole set finishes sooner. " +
        "Measured on 32 threads: 4 plans took 217 s with 2 workers and 131 s with 4.";
    public static string Command(int? value) => CommandPrefix + (value?.ToString(CultureInfo.InvariantCulture) ?? "auto");

    public static MenuNode Build(ParallelViewsChoice choice)
    {
        var locked = choice.EnvironmentLocked;
        var tooltip = locked ? $"Locked by environment variable {ParallelViewsPolicy.EnvironmentVariable}; resolved limit {choice.Limit}. Unset it to use this menu. " + Help : Help;
        var current = locked ? choice.Limit : choice.PersistedValue;
        var presets = new[] { 1, 2, 4, 6, 8 }.Where(n => n <= choice.CpuLimit).ToArray();
        var items = new List<MenuNode>
        {
            new($"Auto ({choice.AutoLimit})", Command(null), !locked, !locked && current is null, Tooltip: tooltip)
        };
        // An env override can resolve to a non-preset. Show that number explicitly as the checked value.
        if (locked && !presets.Contains(choice.Limit)) items.Add(new(choice.Limit.ToString(CultureInfo.InvariantCulture), Enabled: false, Checked: true, Tooltip: tooltip));
        items.AddRange(presets.Select(n => new MenuNode(n.ToString(CultureInfo.InvariantCulture), Command(n), !locked, current == n, Tooltip: tooltip)));
        items.Add(new("Custom…", Enabled: !locked, Checked: !locked && current is not null && !presets.Contains(current.Value),
            Hint: $"Enter an integer from 1 to {choice.CpuLimit}; Enter to apply.", Tooltip: tooltip,
            TextEntry: new ParallelViewsTextEntry((current ?? choice.Limit).ToString(CultureInfo.InvariantCulture), choice.CpuLimit)));
        return new(locked ? "Parallel views (locked by env)" : "Parallel views", Hint: "machine setting", Children: items.ToImmutableArray(), Tooltip: tooltip);
    }
}
