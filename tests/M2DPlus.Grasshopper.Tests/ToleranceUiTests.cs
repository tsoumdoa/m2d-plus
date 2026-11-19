using M2DPlus.Core;
using M2DPlus.Grasshopper.UI.Model;
using Xunit;

namespace M2DPlus.Grasshopper.Tests;

public sealed class ToleranceUiTests
{
    private static UiContext Context(DocumentUnits units = DocumentUnits.Millimetres, params string[] input) =>
        new([new("a", "A", ViewSourceKind.Plane, false, 20, .001)], input.Length == 0 ? [] : [input], units);

    private static MenuNode Item(SettingsPresentation presentation) => ComponentMenu.Build(new(presentation,
        new(DrawingPhase.Done), HasInfo: true)).Single(n => n.Label.StartsWith("Tolerance", StringComparison.Ordinal));

    [Fact]
    public void DefaultMenuShowsDocumentValueAndPhysicalPresets()
    {
        var item = Item(new(ComponentUiState.Default, Context()));
        Assert.Equal(["Document (0.001 mm)", "0.001 mm", "0.01 mm", "0.05 mm", "0.1 mm", "1 mm", "Custom…"], item.Children.Select(n => n.Label));
        Assert.Equal([true, false, false, false, false, false, false], item.Children.Select(n => n.Checked));
        Assert.Equal(MenuCommands.SetValue("tolerance", "document"), item.Children[0].Command);
        Assert.Equal("tolerance = document", item.Hint);
        Assert.Contains("never changes the document", item.Tooltip);
        Assert.Contains("2 × tolerance", item.Tooltip);
    }

    [Theory]
    [InlineData("0.001 mm")]
    [InlineData("0.01 mm")]
    [InlineData("0.05 mm")]
    [InlineData("0.1 mm")]
    [InlineData("1 mm")]
    public void EveryPresetIsCheckedAfterChoosingItsCommand(string preset)
    {
        var choice = Item(new(ComponentUiState.Default, Context())).Children.Single(n => n.Label == preset);
        Assert.True(MenuCommands.TryParseSet(choice.Command!, out var key, out var value));
        var presentation = new SettingsPresentation(ComponentUiState.Default.With(key, value), Context());
        Assert.Equal(preset, Assert.Single(Item(presentation).Children.Where(n => n.Checked)).Label);
        Assert.Equal(Settings.ParseDistance(preset, DocumentUnits.Millimetres), presentation.Resolution!.Views[0].Tolerance);
    }

    [Theory]
    [InlineData(DocumentUnits.Metres, .00005, "m")]
    [InlineData(DocumentUnits.Centimetres, .005, "cm")]
    [InlineData(DocumentUnits.Inches, .001968503937007874, "in")]
    [InlineData(DocumentUnits.Feet, .00016404199475065617, "ft")]
    public void PresetsStayInMillimetresAndResolveInDocumentUnits(DocumentUnits units, double expected, string suffix)
    {
        var presentation = new SettingsPresentation(ComponentUiState.Default.With("tolerance", "0.05 mm"), Context(units));
        Assert.Equal("0.05 mm", presentation.State.Menu.Values["tolerance"]);
        Assert.Equal(expected, presentation.Resolution!.Views[0].Tolerance, 12);
        Assert.Equal("0.05 mm", Assert.Single(Item(presentation).Children.Where(n => n.Checked)).Label);
        Assert.Equal($"Document (0.001 {suffix})", Item(presentation).Children[0].Label);
        // Input values are canonical document-unit numbers: still check the physical preset.
        var input = Item(new(ComponentUiState.Default, Context(units, "tolerance=0.05 mm")));
        Assert.Equal("0.05 mm", Assert.Single(input.Children.Where(n => n.Checked)).Label);
    }

    [Fact]
    public void DocumentLabelExistsBeforeViewsAndPreservesSmallValues()
    {
        var context = new UiContext([], [], DocumentUnits.Metres) { DocumentTolerance = .000001 };
        var item = Item(new(ComponentUiState.Default, context));
        Assert.Equal("Document (1E-06 m)", item.Children[0].Label);
        Assert.True(item.Children[0].Checked);
    }

