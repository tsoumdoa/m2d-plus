# Small-feature truth

Generate all 12 small archives with `python tools/models/small_features.py`
(Python + rhino3dm 8, licence-free). The 19 details are authored in mm;
`manifest.json` contains their analytic profiles, each edge length, loop/hole/region
counts, section area, probe height, expected Visible/Hidden classes, archive hash,
units and transforms. No output from an engine is used as expected geometry.

Each document tolerance (0.01 and 0.001 mm, plus the 0.1 mm timing control) has mm
and metre copies, at the origin and translated by 1 km in **all three axes**.
The gap pair deliberately shares one cell. Every other detail has its own cell.
The near-contact top lies half a tolerance above the cut; exact contact is unchanged.
Mesh vertices are stored in double precision. Extrusion inner profiles are converted
into the outer profile's local frame before insertion. Geometry/UUIDs are deterministic;
archive metadata timestamps can change, so regenerating also refreshes SHA-256 values.

`SmallFeatureTests` evaluates independent line/arc samples in canonical mm,
analytic probe intervals, per-detail regions/areas and the independent slab
certificate. Curved profiles are explicitly excluded from the exact planar route.
The public region pipeline includes fill creation; a correct loop with a failed hatch
does not count as a successful filled region. A separate style-merge check requires
two gap regions with distinct memberships, not just an area sum of two.

Licensed measurement (only through the serial acceptance harness):

```powershell
$env:M2D_SMALL_RESULTS = "$PWD/artifacts/small-features/final"
./tools/Test-WalkingSkeleton.ps1 -Resolver 'C:/path/to/Rhino.Inside_v8_net8.0.dll'
```

The measurement test asserts input integrity and no wrong fill at the requested fine
tolerances. Its exported **Gate** is separate from test execution success. Set
`M2D_SMALL_STRICT=1` to fail acceptance on every unmet drawing/metamorphic gate.
The 0.1 mm control intentionally contains unresolved/sub-tolerance details; it is
not used to certify fine detail. Nothing here implies that a green test run passes
the small-feature gate. See `hillclimb/small-features/HILLCLIMB.md` for measured limits.
