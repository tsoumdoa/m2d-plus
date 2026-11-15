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
Matching .NET 8 and .NET 10 framework-dependent headless workers are included in
workers/net8 and workers/net10; keep these folders with the plugin. The matching
WindowsDesktop runtime and a licensed Rhino installation are required. Drawing sets
and heavy jobs use persistent workers (up to four). Caps and concurrency are chosen
from the scene estimate and available/physical memory; aggregate caps stay within
60% of physical RAM and 80% of available memory. A memory failure retries only that
view once, alone, at a larger cap when memory permits. Small single views stay in-process. Worker cancellation kills only the
owned child process; local cancellation rejects stale output immediately while a
native call may finish. Missing or incompatible workers leave heavy jobs refused.
Expert overrides: M2DPLUS_WORKERS (1-4 CPU ceiling), M2DPLUS_WORKER_MEMORY_MIB
(initial cap, still memory-budgeted), and M2DPLUS_WORKER_IDLE_SECONDS. No environment
variable is needed for ordinary use.
Check Info for unsupported sources, uncertainty, worker failures and admission.
No general geometry certification is claimed.
No macOS, Rhino 7 or .NET Framework support. Install only with Yak; don't also
copy build files into Grasshopper Libraries.

Source, MIT licence, plans and issues: https://github.com/tsoumdoa/m2d-plus
