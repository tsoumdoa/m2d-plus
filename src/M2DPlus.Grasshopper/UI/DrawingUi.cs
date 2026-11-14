using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using M2DPlus.Core;
using M2DPlus.Grasshopper.UI.Model;
using M2DPlus.Rhino.Views;
using Rhino;

namespace M2DPlus.Grasshopper.UI;

/// <summary>Bake request from the message-tab button or the menu. ViewIndex null = every view.</summary>
public sealed class BakeRequest(bool asCopy, int? viewIndex) : EventArgs
{
    public bool AsCopy { get; } = asCopy;
    public int? ViewIndex { get; } = viewIndex;
}

/// <summary>The UI half of the M2D+ Drawing component (UX rev. 4): persisted menu defaults, menus, view-object
/// commands and their reports, and the events the component wires to its job/bake backends. GH UI thread only.</summary>
public sealed class DrawingUi
{
    private readonly GH_Component _owner;
    private bool _runRequested;
    private readonly List<(GH_RuntimeMessageLevel Level, string Text)> _notes = new();

    public DrawingUi(GH_Component owner) => _owner = owner;

    public ComponentUiState State { get; private set; } = ComponentUiState.Default;
    /// <summary>Set when a stored state could not be fully read; show it as a remark.</summary>
    public string? ReadProblem { get; private set; }
    public bool AutoRun => State.AutoRun;

    /// <summary>Supplied by the component: the job state for the progress line and the tab button.</summary>
    public Func<DrawingRuntime>? Runtime { get; set; }
    /// <summary>Supplied by the component once it resolves views: real identities, names, sources and view-object rows.
    /// Until then a fallback derived from the View and Settings inputs is used.</summary>
    public Func<UiContext>? Context { get; set; }

    public event EventHandler<BakeRequest>? BakeRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler? RunRequested;
    public event EventHandler? ClearCacheRequested;
    /// <summary>Raised after the persisted state changes (menu, undo/redo through Read).</summary>
    public event EventHandler? StateChanged;

    public DrawingRuntime CurrentRuntime
    {
        get { try { return Runtime?.Invoke() ?? DrawingRuntime.Idle; } catch (Exception e) when (e is InvalidOperationException or NullReferenceException) { return DrawingRuntime.Idle; } }
    }
    public UiContext CurrentContext => Context?.Invoke() ?? FallbackContext(_owner);
    public ViewParam? ViewInput => _owner.Params.Input.Count > 1 ? _owner.Params.Input[1] as ViewParam : null;

    /// <summary>Refusals from the last view command (Validate, Flip, Create), shown as warnings until View changes.</summary>
    public IReadOnlyList<(GH_RuntimeMessageLevel Level, string Text)> Notes => _notes;

    /// <summary>The component's start gate: true with Run automatically on, or once after Run now.</summary>
    public bool ShouldStart()
    {
        var start = State.AutoRun || _runRequested;
        _runRequested = false;
        Paused = !start;
        return start;
    }

    /// <summary>True after the start gate refused a job (Run automatically off): "Paused · out of date".</summary>
    public bool Paused { get; private set; }

    /// <summary>Stores a new state with one GH undo record. Settings changes re-solve (which cancels the stale job).</summary>
    public void Commit(ComponentUiState next, string undoName, bool resolve = true)
    {
        if (UiStateSerializer.Write(next) == UiStateSerializer.Write(State)) return;
        _owner.RecordUndoEvent(undoName);
        State = next;
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (resolve) _owner.ExpireSolution(true);
        else { _owner.ExpirePreview(true); _owner.OnDisplayExpired(true); }
    }

    public void Execute(string command, bool shift)
    {
        if (MenuCommands.TryParseSet(command, out var key, out var value))
        { Commit(State.With(key, value), $"M2D+ {key} = {value}"); return; }
        if (command.StartsWith(MenuCommands.Preview, StringComparison.Ordinal))
        {
            var layer = command[MenuCommands.Preview.Length..];
            Commit(State.WithPreview(layer, !State.Preview.Contains(layer)), "M2D+ preview", resolve: false);
            return;
        }
        if (command.StartsWith(MenuCommands.BakeOne, StringComparison.Ordinal) && int.TryParse(command[MenuCommands.BakeOne.Length..], out var index))
        { Raise(BakeRequested, new BakeRequest(shift, index), "Bake isn't available in this build yet."); return; }
        switch (command)
        {
            case ViewCommands.Validate or ViewCommands.Flip or ViewCommands.Create or ViewCommands.Select:
                if (ViewInput is { } views && RhinoDoc.ActiveDoc is { } document) ViewObjects.Execute(command, views, document, this);
                break;
            case MenuCommands.Bake: Raise(BakeRequested, new BakeRequest(shift, null), "Bake isn't available in this build yet."); break;
            case MenuCommands.AutoRun: Commit(State.WithAutoRun(!State.AutoRun), "M2D+ run automatically", resolve: !State.AutoRun); break;
            case MenuCommands.Run: RunNow(); break;
            case MenuCommands.Cancel: Raise(CancelRequested, EventArgs.Empty, "Cancel isn't connected in this build."); break;
            case MenuCommands.Reset: Commit(State.ResetMenu(), "M2D+ reset menu defaults"); break;
            case MenuCommands.ClearCache: Raise(ClearCacheRequested, EventArgs.Empty, "There is no drawing cache to clear in this build yet."); break;
            case MenuCommands.CopyInfo: CopyText(InfoText(), "There is no Info to copy yet."); break;
            case MenuCommands.CopySettings:
                var context = CurrentContext;
                CopyText(Settings.CopyEditorAsText(State.ToEditorSettings(context.ViewRows), context.Views), null);
                break;
        }
    }

