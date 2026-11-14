using M2DPlus.Core;
using M2DPlus.Grasshopper.UI.Model;
using Xunit;
using static M2DPlus.Grasshopper.Tests.PresentationTests;

namespace M2DPlus.Grasshopper.Tests;

public sealed class EditorSessionTests
{
    [Fact]
    public void InvalidAndLockedEditsAreRefused()
    {
        var session = new EditorSession(ComponentUiState.Default, Context(["hidden = off"]));
        Assert.Contains("Did you mean 'line'", session.Validate("farclip", "lien"));
        Assert.False(session.Set("farclip", "lien"));
        Assert.False(session.Set("depth", "-2 m"));
        Assert.False(session.Set("name", "A::B"));
        Assert.False(session.Set("hidden", "on")); // set by input
        Assert.False(session.IsDirty);
        Assert.True(session.Set("depth", "12 m"));
        Assert.True(session.IsDirty);
        Assert.Equal("12 m", session.Working.AllViews.Values["depth"]);
    }

    [Fact]
    public void ViewRowEditsResetToAllViews()
    {
        var session = new EditorSession(ComponentUiState.Default.With("*", "scale", "1:50"), Context());
        session.Select(A.Identity);
        Assert.Equal("Reset to All views", session.ResetLabel);
        Assert.True(session.Set("scale", "1:200"));
        Assert.Equal(FieldOrigin.Row, session.Field("scale").Origin);
        session.ResetSelected();
        Assert.Equal(("1:50", FieldOrigin.AllViews), (session.Field("scale").Text, session.Field("scale").Origin));
        session.Select("*");
        Assert.Equal("Reset to defaults", session.ResetLabel);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void ApplyRevertAndRebase()
    {
        var session = new EditorSession(ComponentUiState.Default, Context());
        session.Set("fade", "on");
        var applied = session.Apply();
        Assert.False(session.IsDirty);
        Assert.Equal("on", applied.AllViews.Values["fade"]);
        session.Set("hidden", "off");
        session.Revert();
        Assert.False(session.Working.AllViews.Values.ContainsKey("hidden"));

        // An external change (menu toggle, undo) is followed when there are no pending edits...
        session.Rebase(applied.With("*", "seams", "on"));
        Assert.Equal("on", session.Working.AllViews.Values["seams"]);
        // ...and pending edits survive one.
        session.Set("tangents", "on");
        session.Rebase(applied);
        Assert.Equal("on", session.Working.AllViews.Values["tangents"]);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void CopyAsTextReproducesTheEditorThroughTheSettingsInput()
    {
        var state = ComponentUiState.Default.With("*", "scale", "1:100").With("*", "hidden", "off")
            .With(A.Identity, "depth", "40 m").With(A.Identity, "farclip", "line").With(A.Identity, "fade.start", "10 %")
            .With(B.Identity, "name", "B \"west\" # 2");
        var session = new EditorSession(state, Context());
        var text = session.CopyAsText();
        Assert.Contains("[\"Section A\"]", text);
        var fromEditor = Settings.Resolve([A, B], [], DocumentUnits.Metres, state.ToEditorSettings());
        var fromText = Settings.Resolve([A, B], [[text]], DocumentUnits.Metres);
        Assert.Empty(fromText.Diagnostics);
        for (var i = 0; i < 2; i++)
            foreach (var key in Settings.Keys)
                Assert.Equal(fromEditor.Views[i][key].Value, fromText.Views[i][key].Value);
    }

    [Fact]
    public void ProblemsReportContradictoryFadeAndUnusedRowsCanBeRemoved()
    {
        var state = ComponentUiState.Default.With("*", "fade.start", "80 %").With("*", "fade.end", "20 %").With("text:old", "depth", "2 m");
        var session = new EditorSession(state, Context());
        Assert.Contains(session.Problems, p => p.Contains("fade.start must be <= fade.end"));
        Assert.Single(session.View.UnusedRows);
        session.Select("text:old");
        session.RemoveUnused();
        Assert.Equal("*", session.Selected);
        Assert.Empty(session.View.UnusedRows);
    }

    [Fact]
    public void ContextChangesKeepEditsAndLocks()
    {
        var session = new EditorSession(ComponentUiState.Default, Context());
        session.Select(B.Identity);
        session.Set("depth", "9 m");
        session.UpdateContext(new UiContext([A], [], DocumentUnits.Metres));
        Assert.Equal(B.Identity, session.Selected); // now an unused row, still selectable
        Assert.Equal(RowKind.Unused, session.SelectedRow.Kind);
        session.UpdateContext(new UiContext([A], [["depth = 1"]], DocumentUnits.Millimetres));
        session.Select(A.Identity);
        Assert.True(session.Field("depth").Locked);
    }
}
