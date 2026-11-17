"""Aggregate case 5–8 evidence, sampled metamorphics, fixture inventory and local gallery."""
from __future__ import annotations
import argparse
from collections import Counter
import html
import json
from pathlib import Path
import re
import subprocess
import time
from common import ROOT,digest,write_json


def slug(name): return re.sub(r'[^a-zA-Z0-9_-]+','-',name).strip('-')


def prefix(out,model,view): return out/f'{model}__{slug(view)}__off__view_engine'


def collector_complete(stem,engine):
    """The atomic payload checkpoint is written after its loci file has closed."""
    payload=Path(str(stem)+f'.{engine}.json')
    if not payload.exists(): return False
    value=json.loads(payload.read_text())
    accepted=('complete',) if engine=='m2d' else ('ok','extraction_partial')
    return value.get('status') in accepted and not value.get('extractionFailures' if engine=='m2d' else 'extractionErrors')


def details(out):
    from collections import defaultdict
    from native import svg
    for source in ('CHS-arc','twisted-RHS','I-beam-filleted-web-flanges'):
        stem=prefix(out,'view-curved-steel',f'Normal {source} station 3')
        path=Path(str(stem)+'.m2d-loci.json')
        if not path.exists(): continue
        rows=[r for r in json.loads(path.read_text()) if source in r['source'] and r['class']=='Cut']
        if not rows: continue
        points=[p for r in rows for p in r['points']]; x0=min(p[0] for p in points); x1=max(p[0] for p in points)
        y0=min(p[1] for p in points); y1=max(p[1] for p in points); margin=max(x1-x0,y1-y0)*.15
        paths=defaultdict(list)
        for r in rows: paths['Cut'].append('M'+'L'.join(f'{x:.12g},{-y:.12g}' for x,y in r['points']))
        target=Path(str(stem)+'.profile.svg')
        target.write_text(svg(paths,[],[x0-margin,x1+margin,y0-margin,y1+margin],source+' actual M2D+ normal section; station 3'),encoding='utf-8')
        if (ROOT/'artifacts/render-tools/node_modules/@resvg/resvg-js').exists():
            subprocess.run(['node',str(ROOT/'tools/models/render_native.cjs'),str(target)],check=True,cwd=ROOT,capture_output=True)


def freeze(models):
    manifest_path=ROOT/'tests/fixtures/manifest.json'
    manifest=json.loads(manifest_path.read_text(encoding='utf-8')); target=ROOT/'tests/fixtures/view-cases'; target.mkdir(parents=True,exist_ok=True)
    records=[]
    for path in sorted(models.glob('*.json')):
        meta=json.loads(path.read_text()); archive=path.with_suffix('.3dm')
        if digest(archive)!=meta['sha256']: raise ValueError('Archive hash mismatch '+str(path))
        # Freeze contracts only, including M; all generated geometry stays ignored.
        definition={k:meta[k] for k in ('schemaVersion','id','family','tier','flavor','seed','units','tolerance',
                    'hook','generator','sourceFixtures','notes','expected','views')}
        write_json(target/path.name,definition)
        entry={'id':'models/'+archive.name,'localModelSuite':True,'external':True,'manualOnly':True,'hook':False,
               'sha256':digest(archive),'sizeBytes':archive.stat().st_size,'sidecarSha256':digest(path),
               'units':meta['units'],'tier':meta['tier'],'seed':meta['seed'],'flavor':meta['flavor'],
               'generator':'tools/models/view_cases.py','stats':meta['stats'],'description':'Non-world-aligned view cases 5–8: '+meta['id']}
        entry.update(generatedPath='../../artifacts/view-models/'+archive.name,
                     generatedSidecar='../../artifacts/view-models/'+path.name,
                     definition='view-cases/'+path.name,definitionSha256=digest(target/path.name))
        records.append(entry)
    ids={r['id'] for r in records}
    manifest['fixtures']=[r for r in manifest['fixtures'] if r['id'] not in ids]+records
    write_json(manifest_path,manifest,sort_keys=False)


