"""One admitted view: raw HLD then main route C, with intermediate evidence on disk."""
import json
from pathlib import Path
import sys
import time
from common import ROOT, digest, write_json
import native


def dependency_hashes():
    folder=ROOT/'artifacts/view-engine-v4-bin'
    return {str(p.relative_to(ROOT)):digest(p) for p in [Path(__file__).with_name('native.py'),
        folder/'M2DPlus.Render.dll',folder/'M2DPlus.Rhino.dll',folder/'M2DPlus.Core.dll']}


def run(model, sidecar, view, multiple_threads, emit_phase):
    import clr
    assembly=ROOT/'artifacts/view-engine-v4-bin/M2DPlus.Render.dll'
    clr.AddReference(str(assembly))
    from M2DPlus.Render import ViewCaseRunner,ViewCurveTessellation
    target=Path(sys.argv[sys.argv.index('--result')+1]).with_suffix('')
    source=Path(sys.argv[sys.argv.index('--model')+1])
    raw,svg=native.run(model,sidecar,dict(view,recordLoci=True),multiple_threads,emit_phase,
        polyline_fallback=lambda curve,tolerance:json.loads(ViewCurveTessellation.CurveJson(curve,tolerance)))
    write_json(str(target)+'.native-loci.json',raw.pop('loci',[]))
    write_json(str(target)+'.native.json',raw)
    Path(str(target)+'.native.svg').write_text(svg,encoding='utf-8')
    emit_phase('m2d',{'native':raw})
    started=time.perf_counter()
    engine=json.loads(ViewCaseRunner.Run(str(source.resolve()),json.dumps(view),str(target)))
    write_json(str(target)+'.m2d.json',engine)
    emit_phase('compare',{'native':raw,'m2d':engine})
    agreement=json.loads(ViewCaseRunner.Compare(str(target)+'.native-loci.json',str(target)+'.m2d-loci.json',sidecar['tolerance']*4,.05))
    agreement['complete']=not engine.get('extractionFailures') and not raw.get('extractionErrors')
    if not agreement['complete']: agreement.update(passed=False,incompleteReason='Native or M2D+ output locus extraction failed')
    write_json(str(target)+'.agreement.json',agreement)
    # Do not excuse a mismatch simply because a known native quirk is present.
    classes=[]
    if engine.get('extractionFailures'): classes.append('incomplete M2D+ output locus extraction; inspect extractionFailures')
    if raw.get('unsetProxies'): classes.append('native Unset/parentless proxies present')
    if raw.get('rawSegmentCounts',{}).get('Duplicate'): classes.append('native Duplicate slots present; missing partner not proven')
    if engine['unresolved']: classes.append('M2D+ Unresolved/section uncertainty')
    if not agreement['passed']: classes.append('unexplained classed locus disagreement; clipping/ordering attribution requires isolation')
    expectation=view.get('expected',{})
    section={'status':'unknown','reason':expectation.get('sectionTruth','no analytic profile expectation')}
    if 'area' in expectation:
        regions=[r for r in engine['regions'] if any(expectation['source'] in s for s in r['sources'])]
        area=sum(r['area'] for r in regions); loops=sum(r['loops'] for r in regions)
        section={'status':'pass' if abs(area-expectation['area'])<=expectation['areaTolerance'] and loops==expectation['loops'] else 'fail',
                 'area':area,'loops':loops,'expected':expectation,'regions':regions}
    return {'engine':'view_engine','status':engine['status'],'native':raw,'m2d':engine,
            'agreement':{k:v for k,v in agreement.items() if k not in ('expectedToActual','actualToExpected')},
            'locusMissingSamples':agreement['expectedToActual']['Missed'],'locusExtraSamples':agreement['actualToExpected']['Missed'],
            'disagreementClasses':classes,'sectionOracle':section,'adapterSeconds':time.perf_counter()-started},Path(str(target)+'.svg').read_text(encoding='utf-8')
