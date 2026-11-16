using System.Text.Json;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class CutOnlySettingsTests
{
    private static readonly ViewSettingsContext View = new("v", "Plan", ViewSourceKind.Plane, true, 20, .001);
    private static SettingsRow Row(string value) => new([KeyValuePair.Create("cutonly", value)]);
    private static EffectiveSettings Resolve(string text = "", EditorSettings? editor = null) =>
        Settings.Resolve([View], [[text]], DocumentUnits.Metres, editor).Views[0];

    [Theory] [InlineData("cutonly")] [InlineData("cut only")] [InlineData("Cut   Only")] [InlineData("cut_only")]
    public void NormalizedKeyParsesAndChangesVisibility(string key)
    {
        var off = Resolve(); var on = Resolve(key + " = on");
        Assert.False(off.CutOnly); Assert.True(on.CutOnly);
        Assert.Contains("cutonly", Settings.VisibilityKeys);
        Assert.NotEqual(off.VisibilityKey(), on.VisibilityKey());
        Assert.Equal(off.VisibilityKey(), Resolve("cutonly=off").VisibilityKey());
        Assert.True(on.ForVisibilitySolve().CutOnly);
    }

    [Fact] public void InputBeatsViewObjectBeatsMenuAndInvalidInputKeepsInheritedValue()
    {
        Assert.True(Resolve(editor: new(Row("on"))).CutOnly);
        var editor = new EditorSettings(Row("off"), [KeyValuePair.Create("v", Row("on"))]);
        var fromObject = Resolve(editor: editor);
        Assert.True(fromObject.CutOnly); Assert.Equal(SettingSource.EditorView, fromObject["cutonly"].Source);
        var fromInput = Resolve("cut only=off", editor);
        Assert.False(fromInput.CutOnly); Assert.Equal(SettingSource.Input, fromInput["cutonly"].Source);
        var invalid = Settings.Resolve([View], [["cutonly=maybe"]], DocumentUnits.Metres, editor);
        Assert.True(invalid.Views[0].CutOnly); Assert.Single(invalid.Diagnostics);
    }

    [Fact] public void ExistingWorkerSettingsWhitelistCarriesBooleanAndAuthorship()
    {
        var on = Resolve("cutonly=on");
        var options = new JsonSerializerOptions(); options.Converters.Add(new WorkerSettingsConverter());
        var copy = JsonSerializer.Deserialize<EffectiveSettings>(JsonSerializer.Serialize(on, options), options)!;
        Assert.True(copy.CutOnly); Assert.Equal(on["cutonly"], copy["cutonly"]);
        Assert.Equal(on.VisibilityKey(), copy.VisibilityKey());
    }
}
