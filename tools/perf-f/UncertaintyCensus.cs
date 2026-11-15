using System.Text.Json;
using M2DPlus.Core;
using M2DPlus.Core.Sections;
using M2DPlus.Rhino.Sections;
using M2DPlus.Rhino.Services;
using Rhino.Geometry;

namespace M2DPlus.PerfF;

/// <summary>Observational cause ledger. Does not participate in production classification.</summary>
internal sealed class UncertaintyCensus : IDisposable
{
    private readonly List<(string Source, bool Duplicate, LineClass Class, Curve World)> _records = [];
    internal void Capture(string source, int edge, bool duplicate, LineClass kind, Curve world) =>
        _records.Add((source, duplicate, kind, world.DuplicateCurve()));

    internal object Measure(ViewDrawing output)
    {
        var totals = new Dictionary<string, double>();
        var pieces = new List<object>();
        var projection = Transform.PlanarProjection(output.Frame.Plane);
        var references = _records.Select(r =>
        {
            var c = r.World.DuplicateCurve(); c.Transform(projection);
            return (r.Source, Cause: r.Duplicate ? "native duplicate attribution" : r.Class == LineClass.Unresolved ? "native Unset interval" : "verifier contradiction", Curve: c);
        }).ToArray();
        try
        {
            foreach (var item in output.Items.Where(p => p.Class == LineClass.Unresolved))
            {
                using var geometry = item.CopyGeometry();
                if (geometry is not Curve curve) continue;
                var causes = new Dictionary<string, double>();
                // Quarantine modifies Class but preserves the pre-quarantine class.
                if (item.DepthGeometry is null)
                    causes["region withholding"] = curve.GetLength();
                else if (item.SolvedClass != LineClass.Unresolved)
                    causes["source/footprint quarantine"] = curve.GetLength();
                else
                {
                    Curve[] remaining = [curve.DuplicateCurve()];
                    foreach (string cause in new[] { "native Unset interval", "native duplicate attribution", "verifier contradiction" })
                    {
                        var refs = references.Where(r => r.Source == item.SourceIdentity && r.Cause == cause).Select(r => r.Curve).ToArray();
                        if (refs.Length == 0) continue;
                        var next = new List<Curve>();
                        foreach (var c in remaining)
                        {
                            using (c)
                            foreach (var part in EdgeProvenance.Split(c, refs.Select(r => (r, EdgeOrigin.Original)), output.Tolerance))
                            {
                                using (part)
                                if (part.Origin == EdgeOrigin.Original) causes[cause] = causes.GetValueOrDefault(cause) + part.Curve.GetLength();
                                else next.Add(part.Curve.DuplicateCurve());
                            }
                        }
                        remaining = next.ToArray();
                    }
                    foreach (var c in remaining)
                    {
                        using (c)
                        {
                            var cause = output.KernelEdges == 0 ? "verifier contradiction" : "native coverage/event gap";
                            causes[cause] = causes.GetValueOrDefault(cause) + c.GetLength();
                        }
                    }
                }
                foreach (var (cause, length) in causes) totals[cause] = totals.GetValueOrDefault(cause) + length;
                pieces.Add(new { item.GeometricKey, item.SourceIdentity, causes });
            }
        }
        finally { foreach (var r in references) r.Curve.Dispose(); }
        return new { totals, pieces, method = "final Unresolved length; source/depth-preserving projected overlap with raw native records; disjoint cause partition" };
    }
    public void Dispose() { foreach (var r in _records) r.World.Dispose(); }
}