    [Theory]
    [InlineData("0.025 mm", false)]
    [InlineData("0.025", true)]
    public void TypedNonPresetShowsAsCheckedExtraItem(string text, bool input)
    {
        var presentation = new SettingsPresentation(input ? ComponentUiState.Default : ComponentUiState.Default.With("tolerance", text),
            input ? Context(DocumentUnits.Millimetres, "tolerance=" + text) : Context());
        var item = Item(presentation);
        Assert.Equal(text, item.Children[0].Label);
        Assert.True(item.Children[0].Checked);
        Assert.False(item.Children[0].Enabled);
        Assert.Null(item.Children[0].Command);
        Assert.DoesNotContain(item.Children.Skip(1), n => n.Checked);
    }

    [Fact]
    public void ViewObjectAndInputOverrideMenuWithTheUsualNotesAndLocks()
    {
        var state = ComponentUiState.Default.With("tolerance", "0.05 mm");
        var context = Context() with { ViewRows = new Dictionary<string, SettingsRow>
            { ["a"] = new([KeyValuePair.Create("tolerance", "0.01 mm")]) } };
        var objects = new SettingsPresentation(state, context);
        Assert.Equal(.01, objects.Resolution!.Views[0].Tolerance, 12);
        Assert.Equal("Tolerance (set on every view object)", Item(objects).Label);
        Assert.True(Item(objects).Enabled);
        Assert.Equal("0.05 mm", Assert.Single(Item(objects).Children.Where(n => n.Checked)).Label);

        var input = new SettingsPresentation(state, context with { InputBranches = [["tolerance=0.1 mm"]] });
        Assert.Equal(.1, input.Resolution!.Views[0].Tolerance, 12);
        Assert.Equal("Tolerance (set by input)", Item(input).Label);
        Assert.False(Item(input).Enabled);
        Assert.All(Item(input).Children, n => Assert.False(n.Enabled));
        Assert.Equal("0.1 mm", Assert.Single(Item(input).Children.Where(n => n.Checked)).Label);
    }

    [Theory]
    [InlineData("document")]
    [InlineData("auto")]
    public void DocumentOverridesLowerValuesAndStillLocksWhenSetByInput(string value)
    {
        var state = ComponentUiState.Default.With("tolerance", "0.05 mm");
        var presentation = new SettingsPresentation(state, Context(DocumentUnits.Millimetres, "tolerance=" + value));
        Assert.Equal(.001, presentation.Resolution!.Views[0].Tolerance);
        Assert.False(Item(presentation).Enabled);
        Assert.True(Item(presentation).Children[0].Checked);
        var menu = new SettingsPresentation(state.With("tolerance", value), Context());
        Assert.True(Item(menu).Children[0].Checked);
        Assert.Equal(new SettingsPresentation(ComponentUiState.Default, Context()).Resolution!.Views[0].VisibilityKey(),
            menu.Resolution!.Views[0].VisibilityKey());
    }

    [Theory]
    [InlineData("0.05 mm")]
    [InlineData("0.02 mm")]
    public void MenuValueSurvivesSavedStateAndCopyAsTextAndResetClearsIt(string value)
    {
        var context = Context(DocumentUnits.Metres);
        var state = ComponentUiState.Default.With("tolerance", value);
        var json = UiStateSerializer.Write(state);
        var restored = UiStateSerializer.Read(json, out var problem);
        Assert.Null(problem);
        Assert.Equal(json, UiStateSerializer.Write(restored));
        Assert.Equal(value, restored.Menu.Values["tolerance"]);
        var copied = Settings.CopyEditorAsText(restored.ToEditorSettings(), context.Views);
        Assert.Contains("tolerance = " + value, copied);
        var original = new SettingsPresentation(restored, context).Resolution!.Views[0];
        var roundtrip = Settings.Resolve(context.Views, [[copied]], context.Units).Views[0];
        Assert.Equal(original.VisibilityKey(), roundtrip.VisibilityKey());
        Assert.Equal(original.Tolerance, roundtrip.Tolerance);
        Assert.False(restored.ResetMenu().HasMenuValues);
        Assert.True(Item(new(restored.ResetMenu(), context)).Children[0].Checked);
        Assert.Equal(context.Views[0].DocumentTolerance, new SettingsPresentation(restored.ResetMenu(), context).Resolution!.Views[0].Tolerance);
    }

