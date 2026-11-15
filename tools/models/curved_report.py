"""Append curved evidence without replacing the earlier orthogonal research.

Optional --orthogonal-baseline imports the prior gallery/summary, read-only.
Subsequent runs use the local copies under artifacts/baseline.
"""
import argparse
import base64
from collections import Counter
import html
import json
from pathlib import Path
import shutil
import statistics

from common import ROOT, digest, write_json
from report import CSS, records

DESCRIPTIONS = {
    'curved-steel': 'Arc and 3D-spline CHS/RHS, segmented filleted I-beam, helical stringer, twisted RHS and an oblique toroidal hollow section.',
    'diagrid-shell': 'Open doubly curved NURBS roof with CHS surface-isocurve families and overlapping nodes in different layer styles.',
    'space-frame': 'Barrel-vault space frame: 487 hollow members and 182 spherical nodes, stored as two reusable definitions.',
    'curved-facade': 'Freeform curtain wall, curved mullions/transoms, Brep/mesh glass twins, ruled twisting fins, curved louvres and nine circular perforations.',
    'tangent-continuity': 'G1 fillets, analytic G1/G2 plane-to-polynomial joins, torus/sphere seam orientations and an exactly edge-on sheet; all four tangent/seam setting combinations.',
    'subd-vs-nurbs': 'One organic canopy as SubD, converted NURBS and a density-3 mesh; translated copies for comparison.'}
MARKER = '\n## Curved / freeform extension\n'
E = html.escape


def data_image(path):
    kind = 'image/svg+xml' if path.suffix == '.svg' else 'image/png'
    return 'data:' + kind + ';base64,' + base64.b64encode(path.read_bytes()).decode('ascii')


def summarize(rows, orthogonal):
    result = []
    for tier in ('S','M','L','XL'):
        for flavor in ('blocks','exploded'):
            rs = [r for r in rows if r.get('tier') == tier and r.get('flavor') == flavor and not r.get('multipleThreads')]
            if not rs: continue
            complete = [r for r in rs if r.get('phase') == 'complete' and r['status'] in ('ok','extraction_partial')]
            old = next((r for r in orthogonal.get('performance',[]) if r['tier']==tier and r['flavor']==flavor and not r['threads']),{})
            common = [r['computeSeconds'] for r in complete if r['view']!='Grazing']
            median = statistics.median(common) if common else None
            baseline = old.get('medianCompute')
            result.append(dict(tier=tier,flavor=flavor,attempts=len(rs),completed=len(complete),
                median=statistics.median(r['computeSeconds'] for r in complete) if complete else None,
                comparableMedian=median, orthogonalMedian=baseline,
                ratio=median/baseline if median is not None and baseline and len(common)==(1 if tier=='XL' else 9) else None,
                commonViews=len(common),maxCompute=max((r['computeSeconds'] for r in complete),default=None),
                wall=sum(r.get('wallSeconds',0) for r in rs),peakGiB=max(r.get('peakPrivateBytes',0)/1024**3 for r in rs),
                partialViews=sum(r['status']=='extraction_partial' for r in complete),
                unsetsCompleted=sum(r.get('unsetProxies',0) for r in complete),
                extractionErrorsCompleted=sum(len(r.get('extractionErrors',[])) for r in complete),
                statuses=dict(Counter(r['status'] for r in rs))))
    return result


def coordination_evidence(rows, session):
    """Keep shared-session qualifications separate from measured compute times."""
    identified = {(r['model'], r['view']) for r in session.get('beforeHandoffViews', [])}
    affected = []
    for r in rows:
        note = r.get('coordination', {}).get('session')
        if note or (r['model'], r['view']) in identified:
            affected.append({'model': r['model'], 'view': r['view'],
                             'session': note or session.get('note', ''),
                             'admissionSeconds': r.get('admissionSeconds'),
                             'status': r['status']})
    return {'session': session, 'identifiedViews': affected,
            'totalAdmissionSeconds': sum(r['admissionSeconds'] or 0 for r in affected),
            'qualification': 'Indicative timings. A shared research session is identified; simultaneous non-mutex CPU work was not instrumented. Earlier runs are not certified machine-quiescent.'}


