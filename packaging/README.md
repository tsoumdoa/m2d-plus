# M2D+ — pre-alpha M0

Grasshopper skeleton for Make2D-style drawings, Rhino 8.20+ on Windows.
Drop this `.yak` onto Rhino, accept installation, and restart Rhino. Open
Grasshopper → M2D+ → Drawing → M2D+ Drawing. Wire a view (for example `Top`).
All outputs are empty and a Remark says the engine is not implemented yet.

This package contains the GHA, Core and Rhino libraries; keep them together.
The managed GHA targets .NET 8 and is intended for Rhino .NET 8 and .NET 10.
There is no drawing engine, settings editor, Bake or headless worker in this package.
No macOS, Rhino 7 or .NET Framework support. Install only with Yak; don't also
copy build files into Grasshopper Libraries.

Source, MIT licence, plans and issues: https://github.com/tsoumdoa/m2d-plus