    [Fact]
    public void MenuToleranceStillProducesScaleCoarsenessWarning()
    {
        var presentation = new SettingsPresentation(ComponentUiState.Default.With("tolerance", "0.05 mm").With("scale", "1:1"),
            Context(DocumentUnits.Metres));
        var warning = Assert.Single(presentation.Resolution!.Diagnostics);
        Assert.Equal((DiagnosticSeverity.Warning, "tolerance"), (warning.Severity, warning.Key));
        Assert.Equal(TolerancePolicy.ScaleWarning(.00005, 1, DocumentUnits.Metres), warning.Message);
    }

    [Theory]
    [InlineData("0.02 mm", DocumentUnits.Metres, .00002)]
    [InlineData("0.00002 m", DocumentUnits.Millimetres, .02)]
    [InlineData("0.00002", DocumentUnits.Metres, .00002)]
    [InlineData(".02", DocumentUnits.Millimetres, .02)]
    public void CustomEntryAcceptsSettingsDistancesAndShowsACheckedValue(string text, DocumentUnits units, double expected)
    {
        var entry = Item(new(ComponentUiState.Default, Context(units))).Children.Last().TextEntry!;
        Assert.True(entry.TryAccept(text, out var command, out var error));
        Assert.Null(error);
        Assert.True(MenuCommands.TryParseSet(command!, out var key, out var value));
        var presentation = new SettingsPresentation(ComponentUiState.Default.With(key, value), Context(units));
        Assert.Equal(expected, presentation.Resolution!.Views[0].Tolerance, 12);
        Assert.Equal(text.Trim(), Assert.Single(Item(presentation).Children.Where(n => n.Checked)).Label);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("0")]
    [InlineData("-0.02 mm")]
    [InlineData("NaN")]
    [InlineData("1e309 m")]
    [InlineData("")]
    [InlineData("0.02 mm\nhidden=off")]
    public void CustomEntryRejectsBadValuesWithAOneLineSettingsError(string text)
    {
        var entry = new ToleranceTextEntry(".001", DocumentUnits.Millimetres);
        Assert.False(entry.TryAccept(text, out var command, out var error));
        Assert.Null(command);
        Assert.StartsWith("Invalid tolerance = ", error);
        Assert.DoesNotContain('\n', error!);
        Assert.Contains("Use document or a positive distance", error);
        // Exactly the same validation as a menu/view-object settings row.
        var result = Settings.Resolve(Context().Views, [], DocumentUnits.Millimetres,
            new EditorSettings(new SettingsRow([KeyValuePair.Create("tolerance", text)])));
        Assert.Equal(result.Diagnostics[0].Message.Replace('\r', ' ').Replace('\n', ' '), error);
    }

    [Theory]
    [InlineData("document")]
    [InlineData(" AUTO ")]
    public void CustomDocumentEntryRestoresDocumentTolerance(string text)
    {
        var entry = new ToleranceTextEntry("0.02 mm", DocumentUnits.Metres);
        Assert.True(entry.TryAccept(text, out var command, out var error));
        Assert.Null(error);
        Assert.True(MenuCommands.TryParseSet(command!, out var key, out var value));
        Assert.Equal("document", value);
        var state = ComponentUiState.Default.With("tolerance", "0.02 mm").With(key, value);
        Assert.False(state.HasMenuValues);
        Assert.Equal(Settings.RunText(ComponentUiState.Default.Menu), Settings.RunText(state.Menu));
        Assert.Equal(UiStateSerializer.Write(ComponentUiState.Default), UiStateSerializer.Write(state));
        var presentation = new SettingsPresentation(state, Context(DocumentUnits.Metres));
        Assert.Equal(.001, presentation.Resolution!.Views[0].Tolerance);
        Assert.True(Item(presentation).Children[0].Checked);
    }