    /// <summary>Double-click on the component: select the referenced view objects in Rhino (nothing else happens).</summary>
    public bool SelectViewObjects()
    {
        if (ViewInput is not { } views || RhinoDoc.ActiveDoc is not { } document) return false;
        if (ViewObjects.References(views, document).Count == 0) return false;
        ViewObjects.Select(views, document);
        return true;
    }

    /// <summary>Shows a view command's outcome on the component: successes as one-off remarks, refusals as warnings
    /// that stay (re-added on every solve) until the next view command or a change to the View input.</summary>
    public void Report(string title, IReadOnlyList<ViewObjectResult> results)
    {
        _notes.Clear();
        foreach (var result in results)
        {
            var level = result.Ok ? GH_RuntimeMessageLevel.Remark : GH_RuntimeMessageLevel.Warning;
            if (!result.Ok) _notes.Add((level, $"{title}: {result}"));
            _owner.AddRuntimeMessage(level, $"{title}: {result}");
        }
        _owner.OnDisplayExpired(true);
    }

    /// <summary>Forget the last report (the View input changed).</summary>
    public void ClearNotes() => _notes.Clear();

    /// <summary>Message-tab button. Shift on Bake adds a copy instead of replacing (UX §8.2).</summary>
    public void Press(FooterButton button, bool shift)
    {
        switch (button)
        {
            case FooterButton.Bake: Execute(MenuCommands.Bake, shift); break;
            case FooterButton.Cancel: Execute(MenuCommands.Cancel, false); break;
            case FooterButton.Run: Execute(MenuCommands.Run, false); break;
        }
    }

    private void RunNow()
    {
        if (RunRequested is not null) { RunRequested(this, EventArgs.Empty); return; }
        _runRequested = true;
        _owner.ExpireSolution(true);
    }