def compare(models,out):
    # Geometry-free comparison of saved 2D polylines: no Rhino host, license or shared slot.
    from view_audit import compare as compare_loci, profile_check, planar_control_witness
    results=[]
    for path in sorted(models.glob('*.json')):
        meta=json.loads(path.read_text())
        for v in meta['views']:
            current=prefix(out,meta['id'],v['name'])
            if meta['id']=='view-planar-control':
                write_json(Path(str(current)+'.analytic-rays.json'),planar_control_witness(v,
                    {name:Path(str(current)+f'.{suffix}-loci.json') for name,suffix in [('native','native'),('M2D+','m2d'),('exact kernel','planar')]
                     if Path(str(current)+f'.{"m2d" if suffix=="planar" else suffix}.json').exists()}))
            native_path=Path(str(current)+'.native-loci.json'); m2d_path=Path(str(current)+'.m2d-loci.json')
            audit_path=Path(str(current)+'.audit.json')
            inputs={p.name:digest(p) for p in (native_path,m2d_path) if p.exists()}
            inputs['auditCode']=digest(Path(__file__).with_name('view_audit.py'))
            complete=collector_complete(current,'native') and collector_complete(current,'m2d')
            inputs['collectorComplete']=complete
            previous=json.loads(audit_path.read_text()) if audit_path.exists() else {}
            payloads=all(Path(str(current)+f'.{engine}.json').exists() for engine in ('native','m2d'))
            if inputs!=previous.get('inputs') and payloads and native_path.exists() and m2d_path.exists():
                locus=compare_loci(native_path,m2d_path,meta['tolerance']*4)
                locus['complete']=complete; locus['passed']=locus['passed'] and complete
                write_json(audit_path,{'inputs':inputs,'locus':locus,
                    'nativeProfile':profile_check(native_path,v.get('expected',{}),meta['tolerance']),
                    'm2dProfile':profile_check(m2d_path,v.get('expected',{}),meta['tolerance'])})
            exp=v.get('expected',{}); ref=None; kind=None
            if 'metamorphic' in exp and exp['metamorphic']['rotationDegrees']:
                ref=prefix(out,exp['metamorphic']['referenceModel'],exp['metamorphic']['referenceView']); kind='rotation'
            elif exp.get('symmetry',{}).get('periodDegrees'):
                ref=prefix(out,meta['id'],f"Radial {exp['azimuthDegrees']%30:03} forward"); kind='radial periodicity'
            if ref is None: continue
            current=prefix(out,meta['id'],v['name'])
            if ref==current: continue
            for engine in ('native','m2d'):
                a=Path(str(ref)+f'.{engine}-loci.json'); b=Path(str(current)+f'.{engine}-loci.json')
                record={'model':meta['id'],'view':v['name'],'engine':engine,'kind':kind,'reference':ref.name}
                if not a.exists() or not b.exists(): record['status']='incomplete'
                elif not collector_complete(ref,engine) or not collector_complete(current,engine):
                    record.update(status='incomplete',reason='locus collector incomplete or payload checkpoint absent')
                else:
                    report=Path(str(current)+f'.{engine}-metamorphic.json')
                    hashes={'reference':digest(a),'current':digest(b),'auditCode':inputs['auditCode']}
                    result=json.loads(report.read_text()) if report.exists() else {}
                    if result.get('inputs')!=hashes:
                        result=compare_loci(a,b,meta['tolerance']*4); result['inputs']=hashes
                        write_json(report,result)
                    record.update(status='pass' if result['passed'] else 'fail',
                                  missed=result['expectedToActual']['missed'],extra=result['actualToExpected']['missed'],evidence=report.name,
                                  geometryOnlyPassed=all(result[d]['missingInkSamples']==0 for d in ('expectedToActual','actualToExpected')),
                                  uncertaintyClassSamples=sum(result[d]['uncertaintyClassSamples'] for d in ('expectedToActual','actualToExpected')),
                                  confidentVisibleHiddenSwapSamples=sum(result[d]['confidentVisibleHiddenSwapSamples'] for d in ('expectedToActual','actualToExpected')))
                results.append(record)
    write_json(out/'metamorphic.json',results)


