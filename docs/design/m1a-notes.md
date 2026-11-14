# M1a implementation notes

M1a is the small, in-process native walking skeleton. It keeps the four inputs,
nine output indices and both M0 GUIDs. Geometry is optional; View remains required.
No commits, installation, publication or worker changes are part of this work.

## Ownership and generations

`SceneCapture` runs on its constructing host thread. It resolves document objects,
definition members and GH input values there. New geometry uses `Duplicate()` and
`EnsurePrivateCopy()` before dispatch. SubD, Extrusion and Surface conversion to
Brep is explicit and recorded. Points remain points. Definition-local geometry
is cached once; each instance leaf retains its identity and ordered transform
chain. Compute duplicates the local geometry, composes the chain from outermost
to innermost, and restores outward closed-solid orientation after reflection.
No document, RhinoObject, definition-table or GH wrapper is retained in compute.

Capture uses a 12 ms work-slice target. Enumeration and document witnesses are
host work; individual JSON fingerprints, conversions, copies and definition
enumeration cannot be preempted. Info reports initial enumeration and the maximum
slice, including verification. This is not a 50k-object responsiveness guarantee.
M1b must measure and improve capture overhead before broadening admission.

`DocumentWatch` increments a generation on object, attribute, layer, definition
and close notifications. Headless Rhino can omit notifications, so capture also
verifies object serial/membership, complete serialized attributes, definition
membership, visible layers, tolerance and units. These witnesses guard snapshot
coherence; cache identity separately hashes the **complete geometry serialization**
with SHA-256, plus document-session/source/definition-leaf identity. No key uses
DataCRC. Events invalidate whole views, including GH-reference scenes.

`LeaseEntry<T>` owns one cache reference. Retirement prohibits new leases and
defers disposal until the last live lease ends. `SceneCache` shares local owned
entries and lazily prepared world geometry across views and components. Local
geometry copy/bounds operations are serialized per entry; HLD and preparation
are serialized globally. The retained cache has an estimated 128 MiB budget,
512 local-entry and eight-scene limits. Eviction is a simple full retirement,
not an LRU. Leased retired entries may remain resident beyond that cache budget.

`NativeDrawing.SolveAsync` takes ownership of the scene lease and releases it only
after native work returns, including cancellation and failure. Native HLD/proxies
are job-local. Results contain private copied curves and point values. GH output
construction copies again; mutating emitted geometry cannot alter preparation,
the retained drawing or another component's output.

`RequestIdentity` carries scene/view/settings revisions and a monotonically
increasing component generation. `RequestGate` rejects stale progress and batch
publication atomically. The component debounces 250 ms, captures in host timer
slices, and receives background completions through a mailbox drained by that
timer. Workers never dispatch to the UI or inspect GH documents. A scheduled GH
solution publishes all settled views together. Invalid views retain their index
and Info, with empty geometry. Input edits, document changes/close, component
removal and GH document close cancel and release the component's retained result.

## Native route and quality

The adapter uses parallel projection, hidden curves on, tangents/seams off and
`multipleThreads=false` by default. `multiplethreads = true|false` (also on/off)
is the only effective Settings key. Other keys and per-view settings are reported
as unsupported in M1a. HLD `Points` supplies visible points; hidden points are
suppressed and counted. Unset segment proxies are checked before accessing their
parent or geometry. Parentless/source-less/null extractions are counted and
reported; Duplicate and Projecting segments have separate explicit counters.

Known uncertain pieces go to Unresolved. Any invalid extraction or unsupported
potential occluder quarantines the entire view, moving extracted curves to
Unresolved and suppressing uncertain points. Info distinguishes execution from
quality and states the narrow analytic validation domain. Single-threaded native
execution is reproducible policy, not proof of general geometric correctness.

Direction keywords and Vectors infer their frame outside the scene bounds. Up
is projected world Z, or world Y for straight up/down views. Plane, Rectangle,
Box, named/captured views and clipping-plane views produce a clear per-view
"not in this build yet" error. A Plane is never treated as an uncut view.
Drawings stay on the inferred world view plane. Trees are `{view; source path}`;
Frame and Info use `{view}`. Beyond, Far clip, Cut and Hatch remain empty with an
Info note. Target is reserved and has no placement effect in this milestone.

## Scheduling and limits for review

