# Benchmark manifest contract (M0 placeholder)

The harness arrives with the engine; M0 records the contract from
[PLAN — Test strategy](../../docs/PLAN.md#test-strategy-layered-oracles).
Record one manifest per independently reproducible run, alongside raw results:

| Field | Meaning |
|---|---|
| schemaVersion / runId | Manifest version and unique run identity |
| engineSha256 / fixtureSha256 / oracleSha256 | Hash the actually loaded engine, input bytes and independent oracle |
| rhinoBuild / runtime / architecture | Exact loaded Rhino build, .NET version and process architecture |
| route / threading | Native/convex/tiled and in-process/worker; native thread setting and scheduler budget |
| units / tolerance | Model units and absolute/angle tolerances with explicit units |
| cacheState / temperature | Which caches are populated; cold/warm process and scene |
| workerCount | Admitted workers and aggregate resource budget |
| wallSeconds / cpuSeconds / peakMemoryBytes | Complete-run elapsed/CPU time and process-tree peak, with sampling method |
| firstPreviewSeconds / completeResultSeconds | Time to first preview and complete generation-gated batch |
| verdict / evidence | Pass/fail/unknown per independently checked property, diagnostics and raw-result paths |

Use [tests/fixtures/manifest.json](../../tests/fixtures/manifest.json) for frozen and
external inputs. Verify hashes before running. Campus and 50k-box files stay external;
the existing 50k fixture is spaced, **not** a high-overlap correctness oracle. Record
interference, repetitions, cancellation/resource-release and failures explicitly.
An `ok` execution status never establishes drawing correctness.
