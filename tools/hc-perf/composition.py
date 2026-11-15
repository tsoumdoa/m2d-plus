"""Whole-output mixed routing gate, using A2 owned serial Rhino processes and physical-unit sampling."""
from pathlib import Path
import argparse
import json
import sys
import os
from run import DEFAULT_MODELS, MODELS_ROOT
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'tools/models'))
from common import write_json, digest
from owned_job import run

def completed_watch(case):
    path=Path(case['output']+'.watch.json')
    if not path.exists():return False
    watch=json.loads(path.read_text())
    if watch.get('status')=='exited':return True
    gate=Path(case['output']+'.gate.json')
    return watch.get('status')=='case-completed-before-sibling-timeout' and watch.get('parentStatus')=='wall_timeout' and gate.exists() and watch.get('gateSha256')==digest(gate)
def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--select',default='')
    p.add_argument('--required-only',action='store_true',help='Owner branch gate: small fixtures and eight M views; exclude L and curved-S supplements')
    p.add_argument('--fresh-native',action='store_true')
    p.add_argument('--fresh-native-select',default='',help='Re-time selected views back to back; other native geometry may use the cache')
    p.add_argument('--publication',action='store_true')
    p.add_argument('--resolver',type=Path,default=Path(os.environ['M2D_RHINO_INSIDE']) if os.environ.get('M2D_RHINO_INSIDE') else None,help='Compatible Rhino.Inside resolver DLL; defaults to M2D_RHINO_INSIDE')
    p.add_argument('--trace-native',action='store_true')
    p.add_argument('--resume',action='store_true',help='Reuse completed rows only for an identical compiled engine and model')
    p.add_argument('--watchdog-seconds',type=int,default=1800,help='Owned-model wall budget including independent exact adjudication; memory stays capped at 16 GiB')
    p.add_argument('--output',type=Path,default=ROOT/'artifacts/hc-perf/composition')
    a=p.parse_args();a.output.mkdir(parents=True,exist_ok=True)
    if a.publication and (a.resolver is None or not a.resolver.is_file()):p.error('--publication requires --resolver or M2D_RHINO_INSIDE pointing to an existing compatible resolver DLL')
    if a.watchdog_seconds<=0:p.error('--watchdog-seconds must be positive')
    cases=[]
    for model in sorted((ROOT/'tests/fixtures').glob('*.3dm')):
        for view in ('Top','North','SW'):cases.append((model,view))
    for model in sorted((ROOT/'tests/fixtures/features').glob('*.3dm')):
        for view in ('Top','North','SW'):cases.append((model,view))
    for tier in ('M','L'):
        for form in ('blocks','exploded'):
            for view in ('North','SW','Top','Long-section'):cases.append((DEFAULT_MODELS/f'perf-{tier}-{form}.3dm',view))
    for form in ('blocks','exploded'):
        for view in ('North','SW','Top','Long-section'):cases.append(((MODELS_ROOT/'curved')/f'curved-perf-S-{form}.3dm',view))
    for model in sorted((ROOT/'tests/fixtures/sections').glob('*.3dm')):
        for view in ('Plan-section','Long-section'):cases.append((model,view))
    selected=[]
    compiled=ROOT/'tools/hc-perf/bin/Release/net8.0-windows'
    producer={k:digest(compiled/v) for k,v in [('engineSha256','M2DPlus.Rhino.dll'),('coreSha256','M2DPlus.Core.dll'),('harnessSha256','M2DPlus.HcPerf.dll')]}
    for model,view in cases:
        if a.required_only and (model.stem.startswith('perf-L-') or model.stem.startswith('curved-perf-')):continue
        stem=model.stem+'__'+view
        if a.select and not any(s in stem for s in a.select.split(',')):continue
        if not model.exists():raise FileNotFoundError(model)
        prefix=a.output/stem
        selected.append({'path':str(model),'view':view,'output':str(prefix),'freshNative':a.fresh_native or any(s in stem for s in a.fresh_native_select.split(',') if s),'publication':a.publication,'traceNative':a.trace_native})
    write_json(a.output/'manifest.json',selected)
    # One serial owned worker per model, amortising startup across its views.
    from itertools import groupby
    for model, group in groupby(selected,key=lambda c:c['path']):
        requests=[]
        for case in group:
            previous=Path(case['output']+'.gate.json')
            if a.resume and previous.exists():
                r=json.loads(previous.read_text())
                if r.get('modelSha256','').lower()==digest(case['path']) and all(r.get(k,'').lower()==v.lower() for k,v in producer.items()) and completed_watch(case):
                    print('reuse completed',case['output'],flush=True);continue
            requests.append(case)
        if not requests:continue
        batch=a.output/'request.json';write_json(batch,requests)
        command=[sys.executable,'-B','-u',str(ROOT/'tools/hc-perf/run.py'),'--worker','--composition-batch',str(batch)]
        if a.publication:
            command=['dotnet',str(compiled/'M2DPlus.HcPerf.dll'),str(batch),str(a.resolver.resolve())]
        print(f'{Path(model).stem}: '+', '.join(c['view'] for c in requests),flush=True)
        watch=run(command,requests[0]['output']+'.batch.log',a.watchdog_seconds,16)
        for case in requests:write_json(case['output']+'.watch.json',watch)
        if watch['status']!='exited': print('FAILED',watch,flush=True)
    rows=[json.loads(Path(c['output']+'.gate.json').read_text()) if Path(c['output']+'.gate.json').exists() else {'passed':False,'disagreements':0,'model':c['path'],'view':c['view'],'executionFailure':True} for c in selected]
    for case,row in zip(selected,rows):
        watch=Path(case['output']+'.watch.json')
        if not completed_watch(case) or not all(row.get(k,'').lower()==v.lower() for k,v in producer.items()) or row.get('modelSha256','').lower()!=digest(case['path']).lower():
            row['passed']=False;row['producerOrExecutionFailure']=True
    write_json(a.output/'gate.json',{'producer':producer,'completeInventory':not a.select and len(rows)==233,'passed':all(r['passed'] for r in rows),'cases':len(rows),'disagreements':sum(r['disagreements'] for r in rows),'rows':rows})
if __name__=='__main__':main()
