using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace M2DPlus.Grasshopper;

public sealed partial class DrawingComponent : GH_Component
{
    public DrawingComponent() : base("M2D+ Drawing", "M2D+",
        "Hidden-line plans, sections, elevations and axonometrics from your model and a view. Runs in the background. Reference a planar surface as View and right-click Convert to view boxes to get an editable view box; click Bake on the component to bake to tidy layers.",
        UI.Model.ComponentPlacement.Tab, UI.Model.ComponentPlacement.Panel)
    { Message = "Pick a view"; }

    // Stable component GUID, generated once for M0. Never change; append future inputs only.
    public override Guid ComponentGuid => new("c8d0f9fe-5498-45dd-8caf-10201b80d211");
    /// <summary>Params ▸ Util in its own group (<see cref="UI.Model.ComponentPlacement"/>).</summary>
    public override GH_Exposure Exposure => s_exposure;
    private static readonly GH_Exposure s_exposure = Enum.Parse<GH_Exposure>(UI.Model.ComponentPlacement.Exposure);
    protected override Bitmap Icon => _icon ??= M2DIcons.Drawing();
    private Bitmap? _icon;

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("Geometry", "G", "Scene to draw, shared by every view. Leave empty to draw the visible Rhino model. Also accepts layer names such as Walls or Structure::*, and block instances (repeats are drawn once and reused).", GH_ParamAccess.tree);
        p.AddParameter(new UI.ViewParam(), "View", "V", "What to draw. A referenced planar surface, rectangle or M2D+ view box (right-click Convert to view boxes to make one), a named view, clipping plane name or direction (Top, Front, SW…), a referenced clipping plane, a Plane, a Rectangle (its plane is the view plane and it crops; if it cuts the model you get a section), a Box (cut, crop and depth) or a Vector (look along it). A list gives one drawing per view.", GH_ParamAccess.list);
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
        // Names, nicknames and order come from ComponentOutputs (UX 1.2). Never reorder outputs: saved definitions
        // match them by index. Ignored (index 9) was appended for min visible, Key (index 10) for the output tree.
        var o = UI.Model.ComponentOutputs.All;
        void Curve(int i) => p.AddCurveParameter(o[i].Name, o[i].NickName, o[i].Description, GH_ParamAccess.tree);
        p.AddGeometryParameter(o[0].Name, o[0].NickName, o[0].Description, GH_ParamAccess.tree);
        Curve(1); Curve(2); Curve(3); Curve(4);
        p.AddParameter(new global::Grasshopper.Kernel.Parameters.Param_Hatch(), o[5].Name, o[5].NickName, o[5].Description, GH_ParamAccess.tree);
        Curve(6);
        p.AddRectangleParameter(o[7].Name, o[7].NickName, o[7].Description, GH_ParamAccess.tree);
        p.AddTextParameter(o[8].Name, o[8].NickName, o[8].Description, GH_ParamAccess.tree);
        Curve(9);
        p.AddTextParameter(o[10].Name, o[10].NickName, o[10].Description, GH_ParamAccess.tree);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        for (var i = 0; i < Params.Output.Count; i++)
            da.SetDataTree(i, new GH_Structure<IGH_Goo>());
        SolveDrawing(da);
    }
}
