using System.Windows.Forms;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using M2DPlus.Grasshopper.UI.Model;
using Rhino;
using Rhino.DocObjects;

namespace M2DPlus.Grasshopper.UI;

/// <summary>The View input (UX §2.1): a generic parameter whose right-click menu chooses named views,
/// clipping planes and directions from the active document. Choices are stored as persistent text, so they
/// save with the .gh and resolve exactly like a Panel with the same text. Greyed while a wire is connected.</summary>
public sealed class ViewParam : Param_GenericObject
{
    // Generated once for the View input parameter. Never change.
    public override Guid ComponentGuid => new("a865210f-23f7-4626-8610-6f6e66cb8984");
    public override GH_Exposure Exposure => GH_Exposure.hidden;

    public ViewParam()
    {
        Name = "View"; NickName = "V"; Access = GH_ParamAccess.list;
    }

    /// <summary>The text values currently stored on the param (other persistent goo is kept untouched).</summary>
    public IReadOnlyList<string> CurrentTexts => PersistentData.AllData(true).OfType<GH_String>().Select(s => s.Value).ToArray();

    public override void AppendAdditionalMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalMenuItems(menu);
        Menu_AppendSeparator(menu);
        var document = RhinoDoc.ActiveDoc;
        MenuRenderer.Append(menu, ViewMenu.Build(NamedViews(document), ClippingPlanes(document), CurrentTexts, SourceCount > 0),
            (command, _) => Execute(command, document));
    }

    public static IReadOnlyList<string> NamedViews(RhinoDoc? document) => document is null ? []
        : Enumerable.Range(0, document.NamedViews.Count).Select(i => document.NamedViews[i].Name)
            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<string> ClippingPlanes(RhinoDoc? document) => document is null ? []
        : document.Objects.FindByObjectType(ObjectType.ClipPlane).Select(o => o.Attributes.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public void Execute(string command, RhinoDoc? document)
    {
        if (SourceCount > 0) return;
        var current = CurrentTexts;
        if (command.StartsWith(ViewMenu.Toggle, StringComparison.Ordinal))
            SetTexts(ViewMenu.ToggleValue(current, command[ViewMenu.Toggle.Length..]), "M2D+ view");
        else if (command == ViewMenu.AllNamed) SetTexts(ViewMenu.AddAll(current, NamedViews(document)), "M2D+ all named views");
        else if (command == ViewMenu.AllClipping) SetTexts(ViewMenu.AddAll(current, ClippingPlanes(document)), "M2D+ all clipping planes");
        else if (command == ViewMenu.Clear) SetTexts([], "M2D+ clear views");
        else if (command == ViewMenu.PickClipping && document is not null) PickClippingPlanes(document, current);
        else if (command == ViewMenu.Capture && document is not null) CaptureActiveView(document, current);
    }

    /// <summary>Replaces the text values, keeping any non-text persistent values (planes, rectangles…) in place.</summary>
    public void SetTexts(IReadOnlyList<string> texts, string undoName)
    {
        RecordPersistentDataEvent(undoName);
        var others = PersistentData.AllData(true).Where(g => g is not GH_String).ToArray();
        PersistentData.Clear();
        foreach (var goo in others) PersistentData.Append(goo);
        foreach (var text in texts) PersistentData.Append(new GH_String(text));
        ExpireSolution(true);
    }

    private void PickClippingPlanes(RhinoDoc document, IReadOnlyList<string> current)
    {
        var editor = global::Grasshopper.Instances.DocumentEditor;
        editor?.FadeOut();
        try
        {
            var result = global::Rhino.Input.RhinoGet.GetMultipleObjects("Select clipping planes to draw", false, ObjectType.ClipPlane, out var picked);
            if (result != global::Rhino.Commands.Result.Success || picked is null || picked.Length == 0) return;
            var all = document.Objects.FindByObjectType(ObjectType.ClipPlane).Select(o => o.Attributes.Name ?? "").ToArray();
            // A unique name reads well in a Panel and matches the menu; otherwise the object id is exact.
            var values = picked.Select(r => r.Object()).Where(o => o is not null).Select(o =>
                !string.IsNullOrWhiteSpace(o!.Attributes.Name) && all.Count(n => string.Equals(n, o.Attributes.Name, StringComparison.OrdinalIgnoreCase)) == 1
                    ? o.Attributes.Name : o.Id.ToString("D")).ToArray();
            foreach (var reference in picked) reference.Dispose();
            SetTexts(ViewMenu.AddAll(current, values), "M2D+ pick clipping planes");
        }
        finally { editor?.FadeIn(); }
    }

    private void CaptureActiveView(RhinoDoc document, IReadOnlyList<string> current)
    {
        var view = document.Views.ActiveView;
        if (view is null) { RhinoApp.WriteLine("M2D+: there is no active view to capture."); return; }
        if (view.ActiveViewport.IsPerspectiveProjection)
        {
            global::Rhino.UI.Dialogs.ShowMessage("M2D+ draws parallel views only. Switch the viewport to Parallel and capture again.", "M2D+ · Capture active view");
            return;
        }
        var name = ViewMenu.NextCaptureName(NamedViews(document));
        if (document.NamedViews.Add(name, view.ActiveViewportID) < 0)
        {
            RhinoApp.WriteLine($"M2D+: could not save the active view as \"{name}\".");
            return;
        }
        RhinoApp.WriteLine($"M2D+: saved the active view as named view \"{name}\". Edit it in Rhino's Named Views panel.");
        SetTexts([.. current, name], "M2D+ capture view");
    }
}
