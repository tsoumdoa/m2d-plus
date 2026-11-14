# Section certificate hill climb

Run serially from the repository; each command starts only its own headless
Rhino.Inside process. No installation or interactive process access occurs.

```powershell
./tools/hillclimb/Score.ps1 -Iteration 8 -Summary 'describe one hypothesis'
./tools/Test-WalkingSkeleton.ps1 -Resolver 'C:/path/to/Rhino.Inside_v8_net8.0.dll'
```

`hillclimb/expectations.json` is the audited independent reference for all 1,517
object/view cases. The scorer checks the exact Rhino build, fixture hashes and
case inventory. Reference areas/topology come from analytic profiles, exact
surface graph integration through every profile knot, or triangle-plane polygon
integration with the recorded error budget. Expected data never uses product
region results. See `HILLCLIMB.md` for the oracle audit and limits.

Every trial evaluates the complete case set. Timing excludes reference work:
one warm-up and three consecutive uncached region computations for crossing
objects; other cases still run the geometric gate once. The median crossing
cost includes conversion/validation, certification, region and hatch realization.
It is not HLD cost. Shared-machine load is recorded, not controlled.
The aggregate median uses the upper middle value for an even-sized inventory;
`compare_timing.py` uses the identical convention for paired resampling.

The scorer appends raw geometric scores to `hillclimb/scores.jsonl` and writes
complete per-case records under `artifacts/hillclimb/iteration-N/`. Licensed
acceptance is separate; only candidates that pass it may be retained. A failing
licensed check can be recorded with the direct runner's
`--gate-failure=description` argument. Rejected non-improving trials do not need
another licensed run. Raw initial oracle-audit rows remain in the log; use the
complete 254-crossing baseline, not invalidated preliminary rows.

To reproduce the baseline, build recorded commit
`69e4a8a7d13a02d7d91e683dedbd119159e255be` in a separate checkout and pass its
Rhino library with `-EngineAssembly C:/path/to/M2DPlus.Rhino.dll`. This task also
preserves that library, with its SHA in the reference manifest, under ignored
`artifacts/hillclimb/baseline-assembly/`. The copy is only to this tool's output
directory, never Grasshopper Libraries. `--regenerate-oracle` on the direct
runner is an audit mode; it does not overwrite the tracked expectations.

The benchmark tests each sidecar's section plane, plus explicit plan/side
controls where absent. The scorer measures uncropped section certification;
crop/merge, actual component paths, native visibility and hatch serialization
remain part of the licensed gates and separate `tools/render/` drawings.
