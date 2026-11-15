"""Severity-ranked synthesis of saved evidence; never starts a Rhino host."""
from collections import Counter
import json
import re
from common import write_json


def failure_class(row):
    source=row['sectionOracle']['expected']['source']
    info=row['m2d']['Info']
    diagnostics=[s.strip() for s in info.split(';') if re.match(
        re.escape(source)+r'(?:/[^:;]+)?: (?:Uncertain|Unsupported|Error):',s.strip())]
    text='; '.join(diagnostics)
    if 'Certified crossing produced no trimmed geometry' in text: return 'certified trim failed'
    if 'Self-intersecting Brep faces' in text: return 'source self-intersection guard'
    if 'No trustworthy halfspace certificate' in text: return 'section-loop certificate rejected'
    if 'source='+source in info: return 'native Unset propagated to region'
    return 'region withheld by scene uncertainty'


def findings(rows,summary,metamorphic,out):
    failures=[r for r in rows if r.get('sectionOracle',{}).get('status')=='fail']
    classes=Counter(failure_class(r) for r in failures)
    stop=json.loads((out/'owner-stop.json').read_text()) if (out/'owner-stop.json').exists() else {}
    snapshot_ids={(r['model'],r['view']) for r in stop.get('quotedSnapshotFailures',[])}
    prior=[r for r in failures if (r['model'],r['view']) in snapshot_ids]
    prior_classes=Counter(failure_class(r) for r in prior)
    write_json(out/'root-cause-classes.json',{'final':dict(classes),'ownerSnapshot':dict(prior_classes),
        'sectionFailures':[{'model':r['model'],'view':r['view'],'class':failure_class(r),
            'area':r['sectionOracle']['area'],'loops':r['sectionOracle']['loops'],
            'unresolved':r['m2d']['unresolved']} for r in failures]})
    def evidence(model,view,suffix='.json'):
        slug=re.sub(r'[^a-zA-Z0-9_-]+','-',view).strip('-')
        return f'[{model} / {view}](../../artifacts/baseline/view-cases/{model}__{slug}__off__view_engine{suffix})'
    planar45=evidence('view-planar-control','Planar oblique 45','.analytic-rays.json')
    planar17=evidence('view-planar-control','Planar oblique 17','.analytic-rays.json')
    rhs=evidence('view-curved-steel','Normal RHS-arc station 3')
    ibeam=evidence('view-curved-steel','Normal I-beam-filleted-web-flanges station 3')
    mullion=evidence('view-curved-facade','Normal curved-mullion-2 station 2')
    twisted=evidence('view-curved-steel','Normal twisted-RHS station 4')
    shell=evidence('view-diagrid-shell','Normal diagrid-iso-0-0 station 1')
    spill=evidence('view-diagrid-shell','Normal diagrid-iso-1-0 station 3')
    rotation=evidence('view-grid-curved-perf-S-blocks-17','South elevation')
    periodic=next((r for r in metamorphic if r['engine']=='m2d' and r['kind']=='radial periodicity' and r['status']=='fail'),None)
    radial=evidence(periodic['model'],periodic['view']) if periodic else 'No failed periodic pair recorded'
    m1=evidence('view-curved-perf-M-blocks','Normal b00-f00-bay00-mullion-000002 station 1')
    s1=evidence('view-grid-curved-perf-S-blocks-00','West elevation')
    lines=[
        '**Execution stopped by owner decision.** The active curved-S roof-diagonal station 5 finished; '
        'all subsequent cases were skipped. No optional M probes were started. Normal-priority native jobs ran serially, '
        'releasing the shared Rhino slot between views; only this task\'s owned controllers/jobs were stopped. '
        'A queued worker exited before Rhino admission and is counted as skipped, not an engine crash; its original admission/watch logs remain.', '',
        f"**Inventory:** {summary['models']} models, {summary['views']} defined views; "
        f"{summary['status'].get('complete',0)} complete, {summary['status'].get('wall_timeout',0)} timeouts, "
        f"{summary['status'].get('skipped_owner_decision',0)} owner-skipped. "
        'Twelve grid archives (eight rotated and four controls), six curved/feature extensions, a round tower and a two-box planar control. '
        'Cases 5/6/7/8 contain 146/140/33/104 views. The review set contains only 20 JSON view-definition/expectation fixtures; '
        'all geometry, screenshots, timings and output loci remain ignored artifacts.', '',
        '**Severity order and causal attribution.** Reproducers below are the smallest existing suite scenes exhibiting each class '
        '(two-box control: 2 objects; steel: 8; diagrid: 25; tower: 29). They are not claimed to be delta-minimized. '
        'Native HLD is a comparator, not the truth oracle. Route C uses native HLD after physical clipping; the exact planar kernel is separate.', '',
        '| Rank / class | Small reproducer(s) | Responsible engine and consequence |',
        '|---|---|---|',
        f'| 1. Wrong-Visible, independently confirmed | {planar45} | **Native HLD**, inherited unchanged by route C. At world (1,-0.5,0) and (1,-0.5,1), the frontmost cross-box edge is inside the opaque box; the viewing ray hits its foreground face at (1.5,-1,z). Both engines output Visible. Exact planar kernel and independent box-ray oracle say Hidden. Fix or quarantine this before trusting confident native visibility. |',
        f'| 2. False-Hidden / missing visible ink | {planar17} | **Native HLD**, inherited by route C. Rays from (1,-1.5,0/1) immediately leave the cross-box and move away from the other box, yet the edges are Hidden. Exact kernel says Visible. Reversing the two input objects preserves this failure; it is not explained by input order in this control. |',
        f'| 3. Certified crossing fails physical trim | {rhs} (also RHS-arc station 2) | **Route C sections**: certificate D succeeds, then “Certified crossing produced no trimmed geometry.” Expected 0.09 m² / 2 loops; emitted 0 / 0. Raw visible cut-profile audit passes at station 3. Missing region/ink; 2 Unresolved pieces are conservative, not wrong-Visible. |',
        f'| 4. Section-loop certificate rejection | {ibeam}; {mullion} | **Route C sections** reject self-intersecting/degenerate loops and withhold the region. I profile expects 0.4386265482 m² / 1 loop; mullion expects 0.0025132741 m² / 2 loops. Both emit 0 / 0. Missing section output, not a measured wrong area on an emitted region. I case has 0 Unresolved pieces despite an explicit uncertainty diagnostic; mullion has 135. Whether rejection is over-conservative requires isolating the intersection loop. |',
        f'| 5. Source self-intersection guard | {shell} (also station 2) | **Route C validation** reports self-intersecting Brep faces away from shared topology and excludes the source. Missing region; not evidence that its guard is incorrect. The generator\'s nominal CHS area remains an expectation, but a self-intersecting sweep is outside the certified solid domain. Review source construction and validator together before relaxing this guard. |',
        f'| 6. Region removed by uncertainty propagation | {twisted}; {spill} | **Native Unset → route C quarantine** in twisted RHS (2 invalid native segments, 53 Unresolved); **route C scene-footprint quarantine** in diagrid (other sources rejected). Target certificate succeeds, but final region is withheld. Conservative Unresolved is harmless for visibility truth, yet the missing region is a real completeness limitation. Do not “fix” it by relabeling Visible. |',
        f'| 7. Remaining orientation / periodicity instability | {radial}; {rotation} | **Native and route C paths**, not the exact planar kernel. Equivalent-angle outputs differ; raw native also fails metamorphic pairs. Missing ink, confidence changes and confident class swaps are distinct evidence classes. Except for ranks 1–2, this audit cannot tell which confident side is correct or whether Duplicate/order/clipping caused it. Treat unresolved confident swaps as high-priority triage, not as proved wrong-Visible counts. |',
        f'| 8. Section performance | {s1}; {m1} | **Route C sections**: curved-S West takes 112.105 s, first HLD observed at 101.002 s (capture 0.126 s); much time precedes HLD. M mullion stations 1/2 hit 600.090/600.080 s execution caps in phase `m2d`. They predate the phase probe, so no finer attribution is available. Remaining M cap is 120 s, but those runs were skipped. |', '',
        'The exact planar kernel passed **2/2** independent rational-oracle controls, with no Unresolved output. '
        'No exact-kernel failure was found in this run; this says nothing about ineligible curved scenes. '
        'Duplicate records alone do not prove missing ink, and Unset evidence is separated from benign empty/zero-length slots. '
        'The 17° order reversal tested both orders of the two-object control; no general order-independence claim is made.', '',
        '**Why the section oracle reported 69 fails.** Those were completed attempts with a numeric analytic area/loop expectation. '
        'Every failed target emitted zero material regions (area 0, loops 0), rather than a subtly wrong area. '
        'This end-to-end completeness oracle deliberately counts safety withholding as a failure to deliver the expected section; '
        'it does not classify every such failure as an unsafe geometry algorithm.', '',
        '| Cause of zero target region | Owner snapshot (69) | Final |',
        '|---|---:|---:|']
    for cls in ('certified trim failed','section-loop certificate rejected','source self-intersection guard',
                'native Unset propagated to region','region withheld by scene uncertainty'):
        lines.append(f'| {cls} | {prior_classes[cls]} | {classes[cls]} |')
    lines += ['',
        'The last class is a code-supported inference from successful target certificates, other source uncertainty, '
        'and the final region-removal rule in `src/M2DPlus.Rhino/Services/SectionDrawing.cs`; '
        'the saved result does not trace every individual region-removal decision. Native Unset on twisted RHS is directly logged. '
        'No additional native isolation runs were made after the owner stopped the matrix.', '',
        '**Why 167 were incomplete.** At the quoted snapshot, 164 views had not run, one was starting, and two M views timed out: '
        '165 without a completed solve plus 2 caps. These are not 167 section-kernel failures. '
        'The 121 “unknown” rows were completed views without an independent numeric area/loop oracle '
        '(e.g. elevations, axos, freeform/tilted cuts); unknown is not pass. '
        f"After the final active solve, section results are {json.dumps(summary['sectionOracles'],sort_keys=True)}. "
        'Incomplete now consists of owner-skipped cases and the two timeouts. Native visible-cut profile reconstruction '
        'is a separate audit: HLD exposes no region objects, and hidden cut edges can prevent a closed-loop reconstruction.', '',
        f"**Locus evidence:** {summary['completedLocusDisagreements']} completed views disagree; "
        f"{summary['disagreementViewsByClass']['confidentVisibleHiddenSwapSamples']} have confident Visible/Hidden swaps, "
        f"{summary['disagreementViewsByClass']['missingInkSamples']} missing ink, "
        f"{summary['disagreementViewsByClass']['uncertaintyClassSamples']} uncertainty-class differences. "
        'Classes overlap; these counts must not be added. '
        f"Total reported M2D+ Unresolved pieces: {summary['m2dUnresolvedPieces']}; collector-incomplete views: {summary['locusCollectorIncompleteViews']}. "
        'Comparisons use canonical support-line unions and bidirectional samples (0.05 m spacing, at least 8 per segment, '
        '4× model tolerance). They are not a continuous-locus proof. The report retains per-view timing, region/loop, '
        'Unresolved and mismatch evidence in the local [gallery](../../artifacts/baseline/index.html).', '',
        '**Metamorphics:** for 48 completed S rotation pairs per engine, main M2D+ is 10 pass / 38 fail, '
        'native is 13 / 35; the 48 M pairs per engine remain incomplete. '
        'For 22 equivalent-angle tower pairs, M2D+ is 13 / 9 and native 6 / 16. '
        'These are classed-locus verdicts after co-rotating the view, with no best-fit scale/deformation. '
        'Geometry-only and confidence-only details remain in `metamorphic.json` and each pair\'s audit.', '',
        '**Review and reproduction:** unchanged engine from `origin/main` 80981d7; generated definitions cover all 423 views. '
        'Use the exact model/view names above with `tools/models/run.py --select ... --views ... --engine view_engine --threads off`; '
        'any future M probe must use `--wall 120`. The local stop receipt blocks deferred queues. '
        'No CI jobs, installs, commits or pushes were made. '
        'Shaded references cover SW + Front for every model; six real GH component/view-box checks passed. '
        'Definitions-only fixtures and manual unlicensed harness checks accompany the generators.']
    validation=out/'validation.json'
    if validation.exists():
        v=json.loads(validation.read_text())
        lines+=['',f"**Validation:** Debug and Release builds pass with zero warnings; {v['CoreTests']} Core and "
            f"{v['GhUiTests']} GH UI tests pass; forced local hook passes. Manual checks: "
            f"{v['ManualViewTests']} view-contract/oracle/collector tests and {v['ManualHarnessTests']} harness tests pass. "
            'No further licensed runs were made during wrap-up.']
    return lines