One process-wide `NativeScheduler.Global` admits one native operation at a time,
including preparation. A cancelled call holds its slot until it actually exits;
queued cancelled work never enters native code. The estimated native budget is
512 MiB, based on owned geometry bytes × 8 plus 64 KiB per leaf. M1a additionally
refuses more than 512 normalized sources or 16 views with "Worker unavailable".
These deliberately conservative limits are provisional admission guards, not
calibrated time or memory guarantees. There is no hard in-process cancel, native
crash isolation or native allocation cap. No heavy-job worker fallback exists.

## Selective MVP ports

Ported the thin NativeEngine pattern: explicit viewport/tolerance policy,
cancellation checkpoints, source tags, inverse HLD transform, owned curve
extraction and deterministic native threading. Added HLD Points and guarded
invalid-proxy diagnostics rather than retaining the MVP's silent skips.

Kept the ideas of async generation rejection and independent GH output copies.
Replaced per-component semaphores, finalizer-dependent drawing ownership, CRC
scene keys and mutable DTOs with the new global scheduler, leases and revisions.
Did not port clipping, depth/layout, mixed repair, the general Custom engine,
the old three-component UX, or the MVP's preparation cache implementation.

## Acceptance and reproduction

Core tests cover lease retirement/release races, cancellation at publication,
revision mismatches, partial batches, scheduler queuing, cancellation while a
native call is alive, failure recovery and budget rejection. Licensed tests use
one serial RhinoCore on an STA host thread. Lifecycle edits occur while a **real
HLD progress callback** is held open, not merely before task dispatch. Additional
gates cover output mutation, cancellation immediately before batch completion,
stale generations, changes between capture slices and unchanged-entry reuse.

The analytic fixture is generated in the test: box, crossing curve, four points
and two unequal definition solids through a translated nested instance and an
odd reflection. From Top and Front, the crossing curve must be visible on
`[-1,0] ∪ [2,3]` and hidden on `[0,2]`. Rectangle side intervals are checked for
complete coverage, extra loci and overlaps. Mirrored coordinates are composed by
hand (`x'=12-(x+2)`, `y'=y+4`), independent of production helpers. Points are
checked against the solid's ray intervals. No expectation is copied from HLD.

The GHA is loaded from its build path (not Grasshopper Libraries). Two real GH
components solve two views and assert **one** preparation via counters; their
output curves are independently mutable. Another test changes the view during
native work and checks that only the new view publishes. A headless harness must
pump `GH_Document.NewSolution(false)` for pending schedules: GH's own schedule
timer normally dispatches through an editor window, which is absent here.

Run `tools/Test-WalkingSkeleton.ps1 -Resolver <existing resolver DLL>` for serial
Debug/Core/.NET 8/.NET 10 acceptance. The .NET 10 run rolls the **test host** forward
and asserts the actual runtime major; it does not change Rhino's installed runtime
setting. An installed-Yak interactive check remains manual.

Final local acceptance (2026-11-14): Debug and Release build with zero warnings;
38 Core tests pass in both configurations. All 17 licensed tests pass under each
of .NET 8 and .NET 10 (about 10 s test time per run, including both GH checks).
Ordinary solution tests pass Core and skip all 17 licensed tests without starting
Rhino. The forced local pre-commit hook passes. `Pack-Yak.ps1 -AllowDirty` builds
the unchanged-version `0.1.0-alpha.1` trial under `artifacts/yak/trials/`; its
archive inventory, assembly versions and payload hashes pass validation. The
known intentional `M2D+` / `m2d-plus` Yak name warning remains. No install,
publication, version bump, commit or push was performed.

## M1b and later

M1b must add named uncut views, full declared normalization/reference/model-object
adapters, property-level P/F/U geometry matrix gates and the 305-object six-view
run. Measure capture verification/serialization, large individual operations,
cache residency and admission estimates; add deeper linked-definition and
nonuniform-transform fixtures. Broaden extraction regression fixtures (including
native Unset cases) before claiming more native coverage or enabling threading
by default. Retain conservative quarantine and whole-view invalidation.

Sections and Plane/Rectangle/Box/clipping views wait for S2/M2; Target placement,
full settings, preview controls and Bake remain later milestones. Durable cache,
dependency-aware invalidation, calibrated memory/time admission and a matching
runtime worker are still required before production-scale scenes are admitted.

## Review change (Claude)

Annotations (`AnnotationBase`: text, dimensions, leaders), text dots, hatches and point clouds can't hide anything. They are reported in Info as "Not drawn (non-occluding)" and **don't quarantine** the view. Without this, any label in a real model turned the whole drawing Unresolved. Unknown geometry types are still treated as potential occluders, and they still quarantine the view.
