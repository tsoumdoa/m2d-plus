using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using M2DPlus.Grasshopper.UI.Model;
using M2DPlus.Rhino.Views;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace M2DPlus.Grasshopper.UI;

/// <summary>The GH side of view objects (UX rev. 4 §2.2): finds the Rhino objects the View input references, runs
/// Convert to view boxes / Flip view direction / Create view from current viewport / Select view objects through
/// <see cref="ViewBoxes"/>, and keeps the GH references valid when a surface or curve becomes a view box.</summary>
public static class ViewObjects
{
    /// <summary>One Rhino object referenced by a View item. <see cref="Stored"/>: the item is the View param's own
    /// persistent data (M2D+ can update it). <see cref="Holds"/>: the item's type can hold a view box after conversion.</summary>
    public sealed record ViewReference(Guid Id, IGH_Goo Goo, bool Stored, bool Holds);

    /// <summary>The object id behind a View item, if it names a Rhino object.</summary>
    public static Guid? IdOf(IGH_Goo? goo)
    {
        switch (goo)
        {
            case IGH_GeometricGoo { IsReferencedGeometry: true } geometric when geometric.ReferenceID != Guid.Empty: return geometric.ReferenceID;
            case GH_Guid { Value: var id } when id != Guid.Empty: return id;
            case GH_String { Value: var text } when Guid.TryParse(text, out var parsed): return parsed;
        }
        var value = goo?.ScriptVariable();
        if (value is global::Grasshopper.Rhinoceros.Model.ModelObject { Id: { } modelId } && modelId != Guid.Empty) return modelId;
        return value is Guid raw && raw != Guid.Empty ? raw : null;
    }

    /// <summary>A persistent reference that survives the surface → view box conversion: Brep (it loads extrusions
    /// too) for surfaces, polysurfaces and boxes, Curve for not-yet-converted rectangle curves.</summary>
    public static IGH_Goo Reference(RhinoObject obj) => obj.Geometry is Curve ? new GH_Curve(obj.Id) : new GH_Brep(obj.Id);

    private static bool CanHoldViewBox(IGH_Goo goo) => goo is GH_Brep or GH_Guid or GH_String or GH_ObjectWrapper
        || goo.ScriptVariable() is global::Grasshopper.Rhinoceros.Model.ModelObject;

