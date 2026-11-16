using System.Windows.Forms;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using M2DPlus.Grasshopper.UI.Model;
using Rhino;
using Rhino.DocObjects;

namespace M2DPlus.Grasshopper.UI;

/// <summary>The View input (UX rev. 4 §2.1): a generic parameter whose right-click menu picks view objects in Rhino,
/// validates them into view boxes, flips or creates them, and chooses named views, clipping planes and directions.
/// Text choices are persistent text and picked objects persistent references, so both save with the .gh.
/// Choosing is greyed while a wire is connected; Convert to view boxes, Flip and Create also work on wired references.</summary>
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

    /// <summary>The DrawingUi of the component this param belongs to, if any (for reports).</summary>
    private DrawingUi? OwnerUi => (Attributes?.GetTopLevel?.DocObject ?? OnPingDocument()?.Objects.OfType<DrawingComponent>()
        .FirstOrDefault(c => c.Params.Input.Contains(this))) is DrawingComponent component ? component.Ui : null;

    public override void AppendAdditionalMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalMenuItems(menu);
        Menu_AppendSeparator(menu);
        var document = RhinoDoc.ActiveDoc;
        var state = ViewObjects.State(this, document);
        var stored = PersistentData.AllData(true).Count(g => g is not GH_String);
        MenuRenderer.Append(menu, ViewMenu.Build(NamedViews(document), ClippingPlanes(document), CurrentTexts, SourceCount > 0, state, stored),
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
        if (document is not null && command is ViewCommands.Validate or ViewCommands.Flip or ViewCommands.Create or ViewCommands.Select)
        {
            ViewObjects.Execute(command, this, document, OwnerUi, DepthDialog.Prompt);
            return;
        }
        if (SourceCount > 0) return;
        var current = CurrentTexts;
        if (command.StartsWith(ViewMenu.Toggle, StringComparison.Ordinal))
            SetTexts(ViewMenu.ToggleValue(current, command[ViewMenu.Toggle.Length..]), "M2D+ view");
        else if (command == ViewMenu.AllNamed) SetTexts(ViewMenu.AddAll(current, NamedViews(document)), "M2D+ all named views");
        else if (command == ViewMenu.AllClipping) SetTexts(ViewMenu.AddAll(current, ClippingPlanes(document)), "M2D+ all clipping planes");
        else if (command == ViewMenu.Clear) SetValues([], [], "M2D+ clear views");
        else if (command == ViewMenu.Pick && document is not null) PickViews(document, current);
    }

    /// <summary>Replaces the text values, keeping any non-text persistent values (references, planes…) in place.</summary>
    public void SetTexts(IReadOnlyList<string> texts, string undoName) =>
        SetValues(texts, PersistentData.AllData(true).Where(g => g is not GH_String).ToArray(), undoName);

    /// <summary>Replaces all persistent values: other values first (references, planes…), then texts. One GH undo record.</summary>
    public void SetValues(IReadOnlyList<string> texts, IReadOnlyList<IGH_Goo> others, string undoName)
    {
        OwnerUi?.ClearNotes();
        RecordPersistentDataEvent(undoName);
        PersistentData.Clear();
        foreach (var goo in others) PersistentData.Append(goo);
        foreach (var text in texts) PersistentData.Append(new GH_String(text));
        ExpireSolution(true);
    }

    /// <summary>Adds object references (view boxes, frame surfaces/curves) after the existing ones, skipping duplicates.</summary>
    public void AddReferences(IEnumerable<RhinoObject> objects, string undoName)
    {
        var others = PersistentData.AllData(true).Where(g => g is not GH_String).ToList();
        var known = others.Select(ViewObjects.IdOf).Where(id => id is not null).ToHashSet();
        foreach (var obj in objects)
            if (known.Add(obj.Id)) others.Add(ViewObjects.Reference(obj));
        SetValues(CurrentTexts, others, undoName);
    }

    public override void AddSource(IGH_Param source, int index) { OwnerUi?.ClearNotes(); base.AddSource(source, index); }
    public override void RemoveSource(IGH_Param source) { OwnerUi?.ClearNotes(); base.RemoveSource(source); }
    public override void RemoveAllSources() { OwnerUi?.ClearNotes(); base.RemoveAllSources(); }

    private void PickViews(RhinoDoc document, IReadOnlyList<string> current)
    {
        var editor = global::Grasshopper.Instances.DocumentEditor;
        editor?.FadeOut();
        try
        {
            const ObjectType filter = ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion | ObjectType.Curve | ObjectType.ClipPlane;
            var result = global::Rhino.Input.RhinoGet.GetMultipleObjects("Select planar surfaces, rectangles, view boxes or clipping planes to draw", false, filter, out var picked);
            if (result != global::Rhino.Commands.Result.Success || picked is null || picked.Length == 0) return;
            var objects = picked.Select(r => r.Object()).Where(o => o is not null).Select(o => o!).ToArray();
            foreach (var reference in picked) reference.Dispose();
            var all = document.Objects.FindByObjectType(ObjectType.ClipPlane).Select(o => o.Attributes.Name ?? "").ToArray();
            // A clipping plane is stored as text, as from the Clipping planes menu: a unique name reads well, else its id.
            var clipping = objects.OfType<ClippingPlaneObject>().Select(o =>
                !string.IsNullOrWhiteSpace(o.Attributes.Name) && all.Count(n => string.Equals(n, o.Attributes.Name, StringComparison.OrdinalIgnoreCase)) == 1
                    ? o.Attributes.Name : o.Id.ToString("D")).ToArray();
            var others = PersistentData.AllData(true).Where(g => g is not GH_String).ToList();
            var known = others.Select(ViewObjects.IdOf).Where(id => id is not null).ToHashSet();
            foreach (var obj in objects.Where(o => o is not ClippingPlaneObject))
                if (known.Add(obj.Id)) others.Add(ViewObjects.Reference(obj));
            SetValues(ViewMenu.AddAll(current, clipping), others, "M2D+ pick views");
        }
        finally { editor?.FadeIn(); }
    }
}
