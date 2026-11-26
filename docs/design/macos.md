# macOS support (Rhino 8 for Mac, Grasshopper 1)

Status: **built and unit-tested on Windows; not yet run on a Mac.** Everything below marked
*unverified* needs the [on-Mac checklist](../macos-test-checklist.md). Windows behaviour is unchanged:
the Windows package, worker route, 512 MiB in-process budget and menus are what they were.

## Packages

Two Yak distributions of the same version, built by `tools/Pack-Yak.ps1`:

| | `m2d-plus-<v>-rh8_20-win.yak` (unchanged) | `m2d-plus-<v>-rh8_20-mac.yak` (new) |
|---|---|---|
| GHA, Core, Rhino | `net8.0-windows` build | plain `net8.0` build (no `TargetPlatform(Windows)`, no WindowsDesktop framework reference) |
| Workers | `workers/net8`, `workers/net10` | none |
| README | `packaging/README.md` | `packaging/README-mac.md` |
| Build | `./tools/Pack-Yak.ps1` | `./tools/Pack-Yak.ps1 -Platform mac` (works on Windows) |

`M2DPlus.Rhino` and `M2DPlus.Grasshopper` multi-target `net8.0-windows;net8.0`. The worker, tests and
benchmarks keep using `net8.0-windows`. The `net8.0` GHA references `System.Windows.Forms` and
`System.Drawing.Common` 8.0 through the Grasshopper package, as McNeel's own cross-platform GH plug-ins do;
Rhino for Mac supplies its own implementations, and nothing desktop is bundled. `Test-YakPackage.ps1 -Platform mac`
checks the inventory (7 files), that no assembly is Windows-targeted (assembly metadata, not a byte search), the
manifest platform and the macOS README. `Publish-Yak.ps1` publishes the Windows and mac candidates of one
version together (pack both from the same commit; `-SkipMac` publishes Windows only). The owner approved shipping
the mac distribution as an untested beta before the checklist has run on a Mac (2026-11-22).

## Why there are no workers on macOS

