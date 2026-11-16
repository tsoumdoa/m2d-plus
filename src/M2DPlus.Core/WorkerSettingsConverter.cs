using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using M2DPlus.Core.Sections;

namespace M2DPlus.Core;

/// <summary>Explicit value types only; never accepts CLR type names from a peer.</summary>
public sealed class WorkerSettingsConverter : JsonConverter<EffectiveSettings>
{
    private sealed record Value(string Kind, JsonElement Data);
    private sealed record Entry(Value Effective, Value Authored, string Text, SettingSource Source, SettingSource AssignedBy);
    private sealed record Snapshot(ViewSettingsContext View, Dictionary<string, Entry> Values);
    private static readonly Type[] Types = [typeof(string), typeof(bool), typeof(double), typeof(DistanceSetting),
        typeof(FarClipMode), typeof(CropMode), typeof(GlassMode), typeof(WeightMode), typeof(HatchMode), typeof(CutMode), typeof(OutputTreeMode), typeof(EngineMode), typeof(BakeLayout)];
    private static Value Pack(object? value) => new(value?.GetType().Name ?? "null", JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object)));
    private static object? Unpack(Value value)
    {
        if (value.Kind == "null") return null;
        if (value.Kind == nameof(DistanceSetting))
        {
            var mode = (DistanceMode)value.Data.GetProperty("Mode").GetInt32();
            return mode switch { DistanceMode.Auto => DistanceSetting.Auto, DistanceMode.Off => DistanceSetting.Off,
                DistanceMode.Value => DistanceSetting.At(value.Data.GetProperty("Value").GetDouble()), _ => throw new JsonException("Invalid distance mode") };
        }
        var type = Types.SingleOrDefault(t => t.Name == value.Kind) ?? throw new JsonException("Invalid setting type");
        return value.Data.Deserialize(type);
    }
    public override EffectiveSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var snapshot = JsonSerializer.Deserialize<Snapshot>(ref reader, options) ?? throw new JsonException();
        return new(snapshot.View, snapshot.Values.ToImmutableDictionary(p => p.Key,
            p => new SettingEntry(Unpack(p.Value.Effective), Unpack(p.Value.Authored)!, p.Value.Text, p.Value.Source, p.Value.AssignedBy)));
    }
    public override void Write(Utf8JsonWriter writer, EffectiveSettings value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new Snapshot(value.View, value.Values.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key,
            p => new Entry(Pack(p.Value.Value), Pack(p.Value.AuthoredValue), p.Value.Text, p.Value.Source, p.Value.AssignedBy))), options);
}
