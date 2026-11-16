using Rhino.DocObjects;
using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

internal static class NativeViewport
{
    // All distances are in model units. No one-unit radius, 10-unit camera
    // offset or 100-unit far-plane padding: those were different cameras in m/mm.
    internal static void Configure(ViewportInfo viewport, Rectangle3d frame, Vector3d direction, BoundingBox bounds, double tolerance)
    {
        double radius = Math.Max(bounds.IsValid ? bounds.Diagonal.Length : 0,16*tolerance);
        viewport.IsParallelProjection = true;
        viewport.SetCameraLocation(frame.Plane.Origin+frame.Plane.ZAxis*(4*radius+16*tolerance));
        viewport.SetCameraDirection(direction);viewport.SetCameraUp(frame.Plane.YAxis);
        viewport.SetFrustum(frame.X.Min,frame.X.Max,frame.Y.Min,frame.Y.Max,Math.Max(tolerance,radius*1e-6),10*radius+32*tolerance);
    }
}
