using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace M2DPlus.Grasshopper;

public sealed partial class DrawingComponent : GH_Component
{
    public DrawingComponent() : base("M2D+ Drawing", "M2D+",
        "Hidden-line plans, sections, elevations and axonometrics from your model and a view. Runs in the background. Reference a planar surface as View and right-click Validate views to get an editable view box; click Bake on the component to bake to tidy layers.",
        "M2D+", "Drawing") { Message = "Pick a view"; }

    // Stable component GUID, generated once for M0. Never change; append future inputs only.
    public override Guid ComponentGuid => new("c8d0f9fe-5498-45dd-8caf-10201b80d211");
    public override GH_Exposure Exposure => GH_Exposure.primary;
    protected override Bitmap Icon => _icon ??= M2DIcons.Drawing();
    private Bitmap? _icon;

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("Geometry", "G", "Scene to draw, shared by every view. Leave empty to draw the visible Rhino model. Also accepts layer names such as Walls or Structure::*, and block instances (repeats are drawn once and reused).", GH_ParamAccess.tree);
        p.AddParameter(new UI.ViewParam(), "View", "V", "What to draw. A referenced planar surface, rectangle or M2D+ view box (right-click Validate views to make one), a named view, clipping plane name or direction (Top, Front, SW…), a referenced clipping plane, a Plane, a Rectangle (its plane is the view plane and it crops; if it cuts the model you get a section), a Box (cut, crop and depth) or a Vector (look along it). A list gives one drawing per view.", GH_ParamAccess.list);
        p.AddPlaneParameter("Target", "T", "Optional plane per view. The view's own plane is moved onto it 1:1 (View[i] → Target[i]). One plane for several views lays them out in a row. Leave empty to show each drawing in place.", GH_ParamAccess.list);
        p.AddTextParameter("Settings", "S", "Optional key = value lines, for example hidden = off or farclip = line. They override view-object user text (m2d.*) and the right-click menu. One branch applies to all views; one branch per view, or [View name] headers, set views separately.", GH_ParamAccess.tree);
        // Appended in UX rev. 4. Never reorder inputs: saved definitions match them by index.
        p.AddTextParameter("Depth", "D", "Optional view depth for Plane, Rectangle and clipping-plane views: a number in document units or text such as 8 m. One value for all views, or one per view. Beats depth in Settings. Boxes and view boxes take their depth from their geometry.", GH_ParamAccess.list);
        p[0].Optional = true;
        p[1].Optional = false;
        p[2].Optional = true;
        p[3].Optional = true;
        p[4].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddGeometryParameter("Visible", "V", "Visible linework: profiles, edges, projected curves and points, nearer than the beyond distance.", GH_ParamAccess.tree);
        p.AddCurveParameter("Hidden", "H", "Lines hidden by other geometry. Empty when Hidden lines is off.", GH_ParamAccess.tree);
        p.AddCurveParameter("Beyond", "B", "Visible lines farther than the beyond distance. They are moved out of Visible, not copied.", GH_ParamAccess.tree);
        p.AddCurveParameter("Far clip", "Fc", "Where the far clip plane cuts geometry, drawn only with farclip = line. Otherwise empty.", GH_ParamAccess.tree);
        p.AddCurveParameter("Cut", "C", "Section boundaries. One branch per cut region holding its outer loop and any holes (one region per object by default). Open outlines from open geometry get their own branches and no hatch.", GH_ParamAccess.tree);
        p.AddParameter(new global::Grasshopper.Kernel.Parameters.Param_Hatch(), "Hatch", "Ha", "One hatch per Cut region branch, at the same path, so each Cut branch has exactly one matching Hatch item. The pattern comes from the object's Rhino 8 section style when it has one; otherwise it is a solid hatch in the object's display colour. Where there is no fill (a style without fill, an open outline, hatch = none), the item is null, so the branches stay aligned.", GH_ParamAccess.tree);
        p.AddCurveParameter("Unresolved", "?", "Pieces whose visibility the engine could not decide. They never appear in Visible or Hidden. Usually empty.", GH_ParamAccess.tree);
        p.AddRectangleParameter("Frame", "F", "One placed drawing frame per view, at 1:1. It does not depend on the output's bounds. For a Rectangle view it is that rectangle, placed.", GH_ParamAccess.tree);
        p.AddTextParameter("Info", "i", "One report per view: source, effective settings and where each came from, counts, styles, unresolved and unsupported objects, timing.", GH_ParamAccess.tree);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        for (var i = 0; i < Params.Output.Count; i++)
            da.SetDataTree(i, new GH_Structure<IGH_Goo>());
        SolveDrawing(da);
    }
}
