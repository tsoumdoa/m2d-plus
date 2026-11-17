using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace M2DPlus.Core;

public static partial class Settings
{
    private sealed record Candidate(object Value, SettingSource Source);

    /// <summary>Branches must already be in GH path order. View indices in headers are zero-based.
    /// Invalid assignments are reported individually; the last valid addressed assignment wins.
    /// <paramref name="depth"/> is the component's Depth input (UX rev. 4 §1.1): no items = not set, one item for
    /// every view, or one per view (null or blank items leave that view unset). It beats the Settings input's
    /// <c>depth</c>. A Box view (GH Box or M2D+ view box) always takes its depth from its geometry: any depth
    /// instruction is ignored there with a remark.</summary>
    public static SettingsResolution Resolve(IReadOnlyList<ViewSettingsContext> views,
        IEnumerable<IEnumerable<string>> inputBranches, DocumentUnits units, EditorSettings? editor = null,
        IReadOnlyList<string?>? depth = null)
    {
        _ = MetresPerUnit(units);
        editor ??= EditorSettings.Empty;
        var diagnostics = new List<SettingsDiagnostic>();
        var candidates = views.Select(view => Keys.ToDictionary(key => key,
            key => new List<Candidate> { new(DefaultValue(key, view), SettingSource.Default) })).ToArray();
        for (var i = 0; i < views.Count; i++)
        {
            var view = views[i];
            if (!double.IsFinite(view.SceneDepth) || view.SceneDepth < 0 || !double.IsFinite(view.DocumentTolerance) || view.DocumentTolerance <= 0 ||
                view.SourceDepth is double d && (!double.IsFinite(d) || d < 0))
                throw new ArgumentException("View depths must be finite and nonnegative; document tolerance must be positive.", nameof(views));
            ApplyRow(editor.AllViews, SettingSource.EditorAll, i);
            if (editor.Views.TryGetValue(view.Identity, out var row)) ApplyRow(row, SettingSource.EditorView, i);
        }

        var branches = inputBranches.Select(b => b.ToArray()).ToArray();
        if (branches.Length != 0 && branches.Length != 1 && branches.Length != views.Count)
            diagnostics.Add(new(DiagnosticSeverity.Error, "Give one Settings branch, or one per view"));
        else
            for (var branch = 0; branch < branches.Length; branch++)
            {
                var addressed = branches.Length == 1 ? Enumerable.Range(0, views.Count).ToArray() : [branch];
                var lines = string.Join('\n', branches[branch]).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                {
                    var line = lines[lineIndex].Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    if (line.StartsWith('['))
                    {
                        try { addressed = BindHeader(ReadHeader(line), views, diagnostics, branch, lineIndex + 1); }
                        catch (FormatException e)
                        {
                            addressed = [];
                            diagnostics.Add(new(DiagnosticSeverity.Warning, e.Message, Branch: branch, Line: lineIndex + 1));
                        }
                        continue;
                    }
                    line = StripComment(line).Trim();
                    var equals = line.IndexOf('=');
                    if (equals <= 0 || line[(equals + 1)..].Trim().Length == 0)
                    {
                        diagnostics.Add(new(DiagnosticSeverity.Warning, "Expected key = value.", Branch: branch, Line: lineIndex + 1));
                        continue;
                    }
                    var key = NormalizeKey(line[..equals]);
                    if (IsLegacyKey(key) && line[(equals + 1)..].Trim().Equals("default", StringComparison.OrdinalIgnoreCase)) continue;
                    if (TryValue(key, line[(equals + 1)..].Trim(), units, diagnostics, null, branch, lineIndex + 1, out var value))
                        foreach (var index in addressed) candidates[index][key].Add(new(value!, SettingSource.Input));
                }
            }

        depth ??= [];
        if (depth.Count > 1 && depth.Count != views.Count)
            diagnostics.Add(new(DiagnosticSeverity.Error, "Give one Depth, or one per view"));
        else
            for (var item = 0; item < depth.Count; item++)
            {
                if (string.IsNullOrWhiteSpace(depth[item])) continue;
                var addressed = depth.Count == 1 ? Enumerable.Range(0, views.Count).ToArray() : [item];
                try
                {
                    var value = ParseValue("depth", depth[item]!.Trim(), units);
                    foreach (var index in addressed) candidates[index]["depth"].Add(new(value, SettingSource.DepthInput));
                }
                catch (FormatException e)
                {
                    diagnostics.Add(new(DiagnosticSeverity.Warning, $"Invalid Depth input '{depth[item]}': {e.Message} Use auto or a nonnegative distance (for example 8 m).",
                        depth.Count == 1 ? null : item, Key: "depth"));
                }
            }

        var result = ImmutableArray.CreateBuilder<EffectiveSettings>();
        for (var i = 0; i < views.Count; i++)
        {
            var current = candidates[i];
            // Reject a contradictory start, falling back through its existing inheritance chain.
            while ((double)current["fade.start"][^1].Value > (double)current["fade.end"][^1].Value)
            {
                diagnostics.Add(new(DiagnosticSeverity.Warning, "fade.start must be <= fade.end; ignoring the conflicting start value.", i, Key: "fade.start"));
                current["fade.start"].RemoveAt(current["fade.start"].Count - 1);
            }
            var values = Keys.ToImmutableDictionary(k => k, k => MakeEntry(k, current[k][^1], views[i], units), StringComparer.OrdinalIgnoreCase);
            if (views[i].Source == ViewSourceKind.Box && views[i].SourceDepth is double boxDepth)
            {
                // The box's own geometry is the view volume: it wins over every depth instruction.
                var ignored = current["depth"].Where(c => c.Value is DistanceSetting { Mode: DistanceMode.Value }).Select(c => c.Source).Distinct().ToArray();
                if (ignored.Length > 0)
                    diagnostics.Add(new(DiagnosticSeverity.Remark,
                        $"'{views[i].Name}' is a box: its geometry sets the depth ({Numeric(boxDepth)}), so the {string.Join(" and ", ignored.Select(DepthOrigin))} {(ignored.Length > 1 ? "depths are" : "depth is")} ignored. Change the box instead (drag a view box's back face).",
                        i, Key: "depth"));
                values = values.SetItem("depth", new(boxDepth, DistanceSetting.Auto, "auto", SettingSource.Auto, SettingSource.Auto));
            }
            if (values["beyond"].Value is double beyond && values["farclip"].Value is not FarClipMode.None && beyond >= (double)values["depth"].Value!)
            {
                diagnostics.Add(new(DiagnosticSeverity.Remark, "Beyond is >= depth and is ignored while far clipping is enabled.", i, Key: "beyond"));
                values = values.SetItem("beyond", values["beyond"] with { Value = null });
            }
            if (views[i].Source is ViewSourceKind.Rectangle or ViewSourceKind.Box && values["crop"].AuthoredValue is CropMode.Off)
                diagnostics.Add(new(DiagnosticSeverity.Remark, "Rectangle and Box views always crop; crop = off applies to named views.", i, Key: "crop"));
            result.Add(new(views[i], values));
            if (TolerancePolicy.ScaleWarning((double)values["tolerance"].Value!, (double)values["scale"].Value!, units) is { } toleranceWarning)
                diagnostics.Add(new(DiagnosticSeverity.Warning, toleranceWarning, i, Key: "tolerance"));
        }
        return new(result.ToImmutable(), diagnostics.ToImmutableArray());

        void ApplyRow(SettingsRow row, SettingSource source, int index)
        {
            foreach (var (raw, text) in row.Values)
            {
                var key = NormalizeKey(raw);
                if (IsLegacyKey(key) && text.Trim().Equals("default", StringComparison.OrdinalIgnoreCase)) continue;
                if (TryValue(key, text, units, diagnostics, index, null, null, out var value))
                    candidates[index][key].Add(new(value!, source));
            }
        }
    }

