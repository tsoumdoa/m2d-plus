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
        var menu=new EditorSettings(Row("native"));
        Assert.Equal(EngineMode.Native,Settings.Resolve([View()],[],DocumentUnits.Metres,menu).Views[0].Engine);
        var objects=new EditorSettings(Row("native"),[KeyValuePair.Create("a",Row("exact"))]);
        var objectValue=Settings.Resolve([View()],[],DocumentUnits.Metres,objects).Views[0];
        Assert.Equal(EngineMode.Exact,objectValue.Engine);Assert.Equal(SettingSource.EditorView,objectValue["engine"].Source);
        var input=Settings.Resolve([View(),View("b")],[["[a]\nengine=NATIVE"]],DocumentUnits.Metres,objects);
        Assert.Empty(input.Diagnostics);Assert.Equal(EngineMode.Native,input.Views[0].Engine);
        Assert.Equal(SettingSource.Input,input.Views[0]["engine"].Source);
        Assert.Equal(SettingSource.EditorAll,input.Views[1]["engine"].Source);
        var invalid=Settings.Resolve([View()],[["engine=fats"]],DocumentUnits.Metres,objects);
        Assert.Equal(EngineMode.Exact,invalid.Views[0].Engine);
        Assert.Contains(invalid.Diagnostics,d=>d.Severity==DiagnosticSeverity.Warning && d.Key=="engine");
    }

    [Theory] [InlineData("fast")] [InlineData("FAST")] [InlineData(" fast ")]
    public void SavedFastIsNativeWithARemarkInEverySource(string alias)
    {
        var input=Settings.Resolve([View()],[[$"engine={alias}"]],DocumentUnits.Metres);
        var menu=Settings.Resolve([View()],[],DocumentUnits.Metres,new EditorSettings(Row(alias)));
        var obj=Settings.Resolve([View()],[],DocumentUnits.Metres,new EditorSettings(SettingsRow.Empty,[KeyValuePair.Create("a",Row(alias))]));
        foreach(var result in new[]{input,menu,obj})
        {
            Assert.False(result.HasErrors);Assert.Equal(EngineMode.Native,result.Views[0].Engine);
            Assert.Equal(Resolve("engine=native").VisibilityKey(),result.Views[0].VisibilityKey());
            var note=Assert.Single(result.Diagnostics);Assert.Equal(DiagnosticSeverity.Remark,note.Severity);
            Assert.Equal("engine = fast is now engine = native",note.Message);
            Assert.Contains("engine = native",Settings.CopyAsText(result.Views));
        }
    }

    [Fact] public void EngineAndNativeHiddenGenerationKeyVisibilityButExactHiddenRemainsPresentation()
    {
        var exactOff=Resolve("engine=exact\nhidden=off");var exactOn=Resolve("engine=exact\nhidden=on");
        var nativeOff=Resolve("engine=native\nhidden=off");var nativeOn=Resolve("engine=native\nhidden=on");
        Assert.Contains("engine",Settings.VisibilityKeys);
        Assert.Equal(exactOff.VisibilityKey(),exactOn.VisibilityKey());
        Assert.NotEqual(exactOff.VisibilityKey(),nativeOff.VisibilityKey());
        Assert.NotEqual(nativeOff.VisibilityKey(),nativeOn.VisibilityKey());
        Assert.True(exactOff.ForVisibilitySolve().Hidden);Assert.True(nativeOff.ForVisibilitySolve().Hidden);
        Assert.False(nativeOff.ForVisibilitySolve().RequestedHidden);
        Assert.Equal(nativeOff.VisibilityKey(),nativeOff.ForVisibilitySolve().VisibilityKey());
        Assert.Equal(nativeOff.VisibilityKey(),Resolve("engine=native\nhidden=off\nscale=1:50\nmin visible=5 mm").VisibilityKey());
        Assert.False(nativeOff.Hidden);Assert.Equal(SettingSource.Input,nativeOff.ForVisibilitySolve()["hidden"].AssignedBy);
    }

    [Fact] public void NativeEngineAndAuthorshipSurviveTheWorkerSettingsWhitelist()
    {
        var value=Resolve("engine=native\nhidden=off");
        var options=new JsonSerializerOptions();options.Converters.Add(new WorkerSettingsConverter());
        var restored=JsonSerializer.Deserialize<EffectiveSettings>(JsonSerializer.Serialize(value,options),options)!;
        Assert.Equal(EngineMode.Native,restored.Engine);Assert.False(restored.Hidden);
        Assert.Equal(value.VisibilityKey(),restored.VisibilityKey());
        Assert.Equal(value["engine"],restored["engine"]);
        var solve=JsonSerializer.Deserialize<EffectiveSettings>(JsonSerializer.Serialize(value.ForVisibilitySolve(),options),options)!;
        Assert.True(solve.Hidden);Assert.False(solve.RequestedHidden);Assert.Equal(value.VisibilityKey(),solve.VisibilityKey());
    }
}