def report(models,out):
    stopped=(out/'owner-stop.json').exists()
    metas=[json.loads(p.read_text()) for p in sorted(models.glob('*.json'))]
    rows=[]
    for meta in metas:
        for v in meta['views']:
            path=Path(str(prefix(out,meta['id'],v['name']))+'.json')
            row=json.loads(path.read_text()) if path.exists() else {'status':'not run','model':meta['id'],'view':v['name']}
            admission=Path(str(prefix(out,meta['id'],v['name']))+'.admission.json')
            unadmitted=admission.exists() and json.loads(admission.read_text()).get('status')=='handoff_paused'
            if stopped and (row['status']=='not run' or unadmitted):
                row['recordedRunnerStatus']=row['status']
                row['status']='skipped_owner_decision'
                row['skipReason']='Owner ended the matrix; no Rhino admission for this view'
            if 'native' not in row and 'computeSeconds' in row:
                row['native']={'computeSeconds':row['computeSeconds'],'status':'baseline extraction incomplete'}
            phase_path=Path(str(prefix(out,meta['id'],v['name']))+'.engine-phase.json')
            if phase_path.exists(): row['enginePhaseEvidence']=json.loads(phase_path.read_text())
            audit=Path(str(prefix(out,meta['id'],v['name']))+'.audit.json')
            if audit.exists():
                row['audit']=json.loads(audit.read_text())
                row['locusMissingSamples']=row['audit']['locus']['expectedToActual']['missed']
                row['locusExtraSamples']=row['audit']['locus']['actualToExpected']['missed']
            row['case']=v['case']; rows.append(row)
    metamorphic=json.loads((out/'metamorphic.json').read_text()) if (out/'metamorphic.json').exists() else []
    summary={'models':len(metas),'views':len(rows),'cases':dict(Counter(r['case'] for r in rows)),
             'status':dict(Counter(r['status'] for r in rows)),
             'sectionOracles':dict(Counter(r.get('sectionOracle',{}).get('status','incomplete') for r in rows)),
             'nativeCutProfileAudits':dict(Counter(r.get('audit',{}).get('nativeProfile',{}).get('status','incomplete') for r in rows)),
             'm2dOutputProfileAudits':dict(Counter(r.get('audit',{}).get('m2dProfile',{}).get('status','incomplete') for r in rows)),
             'm2dUnresolvedPieces':sum(r.get('m2d',{}).get('unresolved',0) for r in rows),
             'boundedTessellationFallbacks':{engine:sum(len(r.get(engine,{}).get('tessellationFallbacks',[])) for r in rows) for engine in ('native','m2d')},
             'zeroExtentFallbackPoints':{engine:sum(bool(f.get('PointOnly',f.get('pointOnly',False))) for r in rows
                 for f in r.get(engine,{}).get('tessellationFallbacks',[])) for engine in ('native','m2d')},
             'locusCollectorIncompleteViews':sum(bool(r.get('native',{}).get('extractionErrors') or r.get('m2d',{}).get('extractionFailures')) for r in rows),
             'disagreementViewsByClass':{key:sum(any(r.get('audit',{}).get('locus',{}).get(d,{}).get(key,0)>0
                 for d in ('expectedToActual','actualToExpected')) for r in rows)
                 for key in ('missingInkSamples','uncertaintyClassSamples','confidentVisibleHiddenSwapSamples')},
             'metamorphic':dict(Counter(r['engine']+'/'+r['kind']+'/'+r['status'] for r in metamorphic)),
             'completedLocusDisagreements':sum(bool(r.get('locusMissingSamples',0)+r.get('locusExtraSamples',0)) for r in rows)}
    phases={'m2d':'M2D+ route C','prepare':'raw native HLD preparation','compute':'raw native HLD',
            'extract':'raw native HLD extraction','compare':'locus comparison','initialize':'Rhino startup','read':'model load'}
    timeouts=[{'model':r['model'],'view':r['view'],'engine':phases.get(r.get('phase'),'runner'),
               'phase':r.get('enginePhaseEvidence',{}).get('phase',r.get('phase','unknown')) if r.get('phase')=='m2d' else r.get('phase','unknown'),
               'elapsedSeconds':r.get('wallSeconds'),'enginePhaseEvidence':r.get('enginePhaseEvidence'),'status':r['status']}
              for r in rows if r['status'] in ('wall_timeout','private_memory_limit')]
    write_json(out/'timeouts.json',timeouts)
    write_json(out/'summary.json',summary)
    write_json(out/'skipped.json',[{'model':r['model'],'view':r['view'],'reason':r['skipReason'],
        'recordedRunnerStatus':r['recordedRunnerStatus']} for r in rows if r['status']=='skipped_owner_decision'])
    # Keep bulky per-view evidence in its own files; this index is deliberately compact.
    esc=html.escape
    content=['<section id="view-cases"><h1>View cases · non-world-aligned cases 5–8</h1>',
        '<p>Local serial Rhino.Inside; normal priority; one shared slot released after each view. Raw HLD is a baseline, not ground truth. '
        'Route C uses the stability certificate on main. Locus comparisons are bidirectional samples of tessellated curves at 0.05 m spacing '
        '(minimum 8 samples per segment), 4× model tolerance; no full-curve correctness claim. Cut is visible ink; area/loops are checked separately. '
        'Duplicate and Unset counts flag possible native quirks; they do not excuse unexplained differences.</p>',
        '<pre>'+esc(json.dumps(summary,indent=2))+'</pre><h2>Shaded model references</h2>']
    for meta in metas:
        content.append('<h3>'+esc(meta['id'])+'</h3>')
        for camera in ('SW','Front'):
            image=ROOT/'artifacts/view-models/screenshots'/f"{meta['id']}-{camera}.png"
            if image.exists(): content.append(f'<a href="../view-models/screenshots/{image.name}"><img loading="lazy" width="480" src="../view-models/screenshots/{image.name}" alt="{esc(meta["id"])} {camera}; offscreen CPU shaded reference"></a>')
    content.append('<h2>Selected M2D+ drawings and profile details</h2>')
    chosen=[prefix(out,'view-curved-steel','Normal CHS-arc station 3').name+'.profile.png',
            prefix(out,'view-curved-steel','Normal twisted-RHS station 3').name+'.profile.png',
            prefix(out,'view-round-tower','Radial 015 forward').name+'.png',
            prefix(out,'view-round-tower','Radial 090 forward').name+'.png']
    for name in chosen:
        if (out/name).exists(): content.append(f'<a href="view-cases/{name}"><img width="480" src="view-cases/{name}" alt="{esc(name)}"></a>')
    content.append('<h2>Per-view results</h2><table><tr><th>Model / view</th><th>Status</th><th>Native s</th><th>M2D+ s</th><th>Miss / extra samples</th><th>Unresolved</th><th>M2D+ region / native cut profile</th><th>Evidence</th></tr>')
    for r in rows:
        pre=prefix(out,r['model'],r['view']).name
        evidence=f'<a href="view-cases/{pre}.json">JSON</a>' if (out/(pre+'.json')).exists() else 'not run'
        if (out/(pre+'.png')).exists(): evidence+=f' <a href="view-cases/{pre}.png">M2D+ PNG</a>'
        if (out/(pre+'.native.svg')).exists(): evidence+=f' <a href="view-cases/{pre}.native.svg">native SVG</a>'
        if (out/(pre+'.audit.json')).exists(): evidence+=f' <a href="view-cases/{pre}.audit.json">locus/profile audit</a>'
        content.append('<tr>'+''.join('<td>'+str(x)+'</td>' for x in [esc(r['model']+' / '+r['view']),esc(r['status']),
            round(r['native']['computeSeconds'],3) if r.get('native',{}).get('computeSeconds') is not None else '—',
            round(r['m2d']['seconds'],3) if r.get('m2d',{}).get('seconds') is not None else '—',
            str(r.get('locusMissingSamples','—'))+' / '+str(r.get('locusExtraSamples','—'))+
                (' (partial extraction)' if r.get('audit',{}).get('locus',{}).get('complete') is False else ''),r.get('m2d',{}).get('unresolved','—'),
            r.get('sectionOracle',{}).get('status','incomplete')+' / '+r.get('audit',{}).get('nativeProfile',{}).get('status','incomplete'),evidence])+'</tr>')
    content.append('</table><h2>Metamorphic results</h2><pre>'+esc(json.dumps(metamorphic,indent=2))+'</pre></section>')
    content.append('<h2>View cases · performance timeout findings</h2><pre>'+esc(json.dumps(timeouts,indent=2))+'</pre>')
    index=ROOT/'artifacts/baseline/index.html'; index.parent.mkdir(parents=True,exist_ok=True)
    old=index.read_text(encoding='utf-8') if index.exists() else '<!doctype html><meta charset="utf-8"><title>M2D+ local baseline</title><style>body{font:14px system-ui;margin:24px;color:#222;background:#fafafa}td,th{padding:5px;border:1px solid #ddd}table{border-collapse:collapse}img{max-width:48%;height:auto}pre{white-space:pre-wrap}</style>'
    old=old.split('<!-- VIEW CASES START -->')[0]
    index.write_text(old+'\n<!-- VIEW CASES START -->\n'+''.join(content),encoding='utf-8')
    doc=ROOT/'docs/research/test-models.md'
    before=doc.read_text(encoding='utf-8').split('<!-- VIEW CASES START -->')[0].rstrip()
    lines=['','<!-- VIEW CASES START -->','## View cases (5–8)','',
        'Local-only extension at `origin/main` 80981d7 (PRs #1–#14). Generators: `tools/models/view_cases.py`; '
        'runner adapter: `view_engine.py` + `tools/render/ViewCaseRunner.cs`; managed-only audits/report: `view_audit.py` / `view_report.py`. '
        'No CI, installation, commit or push. Large archives and every drawing remain gitignored. '
        'Only the 20 JSON view definitions and expectations are frozen in `tests/fixtures/view-cases/`, with manifest hashes and `hook: false`; all generated geometry is ignored.','',
        '| Model | Objects | Views |','|---|---:|---:|']
    lines += [f"| {m['id']} | {m['stats']['objects']} | {len(m['views'])} |" for m in metas]
    lines += ['',
        'Case 5 uses the original blocks-form S/M orthogonal and curved geometry, rotated rigidly about world Z by 17° and 45°. '
        'Four unrotated control archives supply matching views. Each has four grid elevations, two grid sections, two plan levels and SW/SE/NE/NW axos. '
        'Coordinates are compared in co-rotated frames; no scale fitting or best-fit deformation is allowed. The model and view rotations should cancel.','',
        'Cases 6–7 cover five interior normalized-parameter stations (.125, .25, .5, .75, .875) per selected rail/profile family. '
        'Steel includes arc/spline CHS and RHS, the filleted I, twisted RHS, helical stringer and large curved CHS. '
        'All eight distinct diagrid isocurves are covered. Repeated buildings use the first instance of each curved mullion/transom/roof-diagonal prototype; '
        'the existing space frame has straight CHS chords, so four connection families are explicitly labeled as straight. '
        'This is archetype coverage, not five cuts of every repeated instance. Facade views use five plan-guide stations; '
        'tilted roof planes are 15°, 30°, 60° from vertical. Plan guides are not claimed exact parametrizations of the doubly curved glass.','',
        'Case 8 uses twelve diametral planes at 15° increments, each looking both ways: 24 distinct directions over 360°, '
        'plus two reference plans. The new round tower has an annular core, four annular slabs, twelve columns and twelve curved curtain-wall sectors, '
        'with a 30° rotational period. Diagrid and curved tiers are not rotationally periodic, so their radial series has no fabricated symmetry pass. '
        'For multi-building tiers the centre axis is the first tower at world X=Y=0; the complete surrounding scene remains present.','',
        'Every sidecar stores GH Plane, Rectangle and Box values and the GUID/front/back of a persisted Extrusion view box. '
        'The bridge reads the saved object through `ViewRequest.Parse(Guid, document)`, the same resolver called by the component, '
        'and checks normal/up, front plane, crop and depth against all three GH value forms. '
        '`view_aux.py component` additionally solves six representative planes through the real registered GH component, with the saved box GUID '
        'and document-mode geometry; it checks nine outputs and aligned Cut/Hatch branch paths. This does not claim 423 full GH UI solves.','',
        'Raw native HLD runs first with native clipping, MT off; main `NativeDrawing` runs route C with physical trim/cap and the stability certificate. '
        'Both see the complete same scene, excluding view objects. Native evidence is checkpointed before route C, so an engine timeout cannot erase '
        'the completed baseline. The owned-process cap is 600 seconds / 8 GiB for features/S; after the first curved-M timeout the owner '
        'requested 120 seconds for remaining M views, with S/features prioritized. The already active second M attempt retained its original cap. '
        'Admission is excluded; normal priority and a five-second mutex-free '
        'handoff. Queued auxiliary work uses the same slot. Timings are indicative shared-machine measurements, not quiescent benchmarks.','',
        'The exact planar kernel runs only for a wholly eligible scene. Even the “orthogonal” building tiers contain CHS, curved fins and SubD: '
        'they are explicitly ineligible, not silently tested as a planar subset. Two small oblique planar control views run the production exact kernel '
        'against its independent rational oracle and raw native HLD.','',
        'Analytic normal-profile expectations are CHS π(R²−r²), two loops; RHS .4×.6−.3×.5 = .09 m², two loops; '
        'filleted I .44−(4−π)×.04² m², one loop. The twisted loft is sampled at its interpolation stations with the prescribed profile rotation. '
        'The helical sweep has no assumed exact profile area. Engine region area/loops and independently reassembled output-polyline area/containment '
        'are retained separately; RHS output bounds are checked in its co-rotated frame. No production intersection routine supplies the expected area.','',
        'The native profile audit uses visible section ink: raw HLD supplies no region objects. A failed loop reconstruction can therefore reflect '
        'hidden/absent cut edges rather than an incorrect intersection area. The main engine region-area oracle is reported separately.','',
        'Locus audits union coincident support-line intervals with Unresolved > Visible/Cut > Hidden, then sample both directions at 0.05 m '
        '(minimum eight subdivisions per segment), at four times model tolerance to cover display-chord approximation. '
        'Missing ink, Unresolved/confident differences and confident Visible/Hidden swaps are counted separately. '
        'These are sampled locus checks, not a continuous Hausdorff certificate. '
        'Collector failures are retained and make the affected metamorphic pair incomplete, never a pass. '
        'When Rhino tessellation fails on a valid curve, the collector subdivides positive-weight rational Bézier spans until their control hull '
        'bounds chord error by model tolerance. Analytic-circle and independently evaluated rational-cubic tests validate the implementation. '
        'Zero-extent returned curves retain their point coordinates and diagnostics; point loci do not contribute to the common line-locus union. '
        'Small closed loops are preserved. Original collector failures and retry history remain in `artifacts/view-retries/` and `view-cases/retries.json`. '
        'Native Duplicate/Unset counts flag possible quirks; they never automatically excuse a disagreement. Native ordering attribution needs an explicit rerun.','',
        'Current measured inventory (capped/incomplete views remain in the denominator):','',
        '```json',json.dumps(summary,indent=2),'```','',
        'Per-view timings, native diagnostics, M2D+ Unresolved counts, analytic region verdicts, canonical locus audits and metamorphic results are '
        'linked from `artifacts/baseline/index.html` → **View cases**. Full JSONs and M2D+ SVG/PNG renders are in `artifacts/baseline/view-cases/`. '
        'Shaded SW/Front references use the labeled offscreen CPU mesh fallback at `artifacts/view-models/screenshots/`; no existing Rhino process is used.','',
        '### View cases: observed failures','']
    from view_findings import findings
    grouped=findings(rows,summary,metamorphic,out)
    lines+=grouped
    (ROOT/'docs/research/view-case-findings.md').write_text('# View-case findings\n\n'+'\n'.join(grouped)+'\n',encoding='utf-8')
    with index.open('a',encoding='utf-8') as f:
        f.write('<h2>View cases · ranked causal findings</h2><pre>'+esc('\n'.join(grouped))+'</pre>')
    lines+=['','### View cases: performance timeouts','',
        'Every capped attempt is a finding for the next section-performance work, not a correctness verdict. '
        '`elapsed` is execution wall time after admission, including startup/capture and any completed native baseline. '
        'The phase identifies the engine active when the owned job was stopped. Later workers sample the managed native-call counter '
        'once per second: `m2d/pre-native` means route C had not yet called HLD; `m2d/native-or-extraction` means that call had started. '
        'This probe touches no live geometry or document and does not change the engine. The first two M attempts predate the probe. '
        'Completed native timing remains in the per-view JSON.','',
        '| Model / view | Engine | Phase reached | Elapsed s |','|---|---|---|---:|']
    for r in timeouts: lines.append(f"| {r['model']} / {r['view']} | {r['engine']} | {r['phase']} | {r['elapsedSeconds']:.3f} |")
    observed=[r for r in rows if r.get('m2d',{}).get('firstHldObservedSeconds') is not None]
    if observed:
        lines+=['','Largest observed delays before the first M2D+ HLD call (one-second sampling, includes capture):','',
                '| Model / view | Before first HLD, s | Capture s | Engine total s |','|---|---:|---:|---:|']
        for r in sorted(observed,key=lambda r:r['m2d']['firstHldObservedSeconds'],reverse=True)[:10]:
            d=r['m2d']; lines.append(f"| {r['model']} / {r['view']} | {d['firstHldObservedSeconds']:.3f} | {d['captureSeconds']:.3f} | {d['seconds']:.3f} |")
    doc.write_text(before+'\n'+'\n'.join(lines)+'\n',encoding='utf-8')
    print(json.dumps(summary,indent=2))