    [Fact]
    public void CustomEntryPrefillsEffectiveToleranceAndDisablesForAnyOverride()
    {
        var state = ComponentUiState.Default.With("tolerance", "0.02 mm");
        var context = Context(DocumentUnits.Metres);
        var custom = Item(new(state, context)).Children.Last();
        Assert.True(custom.Enabled);
        Assert.Equal(.00002, Settings.ParseDistance(custom.TextEntry!.Text, context.Units), 12);
        Assert.Contains("bare numbers use document units", custom.Hint);

        var rows = new Dictionary<string, SettingsRow> { ["a"] = new([KeyValuePair.Create("tolerance", "0.05 mm")]) };
        var objects = Item(new(state, context with { ViewRows = rows }));
        Assert.True(objects.Enabled); // existing Choice behaviour for presets
        Assert.False(objects.Children.Last().Enabled);
        Assert.Equal(.00005, Settings.ParseDistance(objects.Children.Last().TextEntry!.Text, context.Units), 12);
        var input = Item(new(state, context with { InputBranches = [["tolerance=.1 mm"]] }));
        Assert.False(input.Children.Last().Enabled);
        Assert.Equal(.0001, Settings.ParseDistance(input.Children.Last().TextEntry!.Text, context.Units), 12);

        var partial = context with { Views = [.. context.Views, context.Views[0] with { Identity = "b", Name = "B" }], ViewRows = rows };
        Assert.False(Item(new(state, partial)).Children.Last().Enabled);
        Assert.False(Item(new(state, partial with { ViewRows = new Dictionary<string, SettingsRow>(),
            InputBranches = [["[#1]", "tolerance=.05 mm"]] })).Children.Last().Enabled);
    }

    [Theory]
    [InlineData("1:5", "0.01", true)]
    [InlineData("1:20", "0.1", false)]
    [InlineData("1:100", "1", false)]
    public void ScaleAdviceUsesThePolicyAndMarksCoarsePresetsWithoutDisablingThem(string scale, string maximum, bool tooCoarse)
    {
        var state = ComponentUiState.Default.With("scale", scale);
        var item = Item(new(state, Context()));
        Assert.Contains($"Scale {scale} → use ≤ {maximum} mm", item.Tooltip);
        Assert.Contains("1:1–1:5 → ≤ 0.01 mm", item.Tooltip);
        Assert.Contains("above 1:5 and below 1:50 → ≤ 0.1 mm", item.Tooltip);
        Assert.Contains("1:50 and coarser → ≤ 1 mm", item.Tooltip);
        var preset = item.Children.Single(n => n.Label == "0.05 mm");
        Assert.True(preset.Enabled);
        Assert.Equal(tooCoarse, preset.Tooltip!.Contains("too coarse for scale " + scale, StringComparison.Ordinal));
        Assert.Contains("Suits scales above 1:5", preset.Tooltip);
        Assert.Contains("Suits every scale", item.Children.Single(n => n.Label == "0.001 mm").Tooltip);
        Assert.Contains("Suits 1:50 and coarser", item.Children.Single(n => n.Label == "1 mm").Tooltip);
    }

    [Fact]
    public void ScaleAdviceUsesEffectiveInputAndViewObjectScales()
    {
        var state = ComponentUiState.Default.With("scale", "1:100");
        var context = Context() with { ViewRows = new Dictionary<string, SettingsRow>
            { ["a"] = new([KeyValuePair.Create("scale", "1:5")]) } };
        Assert.Contains("Scale 1:5 → use ≤ 0.01 mm", Item(new(state, context)).Tooltip);
        Assert.Contains("Scale 1:20 → use ≤ 0.1 mm", Item(new(state, context with { InputBranches = [["scale=1:20"]] })).Tooltip);
    }
}
