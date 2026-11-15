# Routing phase measurements

Seconds, original instrumented baseline → final retained producer.

| Phase | M North | M SW | L North | Curved S SW |
| --- | ---: | ---: | ---: | ---: |
| Capture/snapshot | 0.317 → 0.373 | 0.271 → 0.419 | 1.074 → 1.196 | 0.128 → 0.050 |
| Normalisation | 0.110 → 0.153 | 0.108 → 0.176 | 0.485 → 0.643 | 0.009 → 0.007 |
| Planar extraction* | 0.791 → 0.502 | 1.187 → 0.337 | 3.766 → 1.554 | 0.173 → 0.000 |
| Route C trim | 0.000 → 0.000 | 0.000 → 0.000 | 0.000 → 0.000 | 0.000 → 0.000 |
| Kernel solve | 1.749 → 1.806 | 3.457 → 3.693 | 5.647 → 5.932 | 0.004 → 0.000 |
| Native setup | 0.987 → 0.915 | 1.403 → 1.321 | 3.462 → 3.390 | 0.149 → 0.024 |
| Native solve | 1.932 → 2.023 | 4.062 → 4.411 | 6.785 → 7.870 | 22.448 → 18.612 |
| Native extraction / duplicates | 0.249 → 0.483 | 0.576 → 0.994 | 0.877 → 1.878 | 0.119 → 0.000 |
| Curve-plane events | 0.123 → 0.102 | 1.957 → 0.718 | 0.368 → 0.401 | 0.000 → 0.000 |
| Interval union | 0.437 → 0.240 | 0.863 → 0.494 | 2.324 → 1.230 | 0.195 → 0.000 |
| Sections | 0.000 → 0.000 | 0.000 → 0.000 | 0.000 → 0.000 | 0.000 → 0.000 |
| Beyond/fade splitting† | 0.084† → 0.021 | 0.455† → 0.090 | 0.277† → 0.053 | 0.183† → 0.020 |
| Output construction | 0.183 → 0.252 | 0.417 → 0.789 | 0.959 → 1.084 | 0.098 → 0.202 |
| GH publication | — → 0.012 | — → 0.057 | — → 0.071 | — → 0.012 |
| End to end | 6.565 → 6.908 | 13.951 → 13.560 | 23.784 → 25.359 | 23.377 → 18.937 |

* Original extraction/union counters overlap. Raw observations are preserved; no corrected extraction estimate is invented.
† Original counters combined Beyond/fade and some output work. Final counters separate them. Original runs did not execute GH publication; final runs execute component Emit into real GH volatile trees.
Snapshot and preparation are common work charged to both routes. End-to-end excludes file loading, serialization, validation, GH scheduling, downstream solves and redraw. Each native-only measurement is fresh and immediately followed by routing on the same adapter and owned scene. These are single pairs, not statistical timing estimates. Trace cloning is charged to routing; cached native durations are excluded.

| Fresh native-only phase | M North | M SW | L North | Curved S SW |
| --- | ---: | ---: | ---: | ---: |
| Capture/snapshot | 0.373 | 0.419 | 1.196 | 0.050 |
| Normalisation | 0.153 | 0.176 | 0.643 | 0.007 |
| Planar extraction* | 0.000 | 0.000 | 0.000 | 0.000 |
| Route C trim | 0.000 | 0.000 | 0.000 | 0.000 |
| Kernel solve | 0.000 | 0.000 | 0.000 | 0.000 |
| Native setup | 0.339 | 0.375 | 1.400 | 0.018 |
| Native solve | 14.829 | 23.229 | 67.102 | 18.717 |
| Native extraction / duplicates | 0.000 | 0.000 | 0.000 | 0.000 |
| Curve-plane events | 0.000 | 0.000 | 0.000 | 0.000 |
| Interval union | 0.000 | 0.000 | 0.000 | 0.000 |
| Sections | 0.000 | 0.000 | 0.000 | 0.000 |
| Beyond/fade splitting† | 0.029 | 0.095 | 0.142 | 0.028 |
| Output construction | 0.755 | 1.125 | 7.163 | 0.191 |
| GH publication | 0.075 | 0.145 | 0.191 | 0.015 |
| End to end | 16.613 | 25.707 | 77.991 | 19.039 |

Native-only extraction/copy time is included in output construction. Its composition-duplicate bucket is zero. Small section restyle/output bookkeeping is separately recorded in raw phase maps.

Extraction/duplicate recovery versus the last validated producer: M North 1.913 → 0.483 s; L North 6.746 → 1.878 s. No section performance work was started.

Final producer: {"engineSha256": "1CA407C133B1F2EA26507AF34D006F64FE2D15E1CF1EEF9D87D85C0B78FFA4E9", "coreSha256": "183E8C6C6AA2A1AC7EF4F92CF2F9091F6A9F5AE01989F6F7B3DB98641A21D94A", "harnessSha256": "4B0378251795A12E94B2AB9F1A806F459CA503E5507FB5596E851A25C39837BA"}.

L North finished drawing and GH publication before the owner stopped the later L SW exact adjudication. Its complete timing remains valid; the cancelled L batch is not labelled a normally completed inventory.