    private void Raise<T>(EventHandler<T>? handler, T args, string unwired) where T : EventArgs
    {
        if (handler is null) Remark(unwired); else handler(this, args);
    }
    private void Raise(EventHandler? handler, EventArgs args, string unwired)
    {
        if (handler is null) Remark(unwired); else handler(this, args);
    }
    private void Remark(string text)
    {
        _owner.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, text);
        _owner.OnDisplayExpired(true);
    }

    private string? InfoText()
    {
        if (_owner.Params.Output.Count < 9) return null;
        var texts = _owner.Params.Output[8].VolatileData.AllData(true).OfType<GH_String>().Select(s => s.Value).ToArray();
        return texts.Length == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, texts);
    }

    private void CopyText(string? text, string? empty)
    {
        if (string.IsNullOrEmpty(text)) { if (empty is not null) Remark(empty); return; }
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { Remark("The clipboard is busy; try again."); }
    }

    public void AppendMenuItems(ToolStripDropDown menu)
    {
        GH_DocumentObject.Menu_AppendSeparator(menu);
        MenuRenderer.Append(menu, ComponentMenu.Build(MenuContext()), Execute);
    }

    /// <summary>The model behind the component menu, for rendering and for tests.</summary>
    public MenuContext MenuContext()
    {
        var presentation = new SettingsPresentation(State, CurrentContext);
        var hasInfo = _owner.Params.Output.Count > 8 && _owner.Params.Output[8].VolatileDataCount > 0;
        var views = ViewInput is { } param ? ViewObjects.State(param, RhinoDoc.ActiveDoc) : null;
        return new(presentation, CurrentRuntime, hasInfo, Views: views);
    }

    public bool Write(GH_IWriter writer)
    {
        writer.SetString(UiStateSerializer.ChunkName, UiStateSerializer.Write(State));
        return true;
    }

    public bool Read(GH_IReader reader)
    {
        var text = reader.ItemExists(UiStateSerializer.ChunkName) ? reader.GetString(UiStateSerializer.ChunkName) : null;
        State = UiStateSerializer.Read(text, out var problem);
        ReadProblem = problem;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Best-effort contexts from the View, Settings and Depth inputs, used until the component supplies real
    /// ones. Identities: "object:" + id for referenced Rhino objects (with their m2d.* rows), "text:" + lower-case
    /// text for text views, "index:" + list index otherwise.</summary>
    public static UiContext FallbackContext(GH_Component owner)
    {
        var document = RhinoDoc.ActiveDoc;
        var tolerance = document?.ModelAbsoluteTolerance is > 0 and var t ? t : .001;
        var views = new List<ViewSettingsContext>();
        var rows = new Dictionary<string, SettingsRow>(StringComparer.Ordinal);
        if (owner.Params.Input.Count > 1)
        {
            var index = 0;
            foreach (var goo in Items(owner.Params.Input[1]))
            {
                var obj = ViewObjects.IdOf(goo) is { } id ? document?.Objects.FindId(id) : null;
                var (kind, name, identity) = obj is not null
                    ? (ViewBoxes.IsViewBox(obj) ? ViewSourceKind.Box : obj is global::Rhino.DocObjects.ClippingPlaneObject ? ViewSourceKind.ClippingPlane : ViewSourceKind.Rectangle,
                        ViewBoxes.Label(obj), Identity(obj.Id))
                    : goo switch
                    {
                        GH_String s when ViewMenu.Directions.Contains(s.Value.Trim(), StringComparer.OrdinalIgnoreCase) => (ViewSourceKind.Direction, s.Value.Trim(), "text:" + s.Value.Trim().ToLowerInvariant()),
                        GH_String s => (ViewSourceKind.NamedView, s.Value.Trim(), "text:" + s.Value.Trim().ToLowerInvariant()),
                        GH_Rectangle => (ViewSourceKind.Rectangle, $"Rectangle {index}", $"index:{index}"),
                        GH_Box => (ViewSourceKind.Box, $"Box {index}", $"index:{index}"),
                        GH_Plane => (ViewSourceKind.Plane, $"Plane {index}", $"index:{index}"),
                        GH_Vector => (ViewSourceKind.Vector, $"Vector {index}", $"index:{index}"),
                        _ => (ViewSourceKind.ClippingPlane, $"View {index}", $"index:{index}")
                    };
                if (obj is not null) rows[identity] = ViewBoxes.ReadRow(obj.Attributes);
                views.Add(new(identity, name.Length == 0 ? $"View {index}" : name, kind, false, 0, tolerance));
                index++;
            }
        }
        var branches = new List<IReadOnlyList<string>>();
        if (owner.Params.Input.Count > 3)
        {
            // Wired or internalised text both count as input; before the first solve only persistent data exists.
            var settings = owner.Params.Input[3];
            IGH_Structure? data = settings.VolatileDataCount > 0 ? settings.VolatileData
                : settings is GH_PersistentParam<GH_String> { PersistentDataCount: > 0 } persistent ? persistent.PersistentData : null;
            if (data is not null)
                foreach (var path in data.Paths.OrderBy(p => p))
                    branches.Add(data.get_Branch(path).Cast<object>().Select(o => o is GH_String s ? s.Value : o?.ToString() ?? "").ToArray());
        }
        var depth = owner.Params.Input.Count > 4 ? Items(owner.Params.Input[4]).Select(g => g is GH_String s ? s.Value : g?.ToString()).ToArray() : [];
        return new(views, branches, Units(document)) { ViewRows = rows, Depth = depth };
    }

    /// <summary>The stable settings identity of a referenced view object.</summary>
    public static string Identity(Guid id) => "object:" + id.ToString("D");

    private static IEnumerable<IGH_Goo> Items(IGH_Param param) => param.VolatileDataCount > 0 ? param.VolatileData.AllData(true).OfType<IGH_Goo>()
        : param is GH_PersistentParam<IGH_Goo> generic ? generic.PersistentData.AllData(true)
        : param is GH_PersistentParam<GH_String> text ? text.PersistentData.AllData(true) : [];

    public static DocumentUnits Units(RhinoDoc? document) => document?.ModelUnitSystem switch
    {
        UnitSystem.Millimeters => DocumentUnits.Millimetres, UnitSystem.Centimeters => DocumentUnits.Centimetres,
        UnitSystem.Kilometers => DocumentUnits.Kilometres, UnitSystem.Inches => DocumentUnits.Inches,
        UnitSystem.Feet => DocumentUnits.Feet, _ => DocumentUnits.Metres
    };
}
