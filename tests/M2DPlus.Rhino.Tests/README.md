# Licensed Rhino integration tests

Plain `dotnet test` discovers skipped integration tests and never initializes Rhino.
The pre-commit hook runs Core and GH UI-model tests; Pack-Yak runs Core tests only.
Neither initializes licensed Rhino. Integration tests are serial
and share one in-process RhinoCore on one STA host thread; they never attach
to or terminate an interactive Rhino session. A compatible Windows x64 runtime,
installed licensed Rhino 8 and McNeel Rhino.Inside resolver are required.

`--filter FullyQualifiedName~DrawingComparisonTests` runs the shared Pipes/HLD
comparator controls (self-match, 10 mm class swaps in both directions, self-overlap,
deletion, coincident classes, Unresolved, points and zero-length projections) and baseline boundary checks.
The geometry controls require the licensed host; the baseline check is managed.
Set `DOTNET_ROLL_FORWARD=LatestMajor` to exercise the installed .NET 10 host.

```powershell
$env:M2D_RHINO_TESTS = '1'
$env:M2D_RHINO_SYSTEM = 'C:/Program Files/Rhino 8/System'
$env:M2D_RHINO_INSIDE = 'C:/path/to/Rhino.Inside_v8_net8.0.dll'
try {
    dotnet test tests/M2DPlus.Rhino.Tests -c Debug --filter Category=RhinoIntegration
} finally {
    Remove-Item Env:M2D_RHINO_TESTS, Env:M2D_RHINO_SYSTEM, Env:M2D_RHINO_INSIDE -ErrorAction SilentlyContinue
}
```

The existing resolver on the M0 research machine is at
`C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/.venv/Lib/site-packages/rhinoinside/Rhino.Inside_v8_net8.0.dll`.
No resolver binary or licence is redistributed. The smoke test constructs a solid
box, independently checks its volume, and reads the frozen `brep_box.3dm` fixture.
M1a adds lifecycle races during a real HLD progress callback, independent analytic
interval/point/mirror oracles, filtering/normalization, shared preparation and live
GH async publication. See [implementation notes](../../docs/design/m1a-notes.md).
Section tests use frozen root/S2 fixtures and generated analytic geometry, rectangle
areas, circle integrals, loop/hole/region counts, containment and independent volume
checks. They cover crop, merge, contact, cap/provenance and embedded-pattern hatch
save/reopen. See [the API and supported-domain notes](../../docs/design/m2-sections.md).

Run both native hosting runtimes **serially** with:

```powershell
./tools/Test-WalkingSkeleton.ps1 -Resolver 'C:/path/to/Rhino.Inside_v8_net8.0.dll'
```

The second run uses `DOTNET_ROLL_FORWARD=LatestMajor` and asserts runtime major 10
inside the GHA load/solve test. It does not change the owner's Rhino runtime
setting. TRX results are in `TestResults/m2-rhino-net8.trx` and `m2-rhino-net10.trx`.
The harness keeps a host document alive for GH's process-wide document witness,
and pumps scheduled GH solutions without showing an editor. Both runs load the
installed native Rhino build and the built GHA; no binaries are installed or
copied to Grasshopper Libraries. These checks do not certify a future worker or
replace an installed-Yak interactive check.

M1b adds all bundled uncut fixture property gates (P/F/U), primitive and Model
Object/View adapters through the actual GH component, named-view crop and
perspective partial failure, 80-level nonuniform/mirrored and 24-level linked
blocks, binary fingerprints, stable order, native extraction failure assertions,
estimate refusal, and 305-object/six-view and 5k-box smokes. Times are recorded,
never gated. Use `-BuildingFixture` (or `M2D_BUILDING_FIXTURE` for direct runs)
for an alternate research fixture path. See [M1b notes](../../docs/design/m1b-notes.md).

