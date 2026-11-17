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
        foreach (var input in UI.Model.ComponentInputs.All)
        {
            var access = Enum.Parse<GH_ParamAccess>(input.Access);
            var index = input.Type switch
            {
                "View" => p.AddParameter(new UI.ViewParam(), input.Name, input.NickName, input.Description, access),
                "Plane" => p.AddPlaneParameter(input.Name, input.NickName, input.Description, access),
                "Text" => p.AddTextParameter(input.Name, input.NickName, input.Description, access),
                _ => p.AddGenericParameter(input.Name, input.NickName, input.Description, access)
            };
            p[index].Optional = input.Optional;
        }
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
