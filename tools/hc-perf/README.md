# hc-perf: PERF hill-climb bench (theory E, certified raster prepass)

One serial Rhino.Inside process. For each model and view it runs the configured engine configurations back to back on the
same prepared scene, records wall time (median of 3 runs under 10 s, one run otherwise) and peak private-memory growth,
and reduces each drawing to 2D loci in the view frame for comparison with plain native HLD.

```sh
python tools/hc-perf/make_spec.py artifacts/hc-perf/iterN.json \
  --configs '[{"name":"native","reuse":true},{"name":"best","raster":{...}},{"name":"cand","raster":{...}}]' --metamorphic cand
sh tools/hc-perf/run.sh "$(cygpath -w $PWD/artifacts/hc-perf/iterN.json)" "$(cygpath -w $PWD/artifacts/hc-perf/iterN)"
python tools/hc-perf/score.py artifacts/hc-perf/iterN/results.jsonl cand
```

- `native` without `reuse` solves with plain native HLD and writes the reference loci and times to `refDir`
  (default `artifacts/hc-perf/native-ref`). With `reuse` it loads them.
- `raster` is a `RasterOptions` object (see `src/M2DPlus.Rhino/Visibility/RasterPrepass.cs`).
- `run.sh` copies the bench to the output folder first, so rebuilding during a run is safe.
- Probe the fixed native cost: `dotnet <out>/bin/M2DPlus.HcPerf.dll <resolver> probe <model.3dm> <view>`.

Locus comparison (`Loci.cs`) is exact on polylines: per class, the parameter interval of each segment within distance tau
of the other drawing's segments is a capsule intersection. Hidden loci that coincide with visible loci of the same drawing
are removed first (native reports coincident pieces once). Disagreements are categorised: `unresolved-swap` (one side has
the piece as Unresolved: native's Unset production depends on scene composition and order), `class-swap`, `missing`, `extra`.
tau = max(10 * model tolerance, 1e-6 * scene diagonal); spans longer than 2 * tau are significant.
