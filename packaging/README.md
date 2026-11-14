# M2D+ — pre-alpha M1a

Small-scene Make2D-style drawings, Rhino 8.20+ on Windows.
Drop this `.yak` onto Rhino, accept installation, and restart Rhino. Open
Grasshopper → M2D+ → Drawing → M2D+ Drawing. Wire a view (for example `Top`).
Direction keywords (Top, Front, Right, Back, Left, Bottom, SW, SE, NE, NW) and
Vectors draw uncut parallel views asynchronously. Empty Geometry draws the visible
document. Outputs: Visible (curves and points), Hidden, Unresolved, Frame and Info.
Unsupported potential occluders quarantine the view; check Info before using a drawing.

This package contains the GHA, Core and Rhino libraries; keep them together.
The managed GHA targets .NET 8 and is intended for Rhino .NET 8 and .NET 10.
Target placement, sections, named views, other outputs, view objects and Bake are
not in this build. The only effective text setting is `multiplethreads = true|false`
(false by default). This build admits at most 512 normalized sources and 16 views,
subject to an estimated in-process memory budget. A drawing worker is unavailable;
large jobs are refused. Cancellation rejects stale output immediately but cannot
hard-stop an in-process native call. No general geometry certification is claimed.
No macOS, Rhino 7 or .NET Framework support. Install only with Yak; don't also
copy build files into Grasshopper Libraries.

Source, MIT licence, plans and issues: https://github.com/tsoumdoa/m2d-plus