The worker is a separate process that hosts Rhino with `Rhino.Runtime.InProcess.RhinoCore` (Rhino.Inside).
McNeel states Rhino.Inside does not run on Rhino for Mac and has no timeline for it
([Rhino.Inside on macOS](https://discourse.mcneel.com/t/rhino-inside-on-macos/173311),
[Rhino.Inside NuGet](https://discourse.mcneel.com/t/rhino-inside-nuget-package/165577)). The other
candidates were checked:

| Alternative | Finding | Verdict |
|---|---|---|
| Rhino.Inside / `RhinoCore` in a child process | Windows only (McNeel staff, above). | Blocked by McNeel. |
| Rhino.Compute / Hops server | Windows-only server. | Blocked. |
| Child process with only `RhinoCommon.dll` | RhinoCommon is a wrapper over the native Rhino core, which must be initialised by Rhino; that initialisation is exactly what Rhino.Inside provides (reasoning; not tried on a Mac). | Not viable as far as known. |
| A second Rhino for Mac instance as the worker (`open -n -a "Rhino 8" --args -nosplash -runscript …`) | `-runscript` and `-nosplash` are documented for Mac ([Starting Rhino](https://docs.mcneel.com/rhino/8mac/help/en-us/information/startingrhino.htm)); `rhinocode` can drive running instances. Unknown: licence use by a second instance, a full GUI app per worker (Dock icon, windows, focus), 10–20 s start-up, and whether it can run unattended. | Possible; needs a Mac to prove. Proposed below. |

So this branch ships the best route available without that proof: **in-process only**.

## The in-process-only route

`HostPlatform` (Rhino layer) reports `HostRouteCapabilities.For(os)`: Windows has isolated workers, a hard
memory cap and hard cancel; every other OS has none. On a Mac:

- `DrawingExecution.SolveBorrowedAsync` sends every request, including sets of views, to
  `SolveInProcessOnlyAsync`. The worker pool is never asked; `DrawingWorkerPool.Installed` is false off
  Windows whatever is on disk.
- **Admission** (`InProcessPolicy`, Core, unit-tested): memory is read at each run (`sysctl hw.memsize`;
  `host_statistics64(HOST_VM_INFO64)` free + inactive pages, as psutil and Activity Monitor estimate
  "available"). Budget = min(35% of RAM, max(256 MiB, 60% of available)). The scene estimate for all views
  must fit, with the Windows source/topology ceilings applied to the whole scene (`SceneCost.AdmittedSerial`):
  views draw one after another, so the 600k-face / 4M-triangle limits are not divided by the view count.
  Larger jobs are refused, as a component runtime error, naming the estimate, the budget and what to do. The
  check runs before queueing and again with live memory once the job holds the native slot
  (`NativeSettings.AdmitInSlot`), because a queued job may start long after its first check. `host_statistics64`
  is called with a 256-word buffer (retried at the size a newer kernel asks for) and only the stable
  revision-one prefix is read; physical RAM comes from `hw.memsize` on its own, so a VM-statistics failure
  still sizes the scheduler from real RAM.
- **Scheduler**: one global `NativeScheduler` sized to 35% of RAM (at least 512 MiB) instead of Windows'
  fixed 512 MiB, still one native solve at a time.
- **Cancellation** is the existing cooperative path: the component rejects stale work at once and never
  publishes it; a native HLD call already running may finish in the background before the next starts.
- **Info**: every view gets one line, `Route: in Rhino's process (macOS has no isolated drawing workers);
  … no hard memory cap. Admission: …`.
- **UI**: the Parallel views menu is hidden (it only sizes the worker pool). *Platform self-test…* is
  added at the end of the menu. Copy info/settings use Eto's clipboard on macOS (WinForms on Windows, as
  before). Everything else is the same code: WinForms timers and menus through Grasshopper's own Mac
  implementation, `GH_DocumentObject.Menu_AppendTextItem` for typed values, the Eto depth dialog,
  `System.Drawing` icons and canvas painting.

Guarantees lost on macOS, and the mitigation:

| Windows guarantee | macOS | Mitigation |
|---|---|---|
| Crash isolation (a native crash kills a worker) | A native crash takes Rhino down | Admission refuses jobs beyond the budget; HLD itself is the same Rhino code as on Windows |
| Hard memory cap (Job Object commit limit), one isolated retry | None | Per-run budget from live memory; refusal message |
| Hard cancel (kill the worker) | Cooperative; stale output never published | Status says cancelled at once; next run queues behind the finishing call |
| Parallel views (2–4 workers on 16+ cores) | Views one after another; each HLD solve is still multithreaded | — |
| Heavy single views beyond 512 MiB estimate (worker) | In-process up to 35% of RAM | Larger than Windows' in-process limit, below a worker's |

## Self-test

Right-click → **Platform self-test…** (macOS only; never shown on Windows). It runs on the UI thread for a
few seconds (its own fixture only), then shows, saves (`~/Documents/M2DPlus-selftest-<time>.txt`) and prints a report:

1. platform: OS, architecture, .NET, Rhino build, RhinoCommon path, and that the loaded M2D+ is the
   `net8.0` (not Windows) build with the in-process route;
2. memory probe: physical/available bytes, budget and scheduler ceiling, to compare with `sysctl hw.memsize`
   and `vm_stat`;
3. worker route: no worker installed or used;
4. admission: a small 4-view set is admitted and a 700k-face scene refused;
5. fixture drawing: `PlatformSelfTest.FixtureInputs()` (a box, a capped cylinder, a sphere, a mesh box and a
   line, in mm) drawn in a private headless document through the production dispatch for four views (plan,
   elevation, SW axonometric and a view-box section at z = 1200 mm), each with the route disclosure and HLD;
6. fixture vs Windows baseline: `DrawingSignature` per view (per-class length and extents in frame
   coordinates, point counts, region and hatch counts, cut area, error) against
   `tests/fixtures/platform/selftest-baseline.json`, embedded in `M2DPlus.Rhino`. Length and area within
   0.5% + 0.01, extents within 0.01 + 0.5% of frame size; piece counts are reported, not gated, because
   HLD may split one locus differently on another build;
7. cooperative cancellation: cancelled from Rhino's own HLD progress callback (inside `Compute`) when one
   arrives, else between views (reported as inconclusive), then a successful solve;
8. UI (GH layer): icons render, the component menu renders through `MenuRenderer` with text entries and
   without Parallel views, Eto, a WinForms timer and an Eto `UITimer` tick, and `RhinoApp.InvokeOnUiThread`
   as the 5 s fallback. The clipboard is not touched automatically (that would replace what the user copied);
   the report dialog's Copy button is the manual check.

The self-test refuses to start while another M2D+ drawing holds or waits for the native slot, so it never
blocks the UI behind someone else's solve.

The baseline is maintained on Windows: `PlatformSelfTestTests` (licensed) checks the self-test passes against
it, and the same fixture drawn through the in-process-only route (forced with the internal
`HostPlatform.OverrideForTests`) matches the Windows-route signatures. Set
`M2DPLUS_WRITE_SELFTEST_BASELINE=<path>` to regenerate it after an intended engine change.

## Closing the gap (proposal)

1. **Prove a second-instance worker on a Mac** (checklist §6): does `open -n -a "Rhino 8" --args -nosplash`
   start a second licensed instance while one is open, can `-runscript` run unattended, and how long does it
   take? If yes, port the worker as a Rhino-for-Mac *instance* rather than a Rhino.Inside process:
   - host: a hidden command (from a small `.rhp` or a `-runscript` RhinoCode C# script) that loads
     `M2DPlus.Rhino.dll` and calls `DrawingWorkerServer.Run` on a pipe; protocol, codec, fingerprints and
     stale-reply checks stay as they are;
   - transport: .NET named pipes are Unix domain sockets on macOS (`$TMPDIR/CoreFxPipe_<name>`); keep the
     name short, because `sun_path` is 104 bytes and `$TMPDIR` is ~49;
   - containment: replace the Job Object with the child's process ID plus a watchdog polling
     `proc_pid_rusage` (`ri_phys_footprint`) that sends `SIGKILL` above the cap. That is a hard kill and a
     soft-hard memory cap (sampled, not a commit limit); record the peak the same way for the one retry;
   - `WorkerMachineMemory` already has the Mac probe; `WorkerMemoryPolicy` sizing applies unchanged.
2. **If McNeel ships Rhino.Inside for Mac**, the existing worker ports nearly as is: build
   `M2DPlus.Worker` for `osx-arm64`/`osx-x64`, replace `SetDllDirectory`/PATH with resolving
   `RhinoCommon` from `Rhino 8.app/Contents/Frameworks/RhCore.framework/…/Resources` and `DYLD` search
   paths, and the same watchdog containment.
3. Either way, re-enable Parallel views on Mac only when workers exist (`HostRouteCapabilities`).
