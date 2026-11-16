using System.Text.Json;
using Xunit;

namespace M2DPlus.Core.Tests;

public sealed class EngineSettingsTests
{
    private static ViewSettingsContext View(string id="a") => new(id,id,ViewSourceKind.Direction,false,20,.001);
    private static SettingsRow Row(string engine) => new([KeyValuePair.Create("engine",engine)]);
    private static EffectiveSettings Resolve(string text) => Settings.Resolve([View()],[[text]],DocumentUnits.Metres).Views[0];

    [Fact] public void EngineDefaultsToExactAndUsesTheNormalPrecedence()
    {
        var defaults=Settings.Resolve([View()],[],DocumentUnits.Metres).Views[0];
        Assert.Equal(EngineMode.Exact,defaults.Engine);
        var menu=new EditorSettings(Row("fast"));
        Assert.Equal(EngineMode.Fast,Settings.Resolve([View()],[],DocumentUnits.Metres,menu).Views[0].Engine);
        var objects=new EditorSettings(Row("fast"),[KeyValuePair.Create("a",Row("exact"))]);
        var objectValue=Settings.Resolve([View()],[],DocumentUnits.Metres,objects).Views[0];
        Assert.Equal(EngineMode.Exact,objectValue.Engine);Assert.Equal(SettingSource.EditorView,objectValue["engine"].Source);
        var input=Settings.Resolve([View(),View("b")],[["[a]\nengine=FAST"]],DocumentUnits.Metres,objects);
        Assert.Empty(input.Diagnostics);Assert.Equal(EngineMode.Fast,input.Views[0].Engine);
        Assert.Equal(SettingSource.Input,input.Views[0]["engine"].Source);
        Assert.Equal(SettingSource.EditorAll,input.Views[1]["engine"].Source);
        var invalid=Settings.Resolve([View()],[["engine=fats"]],DocumentUnits.Metres,objects);
        Assert.Equal(EngineMode.Exact,invalid.Views[0].Engine);
        Assert.Contains(invalid.Diagnostics,d=>d.Message.Contains("fast",StringComparison.Ordinal));
    }

    [Fact] public void EngineAndFastHiddenGenerationKeyVisibilityButExactHiddenRemainsPresentation()
    {
        var exactOff=Resolve("engine=exact\nhidden=off");var exactOn=Resolve("engine=exact\nhidden=on");
        var fastOff=Resolve("engine=fast\nhidden=off");var fastOn=Resolve("engine=fast\nhidden=on");
        Assert.Contains("engine",Settings.VisibilityKeys);
        Assert.Equal(exactOff.VisibilityKey(),exactOn.VisibilityKey());
        Assert.NotEqual(exactOff.VisibilityKey(),fastOff.VisibilityKey());
        Assert.NotEqual(fastOff.VisibilityKey(),fastOn.VisibilityKey());
        Assert.True(exactOff.ForVisibilitySolve().Hidden);Assert.True(fastOff.ForVisibilitySolve().Hidden);
        Assert.False(fastOff.ForVisibilitySolve().RequestedHidden);
        Assert.Equal(fastOff.VisibilityKey(),fastOff.ForVisibilitySolve().VisibilityKey());
        Assert.Equal(fastOff.VisibilityKey(),Resolve("engine=fast\nhidden=off\nscale=1:50\nmin visible=5 mm").VisibilityKey());
        Assert.False(fastOff.Hidden);Assert.Equal(SettingSource.Input,fastOff.ForVisibilitySolve()["hidden"].AssignedBy);
    }

    [Fact] public void FastEngineAndAuthorshipSurviveTheWorkerSettingsWhitelist()
    {
        var value=Resolve("engine=fast\nhidden=off");
        var options=new JsonSerializerOptions();options.Converters.Add(new WorkerSettingsConverter());
        var restored=JsonSerializer.Deserialize<EffectiveSettings>(JsonSerializer.Serialize(value,options),options)!;
        Assert.Equal(EngineMode.Fast,restored.Engine);Assert.False(restored.Hidden);
        Assert.Equal(value.VisibilityKey(),restored.VisibilityKey());
        Assert.Equal(value["engine"],restored["engine"]);
        var solve=JsonSerializer.Deserialize<EffectiveSettings>(JsonSerializer.Serialize(value.ForVisibilitySolve(),options),options)!;
        Assert.True(solve.Hidden);Assert.False(solve.RequestedHidden);Assert.Equal(value.VisibilityKey(),solve.VisibilityKey());
    }
}
