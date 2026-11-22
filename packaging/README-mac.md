# M2D+ for Rhino 8 for Mac (preview)

Make2D-style plans, sections, elevations and axonometric drawings for Rhino 8 for Mac
(8.20 or later) and Grasshopper. **Preview: not yet verified on a Mac.** The same component,
settings, outputs and saved definitions as the Windows package.

Drop this `.yak` onto Rhino, accept installation, and restart Rhino. Open Grasshopper →
Params → Util → M2D+ Drawing (there is no M2D+ tab). Wire a view (for example `Top`).
Install only with Yak; don't also copy build files into Grasshopper Libraries. Keep the GHA,
Core and Rhino libraries together.

What differs from Windows: there are **no background drawing workers on macOS**, because
McNeel's Rhino.Inside (which the workers need) runs only on Windows. Every drawing, including
sets of views, is drawn inside Rhino's own process, one view after another, using Rhino's
multithreaded hidden-line engine. So on a Mac:

- there is no Parallel views menu;
- Cancel stops publishing at once, but a native hidden-line call already running may finish in
  the background before the next drawing starts;
- there is no hard memory cap. Before each run M2D+ checks the scene estimate against a budget
  of 35% of RAM and at most 60% of the memory available now (checked again when a queued drawing
  starts), and refuses larger jobs with a component error that says what to do (close other apps,
  draw fewer views at once, or narrow Geometry/view volumes);
- each view's Info says which route drew it and which guarantees it lacks.

Right-click the component → **Platform self-test…** to check this Mac: memory probe, route,
cancellation, menus, timers and icons, and built-in fixtures compared with the Windows baseline.
It saves a report in Documents.

Pipes (experimental), sections, hatches, Target placement, preview and Bake use the same code as on
Windows (unverified on a Mac until the checklist passes). HLD visibility is not certified. The libraries are architecture-neutral (Apple silicon and Intel). No Rhino 7 or .NET Framework builds.

Source, MIT licence, plans, issues and the macOS test checklist:
https://github.com/tsoumdoa/m2d-plus
