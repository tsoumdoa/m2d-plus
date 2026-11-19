using System.Collections.Immutable;
using System.Globalization;
using M2DPlus.Core;

namespace M2DPlus.Grasshopper.UI.Model;

/// <summary>Custom tolerance entry, using the same resolver/validation as Settings and view-object user text.</summary>
public sealed record ToleranceTextEntry(string Text, DocumentUnits Units)
{
    public bool TryAccept(string text, out string? command, out string? error)
    {
        // Use a stored row, not Settings text: embedded newlines/assignments must never become other settings.
        var probe = new ViewSettingsContext("probe", "Probe", ViewSourceKind.Plane, false, 1, .001);
        var result = Settings.Resolve([probe], [], Units,
            new EditorSettings(new SettingsRow([KeyValuePair.Create("tolerance", text)])));
        var invalid = result.Diagnostics.FirstOrDefault(d => d.Key == "tolerance" && d.Message.StartsWith("Invalid ", StringComparison.Ordinal));
        error = invalid?.Message.Replace('\r', ' ').Replace('\n', ' ');
        command = error is null ? MenuCommands.SetValue("tolerance", SettingsCatalog.Canonical("tolerance", text)) : null;
        return error is null;
    }
}

/// <summary>Scale advice and the custom entry on the shared Choice menu; all numerical advice comes from Core.</summary>
public static class ToleranceMenu
{
    public const string EntryHint = "Enter to apply; bare numbers use document units. Units such as 0.02 mm are accepted; document resets the default.";
    private static string N(double value) => value.ToString("G6", CultureInfo.InvariantCulture);
    private static string Scale(double value) => "1:" + N(value);

    public static MenuNode Build(SettingsPresentation settings)
    {
        var field = settings.Field("tolerance");
        var units = settings.Context.Units;
        var effective = settings.Resolution?.Views ?? [];
        var scales = effective.Select(v => v.Scale).Distinct().Order().ToArray();
        if (scales.Length == 0) scales = [SettingsCatalog.ScaleDenominator(settings.Field("scale").Text) ?? 100];
        var guidance = string.Join("; ", scales.Select(s => $"Scale {Scale(s)} → use ≤ {N(TolerancePolicy.RecommendedMillimetres(s))} mm"));
        var detail = TolerancePolicy.DetailScaleMaximum;
        var coarse = TolerancePolicy.CoarseScaleMinimum;
        var bands = $"1:1–{Scale(detail)} → ≤ {N(TolerancePolicy.RecommendedMillimetres(detail))} mm; " +
            $"above {Scale(detail)} and below {Scale(coarse)} → ≤ {N(TolerancePolicy.RecommendedMillimetres(detail + 1))} mm; " +
            $"{Scale(coarse)} and coarser → ≤ {N(TolerancePolicy.RecommendedMillimetres(coarse))} mm.";
        var menu = ComponentMenu.Choice(settings, "Tolerance", "tolerance");
        var children = menu.Children.Select(child =>
        {
            if (child.Command is null || !MenuCommands.TryParseSet(child.Command, out _, out var value) || value == "document") return child;
            var mm = Settings.ParseDistance(value, DocumentUnits.Millimetres);
            var suits = TolerancePolicy.ScaleWarning(mm, detail, DocumentUnits.Millimetres) is null ? "Suits every scale (1:1 and coarser)."
                : TolerancePolicy.ScaleWarning(mm, detail + 1, DocumentUnits.Millimetres) is null ? $"Suits scales above {Scale(detail)} (for example 1:10, 1:20) and coarser."
                : $"Suits {Scale(coarse)} and coarser.";
            var warnings = scales.Select(s => TolerancePolicy.ScaleWarning(mm, s, DocumentUnits.Millimetres)).Where(w => w is not null);
            return child with { Tooltip = string.Join(" ", new[] { suits }.Concat(warnings)) };
        }).ToList();
        var values = effective.Select(v => v.Tolerance).Distinct().ToArray();
        var text = values.Length == 1 ? values[0].ToString("R", CultureInfo.InvariantCulture)
            : values.Length == 0 ? settings.Context.DocumentTolerance.ToString("R", CultureInfo.InvariantCulture) : "varies";
        // Presets remain editable under view-object overrides, like other Choice fields. Custom entry is stricter.
        children.Add(new("Custom…", Enabled: field.InputViews == 0 && field.ObjectViews == 0 && !field.Locked,
            Hint: EntryHint, TextEntry: new(text, units)));
        return menu with { Children = children.ToImmutableArray(), Tooltip = SettingsCatalog.Spec("tolerance").Tooltip + " " + bands + " " + guidance };
    }
}
