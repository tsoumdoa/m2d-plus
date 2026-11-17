# HLD profiling harness

Build with `dotnet build tools/perf-f -c Release` and run:

```powershell
dotnet tools/perf-f/bin/Release/net8.0-windows/M2DPlus.PerfF.dll <resolver.dll> <model.3dm> <new-output-directory> <Top|Front|SW|plan|section>
```

This starts one owned headless Rhino.Inside process and records a schema-4 drawing
archive plus a manifest with fingerprints, processor count, policy, options and Info.
Model/evidence files stay outside git in `~/.t3/m2d-plus-models`. Use small fixtures,
curved-S, approved feature models or planar perf-S/M; never curved-M or campus.

Exact, composition, serial native, verifier, mesh-tier and dense-audit commands and
controllers are retired at `archive/exact-engine`, `0cfc171` (beta.8). Dated findings
in `hillclimb/perf-f` remain historical evidence. Fresh HLD hashes do not establish
correctness or repeat equality; use fixture-specific geometry/region properties.
