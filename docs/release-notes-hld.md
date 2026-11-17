# Rhino HLD-only change (unreleased)

Rhino HiddenLineDrawing is now the only visibility engine and always runs multithreaded.
Physical Route C sections retain closed regions/holes, crop/merge/hatches, clipping scope,
clear glass, depth/far clipping and cut-only. Cut-only does not call HLD.

Engine and Joints are removed from the menu. Existing Settings, `m2d.*` and saved menu
values (including `fast`) load and are preserved but ignored with deduplicated grey
remarks. Defaults are quiet. Joints suppression is gone.

Hidden is now a projection setting. With hidden off, min-visible is disabled and all
objects are retained, with a grey remark. Fresh HLD solves may change duplicate ownership,
source attribution and Ignored results. Cached realizations remain stable until invalidated.
Native visibility is not certified; valid Unset points are suppressed with per-source
diagnostics. Detected unsafe curve extraction and section failures remain Unresolved.

Worker schema 4 requires matching inputs/settings/runtime/Rhino/assembly fingerprints.
One result's transport is lossless; independent threaded solves need not be byte-identical.
Older drawing blobs are rejected before restoring native geometry. The prior exact engine
is archived at `archive/exact-engine`, main `0cfc171` (beta.8).

Review fixes: orange remains local. Rejected sources/failed records and uncertain points
are skipped with diagnostics; detached Unset affects only its curve. Failed participating
clips affect only overlapping footprints; cut admission does not quarantine native projection.
Only HLD null/missing-frame failure quarantines the whole projection. Cut-only glass changes
invalidate region admission. Zero/thin-depth and no-HLD Info are explicit. Native worker
admission is capped at one below 16 cores, two on larger hosts, with native CPU contention
still possible; managed processor count is not a native thread limit.
