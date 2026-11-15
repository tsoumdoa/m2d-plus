# Future work (after v1)

Ideas the owner wants recorded but not scheduled. Each needs its own design pass first.

## Unrolled (developed) elevations of curved facades (noted 2026-11-15)

Architects need flat, true-length elevations of curved facades: a curved curtain wall drawn as if unrolled onto a plane, with panel widths measured along the curve. This is not a parallel projection. It needs a developable surface or a reference curve (the facade's plan line) to unroll along. Linework is mapped from (arc length along the curve, height), and hidden-line visibility is evaluated along the local facade normal. Open questions:
- How the user defines the unroll curve or surface (a view object like the view box?).
- How visibility and depth work when the viewing direction changes along the curve.
- How it composes with sections and placement.

## Other ideas already discussed

- Persistent, maintained drawings stored in the .3dm, with automatic revision clouds from exact-locus diffs (Fable's wacky-ideas report).
- Perspective views (out of scope for v1).