    private static string DepthOrigin(SettingSource source) => source switch
    {
        SettingSource.DepthInput => "Depth input", SettingSource.Input => "Settings input",
        SettingSource.EditorView => "view object", _ => "component menu"
    };

    private static bool TryValue(string key, string text, DocumentUnits units, List<SettingsDiagnostic> diagnostics,
        int? view, int? branch, int? line, out object? value)
    {
        try
        {
            value = ParseValue(key, text, units);
            if (IsLegacyKey(key) && !text.Trim().Equals("default", StringComparison.OrdinalIgnoreCase) &&
                !diagnostics.Any(d => d.Key == key && d.Severity == DiagnosticSeverity.Remark))
                diagnostics.Add(new(DiagnosticSeverity.Remark, key == "engine" ? "engine is always Rhino HLD now" :
                    "joints is ignored; Rhino HLD controls contact edges", Key: key));
            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            var hint = key is "depth" or "beyond" or "gap" ? " Use auto or a nonnegative distance (for example 3 m)."
                : key == "min_visible" ? " Use off or a nonnegative distance (for example 2 mm)."
                : key.StartsWith("fade.", StringComparison.Ordinal) ? " Use 0–100 %." : "";
            diagnostics.Add(new(DiagnosticSeverity.Warning, $"Invalid {key} = {text}: {e.Message}{hint}", view, branch, line, key));
            value = null;
            return false;
        }
    }