M2 wiring adds actual component Cut/Hatch tree/null alignment, adversarial retained
curve and point visibility, Box/source depth, near/far precedence, selected clipping
scope, flipped native parity, host style definitions/Parent chains, three-source
empty-slot extraction and bounded genuine Unset regressions. See
[M2 wiring notes](../../docs/design/m2-wiring.md) for the measured Unset classes,
extracted fixture provenance, conservative supported domain and render commands.

HLD-only acceptance (schema 4): exclude the 305-object and 5k-box smokes and the full
small-feature scoring sweep for short iteration runs. The small fixtures, section
regions/wiring, HLD section workers, glass/depth/scope, lifecycle, codec and GH/Bake
suites run on .NET 8 and .NET 10. Up to three owned headless Rhino processes may
coexist; never run curved-M or campus. No timing mutex is needed for correctness.

`TrimmedSlabTests` generates an exact saddle with a through-hole: its removed
surface tangency must not block the transverse material cut (analytic area 7.18).
Interior and trim-boundary tangencies must still be refused. `OwnerPipeSectionTests`
optionally reads `owner/pipes-node-gdas.3dm` under `M2D_MODELS` (default
`~/.t3/m2d-plus-models`) and skips if absent. That model currently retains three
certified bodies and two honest refusals for loop direction/face association.
Closed/valid Brep topology alone is insufficient.

`SlabClipSectionTests` also retains the folded upper part of a synthetic prism
above a sound reversed cut. On Rhino 8.36, Route C succeeds both without a far
plane and with one beyond the fold: the analytic section/cap and separate rear
curve/point occlusion checks pass. A far plane through the fold refuses the whole
source, demotes affected background curves to Unresolved and suppresses uncertain
points under the existing point failure policy. The separate inward
cavity shell preserves one region with one hole and the analytic material/void
classification, with visible curve/point probes inside the opened void. These
fixtures do not certify general retained defective solids.
Additional gates cover mixed remote/shared-topology proofs in both parameter
directions (including the recursive-exclusion flag), rational multi-span interior
dips/boundary contact, translated oblique near/far signed boundaries and mirrored
normals, a native oblique analytic cut, and nonuniform block fallback. The block
frame test includes a cut near the scaled local fold at scaled tolerance, plus a
cut inside the fixed physical-tolerance slab. A borderline remote fold now refuses
through the required whole-pair fallback when a native boundary section fails.
A 300 mm curve parallel to an oblique slab with a 0.002 mm gap compares proof-node
counts against the former world-box proof; boundary/roundoff-band, translation,
reflection, in-plane frame changes and a nonparallel far plane remain covered.

`SlabOverlapGuardTests` promotes the overlap characterization controls: duplicate
cylinders and a connected folded revolution with aligned/rotated seams and
reversed orientation refuse on native seam curves; a bounded coplanar overlap also
refuses. A quadratic duplicated dome has remote trims and no seam through the
slab: it admits with the guard bypassed through an internal pair-fixture seam and
refuses on a boundary overlap with the guard. Section and overlap-extraction
failure injection must fall back/refuse. Cached sections are reused, and a real
shared circular rim between axial cylinder bands remains admitted at near/far,
lower/upper outer boundaries. The guard has no overlap-length exemption.

A nearly coincident shallow dome and a small closed coincident component record
native outcomes without claiming completeness. An exact outer-plane tangency
uses injected isolated native section points to test the point rule independently
of Rhino 8.36's point omissions; its native section outcome is also recorded.
These are positive-evidence controls, not a proof for shallow near coincidence,
omitted native overlap components or closed components inside the slab. The
optional pipe area assertion uses `max(16tol², area·1e-6)` across Rhino builds.

Fresh HLD solves are compared by ordered inputs/policy/fingerprints and declared
fixture geometry properties. Lossless single-result codec round trips remain exact.
Do not assert independent solve byte equality. Preserve discrepancy manifests rather
than widening fixture tolerances. `archive/exact-engine` at `0cfc171` is the old engine.
