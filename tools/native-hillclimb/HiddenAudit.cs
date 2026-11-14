using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino.FileIO;
using Rhino.Geometry;

namespace NativeHillclimb;
internal static class HiddenAudit
{
    internal static ViewDrawing Load(string path, params string[] classes)
    {
        var view=new ViewDrawing();
        using var file=File3dm.Read(path) ?? throw new IOException(path);
        foreach(var obj in file.Objects)
        {
            var kind=obj.Attributes.GetUserString("class");
            if(kind is null || !classes.Contains(kind)) continue;
            var geometry=obj.Geometry.Duplicate(); geometry.EnsurePrivateCopy();
            var source=new PreparedLeaf(obj.Attributes.Name,new[]{0},geometry);
            view.Pieces.Add(new(geometry,source,LineClass.Visible));
        }
        return view;
    }
    internal static void Run(string baselineDirectory,string output)
    {
        var records=new List<object>();
        foreach(var dir in Directory.GetDirectories(baselineDirectory).Order(StringComparer.Ordinal))
        {
            if(!File.Exists(Path.Combine(dir,"watch.json"))) continue;
            using var watch=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"watch.json")));
            if(watch.RootElement.GetProperty("status").GetString()!="exited") continue;
            for(var vi=0;vi<4;vi++)
            {
                var on=Path.Combine(dir,$"v{vi}-hTrue-r0-baseline.3dm"); var off=Path.Combine(dir,$"v{vi}-hFalse-r0-baseline.3dm");
                if(!File.Exists(on)||!File.Exists(off)) continue;
                using var row=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,$"v{vi}-hTrue.json")));
                var tol=row.RootElement.GetProperty("tolerance").GetDouble();
                using var a=Load(on,"Visible"); using var b=Load(off,"Visible");
                var verdict=Locus.Compare(a,b,tol);
                var detail=Path.GetFileName(dir)+$"-v{vi}";
                Program.Write(Path.Combine(output,detail+".json"),verdict);
                using var suspects=new File3dm();
                foreach(var difference in verdict.Differences.Where(d=>d.Index>=0 && (d.Kind.StartsWith("missing",StringComparison.Ordinal)||d.Kind.StartsWith("extra",StringComparison.Ordinal))))
                {
                    var from=difference.Kind.StartsWith("missing",StringComparison.Ordinal)?a:b;
                    using var geometry=from.Items[difference.Index].CopyGeometry();
                    using var attrs=new Rhino.DocObjects.ObjectAttributes {Name=difference.Source};
                    attrs.SetUserString("difference",difference.Kind);attrs.SetUserString("parameters",difference.Detail);
                    suspects.Objects.Add(geometry,attrs);
                }
                suspects.Write(Path.Combine(output,detail+"-suspects.3dm"),8);
                var physical=verdict.Differences.Where(d=>d.Kind!="attribution-only").ToArray();
                records.Add(new {model=Path.GetFileName(dir),view=vi,tolerance=tol,visibleLociEqual=physical.Length==0,verdict.AttributionChanges,
                    missing=physical.Where(d=>d.Kind.StartsWith("missing",StringComparison.Ordinal)).Sum(d=>d.UncoveredLength),
                    extra=physical.Where(d=>d.Kind.StartsWith("extra",StringComparison.Ordinal)).Sum(d=>d.UncoveredLength),
                    counts=physical.GroupBy(d=>d.Kind).ToDictionary(g=>g.Key,g=>g.Count()),on=a.Items.Count,off=b.Items.Count});
                Program.Write(Path.Combine(output,"visible-audit.json"),records);
                Console.WriteLine(JsonSerializer.Serialize(records[^1]));
            }
        }
    }
}
