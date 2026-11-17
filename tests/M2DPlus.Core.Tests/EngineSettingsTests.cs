using System.Text.Json;
using Xunit;
namespace M2DPlus.Core.Tests;
public sealed class EngineSettingsTests
{
    private static ViewSettingsContext View => new("v","v",ViewSourceKind.Direction,false,20,.001);
    private static SettingsResolution Resolve(params string[] text) => Settings.Resolve([View],[text],DocumentUnits.Metres);
    [Theory] [InlineData("engine=exact")] [InlineData("engine=native")] [InlineData("engine=fast")] [InlineData("joints=off")] [InlineData("joints=on")]
    public void LegacyValuesLoadButDoNotChangeComputeOrRunIdentity(string text)
    {
        var result=Resolve(text);Assert.False(result.HasErrors);Assert.Equal(Resolve().Views[0].VisibilityKey(),result.Views[0].VisibilityKey());
        Assert.Equal(DiagnosticSeverity.Remark,Assert.Single(result.Diagnostics).Severity);
        Assert.Equal(Settings.RunText(Array.Empty<string>()),Settings.RunText([text]));
        Assert.DoesNotContain("engine =",Settings.CopyAsText(result.Views));Assert.DoesNotContain("joints =",Settings.CopyAsText(result.Views));
    }
    [Fact] public void DefaultsAreQuietAndExplicitInstructionsDeduplicateAcrossSources()
    {
        Assert.Empty(Resolve().Diagnostics);Assert.Empty(Resolve("engine=default","joints=default").Diagnostics);
        var row=new SettingsRow([KeyValuePair.Create("engine","fast"),KeyValuePair.Create("joints","off")]);
        var result=Settings.Resolve([View],[new[]{"engine=native","engine=exact","joints=on"}],DocumentUnits.Metres,new(row,[KeyValuePair.Create("v",row)]));
        Assert.Equal(2,result.Diagnostics.Length);Assert.All(result.Diagnostics,d=>Assert.Equal(DiagnosticSeverity.Remark,d.Severity));
    }
    [Fact] public void MultilineLegacyTextDoesNotEraseActiveRunSettings()
    {
        Assert.Equal(Settings.RunText(["hidden=off"]),Settings.RunText(["engine=fast\nhidden=off\njoints=off"]));
    }
    [Fact] public void HiddenInvalidatesProjectionAndAuthoredValuesRoundTrip()
    {
        var on=Resolve("hidden=on").Views[0];var off=Resolve("engine=fast","hidden=off").Views[0];
        Assert.NotEqual(on.VisibilityKey(),off.VisibilityKey());Assert.False(off.Hidden);
        var options=new JsonSerializerOptions();options.Converters.Add(new WorkerSettingsConverter());
        var restored=JsonSerializer.Deserialize<EffectiveSettings>(JsonSerializer.Serialize(off,options),options)!;
        Assert.Equal(off["engine"],restored["engine"]);Assert.Equal(off.VisibilityKey(),restored.VisibilityKey());Assert.False(restored.Hidden);
        Assert.Equal(Resolve("cutonly=on","hidden=off","tangents=on","seams=on").Views[0].VisibilityKey(),Resolve("cutonly=on").Views[0].VisibilityKey());
    }
    [Fact] public void CutOnlyStillKeysGlassForExcludedForegroundRegionAdmission()
    {
        Assert.NotEqual(Resolve("cutonly=on","glass=opaque").Views[0].VisibilityKey(),Resolve("cutonly=on","glass=clear").Views[0].VisibilityKey());
    }
}