    private static SettingEntry MakeEntry(string key, Candidate candidate, ViewSettingsContext view, DocumentUnits units)
    {
        var authored = candidate.Value;
        object? value = authored;
        var automatic = authored is DistanceSetting { Mode: DistanceMode.Auto } || key == "crop" && authored is CropMode.Auto ||
            key == "name" && (string)authored == "auto" || key == "tolerance" && candidate.Source == SettingSource.Default;
        if (key == "depth") value = authored is DistanceSetting { Mode: DistanceMode.Value } d ? d.Value
            : view.Source is ViewSourceKind.Box or ViewSourceKind.ClippingPlane ? view.SourceDepth ?? view.SceneDepth : view.SceneDepth;
        if (key == "beyond") value = authored is DistanceSetting { Mode: DistanceMode.Value } b ? b.Value
            : authored is DistanceSetting { Mode: DistanceMode.Auto } && view.IsCut ? 3 / MetresPerUnit(units) : null;
        if (key == "crop") value = view.Source is ViewSourceKind.Rectangle or ViewSourceKind.Box || view.Source == ViewSourceKind.NamedView && authored is CropMode.Auto;
        if (key == "name" && automatic) value = view.AutomaticName ?? view.Name;
        return new(value, authored, FormatEffective(key, authored), automatic ? SettingSource.Auto : candidate.Source, candidate.Source);
    }

    private static int[] BindHeader(string header, IReadOnlyList<ViewSettingsContext> views, List<SettingsDiagnostic> diagnostics, int branch, int line)
    {
        if (header == "*") return Enumerable.Range(0, views.Count).ToArray();
        if (header.StartsWith('#') && int.TryParse(header.AsSpan(1), out var index) && index >= 0 && index < views.Count) return [index];
        var matches = Enumerable.Range(0, views.Count).Where(i => string.Equals(views[i].Name, header, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1) return matches;
        var message = matches.Length > 1 ? $"View header '{header}' is ambiguous; use [#index] (zero-based)."
            : $"Unknown view '{header}'. {Suggest(header, views.Select(v => v.Name))} Existing views: {string.Join(", ", views.Select(v => v.Name))}.";
        diagnostics.Add(new(DiagnosticSeverity.Warning, message, Branch: branch, Line: line));
        return [];
    }

    /// <summary>Copies the effective authored instructions (including auto) into one Settings branch.</summary>
    public static string CopyAsText(IReadOnlyList<EffectiveSettings> views)
    {
        var text = new StringBuilder();
        for (var i = 0; i < views.Count; i++)
        {
            text.AppendLine($"[#{i}]");
            foreach (var key in ActiveKeys) text.AppendLine($"{key} = {views[i][key].Text}");
        }
        return text.ToString();
    }

    /// <summary>Copies editor rows only, preserving inheritance. Unused identity rows remain in EditorSettings.</summary>
    public static string CopyEditorAsText(EditorSettings editor, IReadOnlyList<ViewSettingsContext> views)
    {
        var text = new StringBuilder("[*]\n");
        Append(editor.AllViews);
        for (var i = 0; i < views.Count; i++)
            if (editor.Views.TryGetValue(views[i].Identity, out var row))
            {
                var unique = views.Count(v => string.Equals(v.Name, views[i].Name, StringComparison.OrdinalIgnoreCase)) == 1;
                text.AppendLine(unique && views[i].Name is not "*" && !views[i].Name.StartsWith('#')
                    ? $"[{JsonSerializer.Serialize(views[i].Name)}]" : $"[#{i}]");
                Append(row);
            }
        return text.ToString();
        void Append(SettingsRow row)
        {
            foreach (var key in ActiveKeys.Where(row.Values.ContainsKey))
            {
                var value = row.Values[key];
                if (key == "name" && !value.StartsWith('"')) value = JsonSerializer.Serialize(value);
                text.AppendLine($"{key} = {value}");
            }
        }
    }
}
