# Worker benchmark

Build the solution and this project in Debug or Release; use an existing compatible
Rhino.Inside resolver. All Rhino processes are owned by the harness. Models/evidence
stay outside git under `~/.t3/m2d-plus-models`; never overwrite existing evidence.

Current commands (see Program.cs for the complete replay/transport arguments):

- `engine <resolver> <model> <new-output-folder>` profiles threaded HLD, hidden on/off.
- `hld <resolver> <model> <new-output-folder> <variant>` retains raw native controls;
  native audit is removed and native compute remains threaded.
- `cutonly <resolver> <model> <new-output-folder> [building|long|offset:<mm>|pool:<mm>]`
  checks independently certified Cut/Hatch and zero HLD calls for cut-only. Fresh
  normal-result hashes are observations, not equality gates.
- `export`, `workers`, `pool`, `dump`, `restore`, `identity`, `load` and memory probes
  retain owned scene/request/result archives, strict handshake and resource checks.
  Native restoration of one result stays a strict lossless byte gate.

Independent threaded solves may differ. Use the licensed fixture gates for classed
loci at declared tolerances, topology/area, ordered inputs and policy/fingerprints.
`engine-fast-report.py <evidence-root>` summarizes the current hidden-on/off records.
Kernel, exact/fast comparison, planar-kernel generation and verifier/dense-audit
commands are retired at `archive/exact-engine`, `0cfc171` (beta.8). Historical findings
retain their original fingerprints and do not certify the HLD-only policy.
