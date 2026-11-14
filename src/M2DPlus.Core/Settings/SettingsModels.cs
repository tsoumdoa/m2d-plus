using System.Collections.Immutable;
using M2DPlus.Core.Sections;

namespace M2DPlus.Core;

public enum DocumentUnits { Millimetres, Centimetres, Metres, Kilometres, Inches, Feet }
public enum SettingSource { Input, EditorView, EditorAll, Default, Auto }
public enum DiagnosticSeverity { Remark, Warning, Error }
public enum FarClipMode { None, Line, NoLine }
public enum CropMode { Auto, Off }
public enum GlassMode { Opaque, Clear }
public enum WeightMode { Auto, Layers }
public enum OutputTreeMode { Branch, Object }
public enum ViewSourceKind { Rectangle, Plane, Box, ClippingPlane, NamedView, Vector, Direction }
public enum DistanceMode { Auto, Off, Value }

/// <summary>Distance in document units, or an explicit Auto/Off instruction.</summary>
public sealed record DistanceSetting
{
    public DistanceMode Mode { get; }
    public double Value { get; }
    private DistanceSetting(DistanceMode mode, double value) { Mode = mode; Value = value; }
    public static DistanceSetting Auto { get; } = new(DistanceMode.Auto, 0);
    public static DistanceSetting Off { get; } = new(DistanceMode.Off, 0);
    public static DistanceSetting At(double value) => double.IsFinite(value) && value >= 0
        ? new(DistanceMode.Value, value) : throw new ArgumentOutOfRangeException(nameof(value));
}

public sealed record SettingsDiagnostic(DiagnosticSeverity Severity, string Message,
    int? ViewIndex = null, int? Branch = null, int? Line = null, string? Key = null);

/// <summary>Values resolved by the host before Core is called. Identity stays stable when named views reorder.</summary>
public sealed record ViewSettingsContext(string Identity, string Name, ViewSourceKind Source,
    bool IsCut, double SceneDepth, double DocumentTolerance, double? SourceDepth = null,
    string? AutomaticName = null);

/// <summary>A defensive immutable copy of authored key/value text; missing keys inherit.</summary>
public sealed class SettingsRow
{
    public ImmutableDictionary<string, string> Values { get; }
    public static SettingsRow Empty { get; } = new(Array.Empty<KeyValuePair<string, string>>());
    public SettingsRow(IEnumerable<KeyValuePair<string, string>> values) => Values = values
        .ToImmutableDictionary(p => p.Key.ToLowerInvariant(), p => p.Value, StringComparer.OrdinalIgnoreCase);
}

public sealed class EditorSettings
{
    public SettingsRow AllViews { get; }
    public ImmutableDictionary<string, SettingsRow> Views { get; }
    public static EditorSettings Empty { get; } = new(SettingsRow.Empty);
    public EditorSettings(SettingsRow allViews, IEnumerable<KeyValuePair<string, SettingsRow>>? views = null)
    {
        AllViews = allViews;
        Views = (views ?? Array.Empty<KeyValuePair<string, SettingsRow>>()).ToImmutableDictionary(StringComparer.Ordinal);
    }
}

/// <summary>Source describes the effective value; AssignedBy preserves input locks even for Input = auto.</summary>
public sealed record SettingEntry(object? Value, object AuthoredValue, string Text,
    SettingSource Source, SettingSource AssignedBy)
{
    public bool IsSetByInput => AssignedBy == SettingSource.Input;
    public string SourceDescription => IsSetByInput ? Source == SettingSource.Auto ? "auto (set by input)" : "set by input"
        : Source switch { SettingSource.EditorView => "editor view row", SettingSource.EditorAll => "editor All views row",
            SettingSource.Default => "default", _ => "auto" };
}

public sealed class EffectiveSettings
{
    public ViewSettingsContext View { get; }
    public ImmutableDictionary<string, SettingEntry> Values { get; }
    internal EffectiveSettings(ViewSettingsContext view, ImmutableDictionary<string, SettingEntry> values)
    { View = view; Values = values; }
    public SettingEntry this[string key] => Values[key];
    public T Get<T>(string key) => (T)Values[key].Value!;
    public double Depth => Get<double>("depth");
    public double? Beyond => (double?)Values["beyond"].Value;
    public FarClipMode FarClip => Get<FarClipMode>("farclip");
    public bool Fade => Get<bool>("fade");
    public double FadeStart => Get<double>("fade.start");
    public double FadeEnd => Get<double>("fade.end");
    public double FadeLimit => Get<double>("fade.limit");
    public bool Crop => Get<bool>("crop");
    public bool Hidden => Get<bool>("hidden");
    public bool Tangents => Get<bool>("tangents");
    public bool Seams => Get<bool>("seams");
    public GlassMode Glass => Get<GlassMode>("glass");
    public double Scale => Get<double>("scale");
    public WeightMode Weights => Get<WeightMode>("weights");
    public HatchMode Hatch => Get<HatchMode>("hatch");
    public CutMode Cuts => Get<CutMode>("cuts");
    public DistanceSetting Gap => Get<DistanceSetting>("gap");
    public string Name => Get<string>("name");
    public OutputTreeMode Tree => Get<OutputTreeMode>("tree");
    public double Tolerance => Get<double>("tolerance");
    public string Describe() => string.Join(Environment.NewLine, Settings.Keys.Select(k =>
        $"{k} = {Settings.FormatEffective(k, Values[k].Value)} ({Values[k].SourceDescription})"));
}

public sealed record SettingsResolution(ImmutableArray<EffectiveSettings> Views, ImmutableArray<SettingsDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}
