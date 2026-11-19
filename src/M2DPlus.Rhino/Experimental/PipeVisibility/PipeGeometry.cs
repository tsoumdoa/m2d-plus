using Rhino.Geometry;

namespace M2DPlus.Rhino.Experimental.PipeVisibility;

// Value records shared by the compute adapter and the standalone research command.
internal static class PipeGeometry
{
    internal sealed record Input(string Name, GeometryBase Geometry);
    internal sealed record Stroke(string Source, string Class, Curve Curve);
    internal sealed record Dot(string Source, string Class, Point3d Position);
}
