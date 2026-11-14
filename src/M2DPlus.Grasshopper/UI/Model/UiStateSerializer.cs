using System.Text;
using System.Text.Json;
using M2DPlus.Core;

namespace M2DPlus.Grasshopper.UI.Model;

/// <summary>Compact, versioned JSON stored in the .gh (one GH string item). Defaults are omitted.
/// Version 1 layout: {"v":1,"all":{key:text},"views":{identity:{key:text}},"auto":false,"preview":[...]}.
/// Unknown row keys, unknown preview names and unknown top-level properties are preserved verbatim,
/// so a definition saved by a newer M2D+ keeps its data through an edit in this version.</summary>
public static class UiStateSerializer
{
    public const int CurrentVersion = 1;
    public const string ChunkName = "M2DPlus.UiState";
    private static readonly string[] Known = ["v", "all", "views", "auto", "preview"];

    public static string Write(ComponentUiState state)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteNumber("v", CurrentVersion);
            if (state.AllViews.Values.Count > 0) { json.WritePropertyName("all"); WriteRow(json, state.AllViews); }
            if (state.Views.Count > 0)
            {
                json.WriteStartObject("views");
                foreach (var (identity, row) in state.Views) { json.WritePropertyName(identity); WriteRow(json, row); }
                json.WriteEndObject();
            }
            if (!state.AutoRun) json.WriteBoolean("auto", false);
            if (!state.Preview.SetEquals(PreviewLayers.Default))
            {
                json.WriteStartArray("preview");
                foreach (var layer in state.Preview) json.WriteStringValue(layer);
                json.WriteEndArray();
            }
            foreach (var (name, raw) in state.Extras)
            {
                json.WritePropertyName(name);
                using var document = JsonDocument.Parse(raw);
                document.RootElement.WriteTo(json);
            }
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteRow(Utf8JsonWriter json, SettingsRow row)
    {
        json.WriteStartObject();
        // Settings.Keys order first (readable), then unknown keys alphabetically.
        foreach (var key in Settings.Keys.Where(row.Values.ContainsKey)
            .Concat(row.Values.Keys.Where(k => !Settings.Keys.Contains(k)).Order(StringComparer.Ordinal)))
            json.WriteString(key, row.Values[key]);
        json.WriteEndObject();
    }

    /// <summary>Never throws for bad content: returns defaults and an explanation instead.</summary>
    public static ComponentUiState Read(string? text, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(text)) return ComponentUiState.Default;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Expected a JSON object.");
            var version = root.TryGetProperty("v", out var v) && v.TryGetInt32(out var number) ? number : 0;
            if (version < 1) throw new FormatException("Missing version.");
            var all = root.TryGetProperty("all", out var a) ? ReadRow(a) : SettingsRow.Empty;
            var views = new List<KeyValuePair<string, SettingsRow>>();
            if (root.TryGetProperty("views", out var vs) && vs.ValueKind == JsonValueKind.Object)
                foreach (var view in vs.EnumerateObject()) views.Add(new(view.Name, ReadRow(view.Value)));
            var auto = !root.TryGetProperty("auto", out var r) || r.ValueKind != JsonValueKind.False;
            IEnumerable<string>? preview = null;
            if (root.TryGetProperty("preview", out var p) && p.ValueKind == JsonValueKind.Array)
                preview = p.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray();
            var extras = root.EnumerateObject().Where(o => !Known.Contains(o.Name))
                .Select(o => KeyValuePair.Create(o.Name, o.Value.GetRawText())).ToArray();
            if (version > CurrentVersion)
                problem = $"These editor settings were saved by a newer M2D+ (format {version}). Values this version understands are used; the rest are kept.";
            return new(all, views, auto, preview, extras);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            problem = "The stored editor settings could not be read, so defaults are used: " + e.Message;
            return ComponentUiState.Default;
        }
    }

    private static SettingsRow ReadRow(JsonElement element) => element.ValueKind != JsonValueKind.Object ? SettingsRow.Empty
        : new(element.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
            .Select(p => KeyValuePair.Create(p.Name, p.Value.GetString()!)));
}
