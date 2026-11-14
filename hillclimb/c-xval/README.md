# Independent C cross-validation of theory D

Branch `hc/sec-c2-xval`, based on D commit
`4be80bbb938db757fa029e4a2505d06c841850ff`. No production engine file is changed.
C2 climbing was explicitly abandoned at the owner's change of task; its engine
changes were reverted. Only our verified child test-process tree was terminated.
The interrupted iteration 10 has no acceptance claim. C2 history is preserved in
C2-HISTORY.md and the workspace's ignored artifacts/c2-preserved directory.

## Reproduce the full score

```powershell
./hillclimb/c-xval/Score.ps1 -Iteration 0 -Summary 'Independent C score of D'
```

This forces compilation, then runs both runtimes serially through
`tools/Test-WalkingSkeleton.ps1`, with all existing licensed tests. Only test-host
optional timeout and runtime selection support is brought over from C. Their
ordinary defaults remain 90 seconds and the .NET 8/10 pair. The original 804 cases,
176 expected crossings, fixture geometry and area allowance are unchanged.
The C data are isolated here; D's hillclimb/expectations.json, scores.jsonl and
HILLCLIMB.md are untouched. All original C oracles remain unchanged except the 12
analytically justified CHS corrections recorded in c2-tube-audit.json.

## Full frozen-oracle verdict

Both CLR **8.0.34 and 10.0.15**, Rhino **8.36.26296.17001**:
**161/176 = 91.4773%** certified under C's frozen expectations, from **804 cases**.
D emits fills for 163 expected crossings. Two fail the frozen area allowance.
No fill of a non-crossing/open case and no loop/hole/locus failure is reported.
All 141 existing D licensed tests plus C's 12-tube audit pass; the added full C
score is the single failing test (142 pass / 1 fail on each runtime).

| Full case ID | D area (m²) | Frozen C expectation | Allowed absolute error | Observed absolute error |
|---|---:|---:|---:|---:|
| diagrid-shell/Roof section/diagrid-iso-1-0 | 0.013652403421501069 | 0.013689024200826586 | 3.0671002849883922e-05 | 3.6620779325516675e-05 |
| diagrid-shell/Roof section/diagrid-iso-1-3 | 0.013652402807115792 | 0.013689023585273612 | 3.0670996701719885e-05 | 3.6620778157820527e-05 |

These are area failures under the frozen C oracle, not established engine errors.
The named sources are closed four-face NURBS tube Breps. Each returned region has
one outer and one hole, and is not quarantined. Results are identical on both
runtimes, including the failure values.

## Independent refinement audit: false convergence in C

The exact source bodies are independently integrated using in-plane material
scanline chords. Parameter-only CurveBrep roots at 1e-8 query accuracy and strict
source membership select material intervals. This never uses BrepPlane, production
loops, caps, winding, or region mass properties. Repeating with the original point
query overload at 65,536 chords agrees within 2e-17 m². Every refinement value on
both runtimes is in repro/audit-net8.json and audit-net10.json.

| Source | C at 1,024 chords | C at 2,048 (frozen stop) | 4,096 | 65,536 | D versus 65,536 |
|---|---:|---:|---:|---:|---:|
| diagrid-iso-1-0 | 0.0136857312464 | 0.0136890242008 | 0.013633437839 | 0.013652043501 | 0.002636% |
| diagrid-iso-1-3 | 0.0136857306357 | 0.0136890235853 | 0.0136334372261 | 0.0136520428866 | 0.002636% |

C stopped when the 1,024/2,048 delta (~3.29e-6) appeared converged; the 4,096
value moves by ~5.56e-5, exposing aliasing of chord endpoints. At 8,192 through
65,536 the estimates cluster near D's result, with sub-1e-6 spread over the finest
three grids. D agrees with the 65,536 result to about **0.00264%**, far inside C's
unchanged 0.2% allowance. D's separate polygon oracle also estimates ~0.013651914.
This is strong evidence that the two raw failures belong to C's numerical oracle.
We **do not edit these two frozen expectations or claim a passing frozen gate**.
With refined expectations, the two emitted fills would be within allowance;
that is an audit interpretation, not an additional full-rescore result.

## Minimal reproductions

`repro/diagrid-iso-1-0.3dm` and `repro/diagrid-iso-1-3.3dm` each contain exactly
one original Brep, in meters, without geometry changes. Plane: origin (0,0,0),
X=(1,0,0), Y=(0,0,1), normal=(0,-1,0); tolerance 0.0001, angle tolerance 0.001,
no crop/far plane, prepareVisibility=true, default style. For each file, call:

```csharp
SectionRegionEngine.Compute(
    new[] { new SectionSource(name, brep, SectionStyleSnapshot.Default()) },
    new SectionRequest(new Plane(Point3d.Origin, Vector3d.XAxis, Vector3d.ZAxis), .0001, .001),
    prepareVisibility: true);
```

Executable reproduction (intentionally fails the frozen assertions and writes
the complete convergence audit; run the two commands separately):

```powershell
$env:M2D_XVAL_REPRO = '1'
./tools/Test-WalkingSkeleton.ps1 -Resolver '<existing Rhino.Inside resolver>' -RuntimeMajors @(8) -Filter FullyQualifiedName~FrozenDiagridRoofAreaReproduction
./tools/Test-WalkingSkeleton.ps1 -Resolver '<existing Rhino.Inside resolver>' -RuntimeMajors @(10) -Filter FullyQualifiedName~FrozenDiagridRoofAreaReproduction
Remove-Item Env:M2D_XVAL_REPRO
```

## Twelve CHS cases and remaining uncertainty

All 12 corrected space-frame cases fill and match exact finite-cylinder chord
arithmetic within 4e-15 m² on both runtimes. They are straight tubes of R=0.065,
r=0.05, L=1.2 with a parallel-to-axis plane at d=0.05668327095467873 (>r), so the
cut is one rectangle of area 2L sqrt(R²-d²) = 0.07634893013487501 m², with no hole.
The original ~0.7% discrepancy was **C oracle error**, not engine error. Old values
(~0.07689260048227647), transforms, radii, formulas and all 12 engine values remain
in c2-tube-audit.json. The allowance was never loosened.

Thirteen unique expected crossings are uncertain: **7 transversality**, **4 face
intersections**, **2 degenerate/self-intersecting loop sets**. The raw score lists
both visibility and fill diagnostics for some sources; these are not extra cases.
All case names and exact reasons are in cross-validation.json/uncertainCases.
The other two non-certified cases are the filled area outliers above.

Both runtime engine SHA-256:
`EE82119235EB3A6499BCD7B1A15F4AF9A3222792E26407850554CD678A2984FC`.
Fixture hashes, corrected oracle hash, timings, full gate records and all per-case
measurements are in iteration-0.net8/net10 JSON files and cross-validation.json.
No commit, push, installation, D-engine modification or owner-process access.

Final Debug/Release builds have zero warnings/errors. Forced local Git-sh hook passes.
The isolated fixture export passes; expected failing full-audit reproductions were
run serially on both runtimes. Production directories compare byte-for-byte to HEAD.