    /// <summary>The referenced objects, from the wire when V is wired, else from its own persistent data. Text items
    /// that name a clipping plane are included (for Select), other text is not an object.</summary>
    public static IReadOnlyList<ViewReference> References(ViewParam param, RhinoDoc? document)
    {
        var wired = param.SourceCount > 0;
        IEnumerable<IGH_Goo> items = wired ? param.VolatileData.AllData(true) : param.PersistentData.AllData(true);
        var result = new List<ViewReference>();
        foreach (var goo in items)
        {
            var id = IdOf(goo);
            if (id is null && goo is GH_String { Value: var name } && document is not null)
                id = document.Objects.FindByObjectType(ObjectType.ClipPlane)
                    .FirstOrDefault(o => string.Equals(o.Attributes.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;
            if (id is { } objectId && result.All(r => r.Id != objectId)) result.Add(new(objectId, goo, !wired, CanHoldViewBox(goo)));
        }
        return result;
    }

    public static ViewCommands.State State(ViewParam param, RhinoDoc? document)
    {
        var references = References(param, document);
        var boxes = document is null ? 0 : references.Count(r => ViewBoxes.IsViewBox(document.Objects.FindId(r.Id)));
        return new(references.Count, boxes, param.SourceCount > 0);
    }

    /// <summary>Runs one view command and reports every object's outcome to Rhino's command line and the component.</summary>
    public static IReadOnlyList<ViewObjectResult> Execute(string command, ViewParam param, RhinoDoc document, DrawingUi? ui)
    {
        IReadOnlyList<ViewObjectResult> results = command switch
        {
            ViewCommands.Validate => Validate(param, document),
            ViewCommands.Flip => ViewBoxes.Flip(document, References(param, document).Select(r => r.Id)),
            ViewCommands.Create => Create(param, document),
            ViewCommands.Select => Select(param, document),
            _ => []
        };
        var title = command switch
        {
            ViewCommands.Validate => "Convert to view boxes", ViewCommands.Flip => "Flip view direction",
            ViewCommands.Create => "Create view", _ => "Select view objects"
        };
        if (results.Count == 0 && command != ViewCommands.Select)
            results = [new(Guid.Empty, title, false, false, "the View input references no Rhino objects. Right-click V ▸ Pick views in Rhino… first.")];
        var summary = command == ViewCommands.Validate && results.Any(r => r.Id != Guid.Empty)
            ? ViewCommands.ConvertSummary(results.Count(r => r.Ok && r.Changed), results.Count(r => r.Ok && !r.Changed),
                results.Where(r => !r.Ok).Select(r => r.ToString()).ToArray())
            : null;
        if (summary is not null) RhinoApp.WriteLine("M2D+ " + summary);
        foreach (var result in results) RhinoApp.WriteLine($"M2D+ {title}: {result}");
        if (command != ViewCommands.Select) ui?.Report(title, results, summary);
        if (results.Any(r => r.Changed)) document.Views.Redraw();
        return results;
    }

    /// <summary>Convert to view boxes. References that come through a wire whose type can't hold a view box (a Surface or
    /// Curve parameter) are refused, so the wire never breaks; references stored on V are re-typed to Brep.</summary>
    public static IReadOnlyList<ViewObjectResult> Validate(ViewParam param, RhinoDoc document)
    {
        var references = References(param, document);
        var refused = references.Where(r => !r.Stored && !r.Holds && !ViewBoxes.IsViewBox(document.Objects.FindId(r.Id))
                && document.Objects.FindId(r.Id) is not ClippingPlaneObject).ToArray();
        var results = refused.Select(r => new ViewObjectResult(r.Id, Label(document, r.Id), false, false,
            $"comes through a {r.Goo.TypeName} wire, which can't hold a view box. Reference it on V instead (right-click V ▸ Pick views in Rhino…), or through a Brep, Geometry or Model Object parameter.")).ToList();
        results.AddRange(ViewBoxes.Validate(document, references.Except(refused).Select(r => r.Id)));
        var converted = results.Where(r => r.Ok && r.Changed).Select(r => r.Id).ToHashSet();
        if (param.SourceCount == 0 && references.Any(r => converted.Contains(r.Id) && r.Goo is not GH_Brep))
        {
            var texts = param.CurrentTexts;
            var others = param.PersistentData.AllData(true).Where(g => g is not GH_String)
                .Select(g => IdOf(g) is { } id && converted.Contains(id) && g is not GH_Brep ? new GH_Brep(id) : g).ToArray();
            param.SetValues(texts, others, "M2D+ convert to view boxes");
        }
        else if (converted.Count > 0) param.ExpireSolution(true);
        return results.OrderBy(r => references.ToList().FindIndex(x => x.Id == r.Id)).ToArray();
    }

    /// <summary>Create view from current viewport: needs Rhino's active viewport, so it is a no-op headless.</summary>
    private static IReadOnlyList<ViewObjectResult> Create(ViewParam param, RhinoDoc document)
    {
        var view = document.Views.ActiveView;
        if (view is null) return [new(Guid.Empty, "Create view", false, false, "there is no active Rhino viewport.")];
        using var viewport = new ViewportInfo(view.ActiveViewport);
        return [CreateFrom(param, document, viewport)];
    }

    /// <summary>Bakes a view box for the viewport and adds its reference to V (when V is not wired).</summary>
    public static ViewObjectResult CreateFrom(ViewParam param, RhinoDoc document, ViewportInfo viewport)
    {
        var excluded = References(param, document).Select(r => r.Id).ToArray();
        var result = ViewBoxes.Create(document, viewport, ViewBoxes.SceneBounds(document, excluded));
        if (!result.Ok) return result;
        if (param.SourceCount > 0) return result with { Message = result.Message + " V is wired, so wire it in yourself." };
        param.AddReferences([document.Objects.FindId(result.Id)!], "M2D+ create view");
        return result with { Message = result.Message + " Added to View." };
    }

    /// <summary>Select view objects (also the component's double-click): selects them in Rhino so their user text is in
    /// Properties. Returns nothing to report.</summary>
    public static IReadOnlyList<ViewObjectResult> Select(ViewParam param, RhinoDoc document)
    {
        var ids = References(param, document).Select(r => r.Id).Where(id => document.Objects.FindId(id) is not null).ToArray();
        if (ids.Length == 0) return [];
        document.Objects.UnselectAll();
        foreach (var id in ids) document.Objects.Select(id, true, true);
        document.Views.Redraw();
        return [];
    }

    private static string Label(RhinoDoc document, Guid id) => document.Objects.FindId(id) is { } obj ? ViewBoxes.Label(obj) : id.ToString("D");
}
