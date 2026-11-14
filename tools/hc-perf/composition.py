"""Whole-output mixed routing gate, using A2 owned serial Rhino processes and physical-unit sampling."""
from pathlib import Path
import argparse
import json
import sys
from run import DEFAULT_MODELS
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'tools/models'))
from common import write_json, digest
from owned_job import run

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--select',default='')
    p.add_argument('--resume',action='store_true',help='Reuse completed rows only for an identical compiled engine and model')
    p.add_argument('--output',type=Path,default=ROOT/'artifacts/hc-perf/composition')
    a=p.parse_args();a.output.mkdir(parents=True,exist_ok=True)
    cases=[]
    for model in sorted((ROOT/'tests/fixtures').glob('*.3dm')):
        for view in ('Top','North','SW'):cases.append((model,view))
    for model in sorted((ROOT/'tests/fixtures/features').glob('*.3dm')):
        for view in ('Top','North','SW'):cases.append((model,view))
    for tier in ('M','L'):
        for form in ('blocks','exploded'):
            for view in ('North','SW','Top','Long-section'):cases.append((DEFAULT_MODELS/f'perf-{tier}-{form}.3dm',view))
    for form in ('blocks','exploded'):
        for view in ('North','SW','Top','Long-section'):cases.append((Path('C:/Users/tomosandego/.t3/worktrees/m2d-plus/t3-d6bdd8e8-f1e6b3c68a0e104e/artifacts/models')/f'curved-perf-S-{form}.3dm',view))
    for model in sorted((ROOT/'tests/fixtures/sections').glob('*.3dm')):
        for view in ('Plan-section','Long-section'):cases.append((model,view))
    selected=[]
    for model,view in cases:
        stem=model.stem+'__'+view
        if a.select and a.select not in stem:continue
        if not model.exists():raise FileNotFoundError(model)
        prefix=a.output/stem
        selected.append({'path':str(model),'view':view,'output':str(prefix)})
    write_json(a.output/'manifest.json',selected)
    # One serial owned worker per model, amortising startup across its views.
    from itertools import groupby
    for model, group in groupby(selected,key=lambda c:c['path']):
        requests=[]
        for case in group:
            previous=Path(case['output']+'.gate.json')
            if a.resume and previous.exists():
                r=json.loads(previous.read_text())
                engine=ROOT/'tools/hc-perf/bin/Release/net8.0-windows/M2DPlus.Rhino.dll'
                if r.get('modelSha256','').lower()==digest(case['path']) and r.get('engineSha256','').lower()==digest(engine):
                    print('reuse completed',case['output'],flush=True);continue
            requests.append(case)
        if not requests:continue
        batch=a.output/'request.json';write_json(batch,requests)
        command=[sys.executable,'-B','-u',str(ROOT/'tools/hc-perf/run.py'),'--worker','--composition-batch',str(batch)]
        print(f'{Path(model).stem}: '+', '.join(c['view'] for c in requests),flush=True)
        watch=run(command,requests[0]['output']+'.batch.log',1800,16)
        for case in requests:write_json(case['output']+'.watch.json',watch)
        if watch['status']!='exited': print('FAILED',watch,flush=True)
    rows=[json.loads(Path(c['output']+'.gate.json').read_text()) if Path(c['output']+'.gate.json').exists() else {'passed':False,'disagreements':0,'model':c['path'],'view':c['view'],'executionFailure':True} for c in selected]
    write_json(a.output/'gate.json',{'passed':all(r['passed'] for r in rows),'cases':len(rows),'disagreements':sum(r['disagreements'] for r in rows),'rows':rows})
if __name__=='__main__':main()