def comparison_chart(summary):
    values=summary
    if not values: return '<p>No tier timings yet.</p>'
    maximum=max(max((s['comparableMedian'] or 0) if s['commonViews']==(1 if s['tier']=='XL' else 9) else 0,
                    s['orthogonalMedian'] or 0) for s in values)*1.1 or 1
    h=len(values)*45+48
    out=[f'<svg viewBox="0 0 700 {h}" style="width:100%;height:{h}px" role="img" aria-label="Curved and orthogonal native HLD seconds">',
         '<text x="155" y="16" fill="currentColor" font-size="12">Upper: earlier orthogonal · lower: curved · common-view median seconds</text>']
    for i,s in enumerate(values):
        y=35+i*45
        out.append(f'<text x="140" y="{y+12}" text-anchor="end" fill="currentColor" font-size="12">{s["tier"]} {s["flavor"]}</text>')
        required=1 if s['tier']=='XL' else 9
        comparable=s['comparableMedian'] if s['commonViews']==required else None
        for j,(v,c) in enumerate(((s['orthogonalMedian'],'var(--muted-foreground,#9299a4)'),(comparable,'var(--chart-1,#2785c5)'))):
            if v is None:
                capped=any(k in s['statuses'] for k in ('wall_timeout','private_memory_limit'))
                label=f'{s["commonViews"]}/{required} common views completed; '+('capped' if capped else 'pending')
                out.append(f'<text x="155" y="{y+j*15+10}" fill="var(--warning,#d48443)" font-size="11">{label}</text>')
                continue
            w=v/maximum*450
            out.append(f'<rect x="155" y="{y+j*15}" width="{w:.2f}" height="11" fill="{c}"/><text x="{160+w:.2f}" y="{y+j*15+10}" fill="currentColor" font-size="11">{v:.2f}</text>')
    out.append('</svg><p style="font-size:12px">Cold single trials, MT off; indicative shared-session timings. Curved adds a grazing view, excluded here. XL, when run, compares one section per form. Capped attempts are excluded, never treated as successes.</p>')
    return ''.join(out)


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--models',type=Path,default=ROOT/'artifacts/models')
    p.add_argument('--results',type=Path,default=ROOT/'artifacts/baseline')
    p.add_argument('--docs',type=Path,default=ROOT/'docs/research/test-models.md')
    p.add_argument('--orthogonal-baseline',type=Path)
    args=p.parse_args(); args.results.mkdir(parents=True,exist_ok=True)
    if args.orthogonal_baseline:
        provenance={}
        for name in ('index.html','summary.json'):
            source=args.orthogonal_baseline/name
            target=args.results/('orthogonal-'+name)
            shutil.copyfile(source,target)
            provenance[name]={'source':str(source.resolve()),'sha256':digest(source)}
        write_json(args.results/'orthogonal-provenance.json',provenance)
    oldpath=args.results/'orthogonal-summary.json'
    old=json.loads(oldpath.read_text()) if oldpath.exists() else {}
    verification_path=args.results/'curved-verification.json'
    verification=json.loads(verification_path.read_text()) if verification_path.exists() else {}
    models=[]
    for f in sorted(args.models.glob('*.json')):
        m=json.loads(f.read_text())
        if m.get('family')=='curved': models.append(m)
    byid={m['id']:m for m in models}
    rows=[r for r in records(args.results) if r['model'] in byid]
    session_path=args.results/'coordination-session.json'
    session=json.loads(session_path.read_text()) if session_path.exists() else {}
    coordination=coordination_evidence(rows,session)
    summary=summarize(rows,old)
    chart=comparison_chart(summary)
    omitted=session.get('wrapUpScope',{}).get('omittedNativeModels',[])
    omission_note=('Native batches omitted at the user’s wrap-up request: '+', '.join(omitted)+'. Their generated models, validation and shaded references remain available.') if omitted else ''
    if omission_note: chart+='<p>'+E(omission_note)+'</p>'
    out=['<section id="curved"><h2>Curved</h2><p>Curved/freeform models · local-only · fresh serial Rhino.Inside workers · 600 s / 8 GiB caps. Native HLD is an execution baseline, not a visibility oracle. Tangent/seam comparison settings are honored per view.</p>',chart,
         '<p>Shaded references use Rhino meshes and the offscreen CPU depth buffer. They are opaque reference renders, not native viewport captures. Click a drawing to inspect.</p>',
         '<p>'+E(coordination['qualification'])+' The runner releases admission after every view and leaves a 30-second handoff window. Queue and handoff time are excluded from execution wall time.</p>']
    captures=[]
    for m in models:
        out.append(f'<details open><summary>{E(m["id"])} · {m["stats"]["objects"]:,} objects · {m["sizeBytes"]/1e6:.3f} MB</summary><p>{E(DESCRIPTIONS.get(m["id"],"Rotated curved facade bays, fins, CHS roof grids and twisting towers; blocks/exploded geometry equivalents."))}</p>')
        if m['id'] in omitted:
            out.append('<p>Native baseline omitted by the final wrap-up scope.</p>')
        out.append('<div class="grid">')
        capture=args.models/'screenshots'/(m['id']+'.capture.json')
        c=json.loads(capture.read_text()) if capture.exists() else {}
        current=c.get('status')=='ok' and c.get('fixtureSha256')==m['sha256'] and c.get('sidecarSha256')==digest(args.models/(m['id']+'.json'))
        for v in ['SW','Front']+(['Top'] if m['tier'] else []):
            path=args.models/'screenshots'/f'{m["id"]}-{v}.png'
            if current and path.exists():
                out.append(f'<article class="card"><h3>Shaded · {v}</h3><div class="drawing"><img style="width:100%;height:100%;object-fit:contain" src="{data_image(path)}" alt="{m["id"]} {v}"></div></article>')
                captures.append(dict(model=m['id'],view=v,path=str(path.resolve()),sha256=digest(path)))
        for r in (r for r in rows if r['model']==m['id']):
            path=r['_path'].with_suffix('.svg'); c=r.get('computeSeconds')
            timing=f'{c:.3f} s HLD' if c is not None else 'HLD incomplete'
            completed=r.get('phase')=='complete' and r['status'] in ('ok','extraction_partial')
            preview=path.with_suffix('.png') if path.exists() and path.stat().st_size>1_000_000 and path.with_suffix('.png').exists() else path
            image=f'<img style="width:100%;height:100%;object-fit:contain" src="{data_image(preview)}" alt="{E(r["view"])}">' if completed and preview.exists() else '<p>No completed drawing</p>'
            vector_link=f'<a href="{E(path.name)}">Full native SVG</a>' if completed and preview.suffix=='.png' else ''
            out.append(f'<article class="card"><h3>Native · {E(r["view"])}</h3><div class="drawing">{image}</div><p class="stats">{timing} · {r.get("wallSeconds",0):.2f} s wall · {E(r["status"])}<br>Unset {r.get("unsetProxies",0)} · {E(str(r.get("pieceCounts",{})))}<br>{vector_link}</p></article>')
        out.append('</div></details>')
    observations=args.results/'curved-observations.json'
    findings=json.loads(observations.read_text()) if observations.exists() else {'findings':[]}
    out.append('<h3>Inspection notes</h3><ul>'+''.join('<li>'+E(f['model']+' / '+f['view']+': '+f['observation'])+'</li>' for f in findings['findings'])+'</ul></section>')
    oldindex=args.results/'orthogonal-index.html'
    if oldindex.exists():
        template=oldindex.read_text(encoding='utf-8')
        # Insert before the existing dialog/script so its click handlers cover new images.
        template=template.replace('<dialog id="zoom">',''.join(out)+'<dialog id="zoom">',1)
    else:
        template='<!doctype html><meta charset="utf-8"><style>'+CSS+'</style><main>'+''.join(out)+'</main>'
    (args.results/'index.html').write_text(template,encoding='utf-8')
    (args.results/'curved-summary.html').write_text('<!doctype html><meta charset="utf-8"><style>body{font-family:var(--font-sans,system-ui);color:var(--foreground,#222)}svg text{font-family:inherit}</style>'+chart,encoding='utf-8')
    write_json(args.results/'curved-summary.json',{'models':len(models),'attempts':len(rows),'statuses':dict(Counter(r['status'] for r in rows)),
               'performance':summary,'screenshots':captures,'coordination':coordination,'omittedNativeModels':omitted,
               'unsetProxies':sum(r.get('unsetProxies',0) for r in rows)})
    write_json(args.models/'screenshots'/'curved-manifest.json',{'captures':captures})
    doc=[MARKER,'Local-only extension, generated with `tools/models/curved.py`; seed 20261114. No plug-in contracts, CI or hook jobs change. All six feature files are below 2 MB and marked `hook: false`; performance archives and results stay gitignored.\n',
         '| Model | Objects / expanded | MB | Coverage |','|---|---:|---:|---|']
    for m in models:
        purpose = DESCRIPTIONS[m['id']] if m['id'] in DESCRIPTIONS else str(m['expected']['buildings'])+' twisting buildings; '+str(m['expected']['floors'])+' storeys; rotated curved bays and roof grids.'
        doc.append(f'| {m["id"]} | {m["stats"]["objects"]:,} / {m["stats"]["expandedObjects"]:,} | {m["sizeBytes"]/1e6:.3f} | {purpose} |')
    doc += ['\n### Curved validation and method\n',
        'Feature validation reads the native archives, checks geometry, size, hashes and edge inventories, and independently intersects the oblique CHS. The torus-plane area comes from a 65,536-sample polygon line integral of the analytic torus parametrization (not HLD): 0.7029040164 m², one region and one hole; convergence delta 3.23e-9 m² and acceptance 2e-5 m². Native Brep-plane section area is 0.7029084851 m². The perforated plate independently checks nine holes and area 9 − 9π(0.25)² m².\n',
        'G1 and G2 test joins use z=0.6x² and z=0.6x³ respectively adjoining z=0. Their shared edge has matching position/tangent; only the cubic also matches curvature. Sidecars list actual Brep tangent, crease, seam and naked edge indices. The I-beam sweeps each straight/fillet profile segment separately to preserve tangent face boundaries. The roof shell is an open Brep; its cut has outline only, no hatch. The diagrid follows surface isocurves, not claimed geodesics. No node/member union is performed.\n',
        'Mesh/Brep glass checks sample vertices and face centroids after inverse translation; canopy checks sample NURBS surfaces bidirectionally after explicit SubD conversion, plus mesh face centroids. These measure sampled deviation, not a global Hausdorff bound. `artifacts/models/validation.json`, `curved-pair-validation.json` and the repeat directory retain the checks.\n',
        'All six feature archives and both S forms regenerated byte-for-byte (`artifacts/curved-repeat/byte-comparison.json`). The diagrid archive produced a different native in-memory JSON reserialization hash when the identical file was loaded twice; the validator retains that diagnostic and treats identical archive bytes as authoritative. All S/M/L/XL blocks/exploded pairs passed world-space geometry and camera equivalence (500/5,000/20,000/50,000 objects). The glass mesh sampled maximum deviation was 0.000406 m; canopy mesh face-centroid deviation was 0.000389 m; SubD-to-NURBS sampled deviation was 2.85e-15 m.\n',
        verification.get('summary','Build/hook verification evidence is unavailable in this result directory.')+' Frozen feature sidecars come from the verified repeat generation; original baseline input sidecars remain under artifacts with their source-at-generation hashes. The later mutex handoff change alters the current common.py dependency hash, not those already-generated archive bytes. Baseline rows retain their own harness hashes.\n',
        'The ten tier views are four elevations, two plan cuts, transverse and long-overlap roof sections, SW axo and a nearly tangent grazing elevation. Native trials use threading off. '+('The final wrap-up scope is all 40 feature views, both S/M forms (20 views each), and L blocks (10 views): 90 attempts. '+omission_note if omitted else 'All new feature views and S–L views are selected; XL selects only the roof section once per form, last.')+' Each fresh worker has a 600 s execution-wall / 8 GiB owned-job cap. After the coordination change, queue time is recorded separately and excluded from that execution cap; earlier rows include process launch in their wall measurement. Raw HLD explicitly normalizes SubD to Brep and does not implement style-aware region merging or hatch semantics.\n',
        'Exact object counts truncate the final storey/bay. Roof heights and the upper plan cut follow the occupied storey count. Independent native Brep-plane intersections confirm both plan cuts hit facade panels and the roof section hits a roof member in every form/tier (24 checks). An earlier layout trial is retained separately under `artifacts/baseline/curved-layout-pilot/` and excluded from these measurements.\n',
        '### Curved timings versus earlier orthogonal tiers\n',
        '| Tier | Form | Completed / attempted | Partial views / Unset (completed) | Curved median s (all) | Curved common-view median s | Earlier orthogonal median s | Ratio | Max HLD s | Wall s | Peak GiB |',
        '|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|']
    fmt=lambda v:'—' if v is None else f'{v:.3f}'
    for s in summary:
        quality=f'{s["partialViews"]} / {s["unsetsCompleted"]:,}' if s['completed'] else '—'
        doc.append(f'| {s["tier"]} | {s["flavor"]} | {s["completed"]}/{s["attempts"]} | {quality} | {fmt(s["median"])} | {fmt(s["comparableMedian"])} | {fmt(s["orthogonalMedian"])} | {fmt(s["ratio"])} | {fmt(s["maxCompute"])} | {s["wall"]:.1f} | {s["peakGiB"]:.2f} |')
    capped=[r for r in rows if r['status'] in ('wall_timeout','private_memory_limit','admission_timeout')]
    if capped:
        doc += ['\nCapped attempts (no completed drawing is implied):\n',
                '| Model | View | Limit reached | Last phase | Execution wall s |',
                '|---|---|---|---|---:|']
        for r in capped:
            doc.append(f'| {r["model"]} | {r["view"]} | {r["status"]} | {r.get("phase","not admitted")} | {r.get("wallSeconds",0):.1f} |')
    doc += ['\nEarlier orthogonal rows are preserved from PR #6, not rerun here. Common-view medians exclude the added grazing view; these scenes differ and the ratios are descriptive, not a controlled curvature-only experiment. Ratios are suppressed unless all nine common views complete (one for XL). XL comparisons, when present, use one section per form. Capped attempts remain in denominators and are excluded from medians; partial extraction counts as completed compute but not certified geometry.\n',
            '### Shared research timing qualification\n',
            coordination['qualification']+' The user reported S3 performance research waiting on the shared mutex during the M batch. The active solve finished normally; the old controller stopped at a pre-Rhino gate. The revised runner queues before Rhino initialization, releases after each view, and leaves a 30-second mutex-free handoff window. Its 3,600-second admission timeout is separate from the 600-second execution cap. No other owner’s process was attached to or terminated.\n',
            f'The identified shared-session views below accumulated {coordination["totalAdmissionSeconds"]:.1f} s of measured admission time. Per-result `coordination`, `admissionSeconds` and `elapsedSeconds` retain the evidence; execution wall and HLD compute exclude the queue and handoff. A shared session does not establish simultaneous Rhino solves: cooperating work is serialized by the mutex. Earlier trials lack concurrency instrumentation and must also be treated as indicative.\n',
            '| Model | Shared-session views | Recorded queue s |','|---|---|---:|']
    for model in sorted({r['model'] for r in coordination['identifiedViews']}):
        affected=[r for r in coordination['identifiedViews'] if r['model']==model]
        measured=[r['admissionSeconds'] for r in affected if r['admissionSeconds'] is not None]
        queued=f'{sum(measured):.1f}' if measured else 'not recorded'
        if measured and len(measured)!=len(affected): queued+=' (some views unrecorded)'
        doc.append('| '+model+' | '+', '.join(r['view'] for r in affected)+' | '+queued+' |')
    doc += ['\n### Curved feature view measurements\n','| Model | View | HLD s | Unset | Visible / hidden / cut | Status |','|---|---|---:|---:|---|---|']
    for r in rows:
        if r.get('tier'): continue
        c=r.get('pieceCounts',{})
        doc.append(f'| {r["model"]} | {r["view"]} | {fmt(r.get("computeSeconds"))} | {r.get("unsetProxies",0)} | {c.get("Visible",0)} / {c.get("Hidden",0)} / {c.get("Cut",0)} | {r["status"]} |')
    doc += ['\n### Curved inspection findings\n']
    for f in findings['findings']:
        doc.append('- **'+f['model']+' / '+f['view']+'**: '+f['observation'])
    doc += ['\nThe **Curved** section of `artifacts/baseline/index.html` embeds shaded SW/Front feature references, SW/Front/Top tier references, previews of all completed native drawings and the comparison chart. Drawings over 1 MB use compact PNG previews with links to full native SVGs beside the HTML; retain that folder when sharing the gallery. Shaded images are explicitly the offscreen CPU mesh fallback. Native SVG curves are tessellated only for display. The screenshot list is `artifacts/models/screenshots/curved-manifest.json`.\n']
    original=args.docs.read_text(encoding='utf-8').split(MARKER)[0]
    args.docs.write_text(original+'\n'.join(doc),encoding='utf-8')
    print('Curved report:',len(models),'models,',len(rows),'attempts')


if __name__=='__main__': main()