def main():
    p=argparse.ArgumentParser(description=__doc__); p.add_argument('--models',type=Path,default=ROOT/'artifacts/view-models')
    p.add_argument('--out',type=Path,default=ROOT/'artifacts/baseline/view-cases'); p.add_argument('--freeze',action='store_true'); p.add_argument('--compare',action='store_true')
    p.add_argument('--follow',action='store_true',help='Refresh after each 20 finished attempts, then exit after the complete local matrix; starts no Rhino')
    args=p.parse_args(); args.out.mkdir(parents=True,exist_ok=True)
    if args.freeze: freeze(args.models)
    if args.compare:
        from view_audit import initialize_core
        initialize_core()
    metas=[json.loads(p.read_text()) for p in args.models.glob('*.json')]
    stems=[prefix(args.out,m['id'],v['name']) for m in metas for v in m['views']]
    previous=-20
    while True:
        finished=sum(Path(str(s)+'.watch.json').exists() for s in stems)
        if not args.follow or finished-previous>=20 or finished==len(stems):
            if args.compare: compare(args.models,args.out)
            details(args.out); report(args.models,args.out); previous=finished
        if not args.follow or (args.out/'owner-stop.json').exists(): break
        if finished==len(stems):
            rows=[json.loads(Path(str(s)+'.json').read_text()) for s in stems]
            if all(r.get('phase')=='complete' or r.get('status') in ('exception','wall_timeout','private_memory_limit','worker_crash') for r in rows):
                write_json(args.out/'completed-matrix.json',{'viewsAttempted':finished,'models':len(metas),
                    'utc':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),'correctnessVerdicts':'see summary.json; capped/incomplete is never pass'})
                break
        time.sleep(15)


if __name__=='__main__': main()
