"""Serial, owned native correctness cache; intentionally no suite timing mutex."""
from pathlib import Path
import argparse
import json
import subprocess
import sys

ROOT=Path(__file__).resolve().parents[2]
from run import DEFAULT_MODELS
sys.path.insert(0,str(ROOT/'tools/models'))
from common import write_json, digest
from owned_job import run


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--models',type=Path,default=DEFAULT_MODELS)
    p.add_argument('--output',type=Path,default=ROOT/'artifacts/hc-perf/native-a2')
    p.add_argument('--select',default='')
    p.add_argument('--all',action='store_true',help='Also retain the original supplemental fixture/feature native gates')
    p.add_argument('--bench-bin',type=Path,default=ROOT/'artifacts/hc-perf/slot-bin/M2DPlus.HcPerf.dll')
    a=p.parse_args();a.output.mkdir(parents=True,exist_ok=True)
    models=[a.models/f'perf-{tier}-{form}.3dm' for tier in ('M','L') for form in ('blocks','exploded')]
    if a.all:
        models+=sorted((ROOT/'tests/fixtures').glob('*.3dm'))
        models+=[m for m in sorted(a.models.glob('*.3dm')) if not m.name.startswith('perf-')]
    for model in models:
        requests=[]
        views=('SW','North','Top') if model.name.startswith('perf-') else ('Top','Front','SW')
        for view in views:
            stem=f'{model.stem}__{view}'
            if a.select not in stem:continue
            target=a.output/stem
            meta=Path(str(target)+'.native-meta.json')
            if meta.exists():
                record=json.loads(meta.read_text())
                if record['modelSha256'].lower()!=digest(model) or record['view']!=view:
                    raise RuntimeError(f'{stem}: stale native cache model/view')
                for suffix,key in [('.native.json','nativeSha256'),('.slots.json','slotsSha256'),('.scene.json','sceneSha256')]:
                    if record[key].lower()!=digest(str(target)+suffix):
                        raise RuntimeError(f'{stem}: stale native cache {suffix}')
                print('cached',stem,flush=True);continue
            requests.append({'path':str(model),'view':view,'output':str(target)})
        if not requests:continue
        batch=a.output/(model.stem+'.requests.json');write_json(batch,requests)
        command=[sys.executable,'-B','-u',str(ROOT/'tools/hc-perf/run.py'),'--worker','--native-batch',str(batch),'--bench-bin',str(a.bench_bin)]
        print('native correctness',model.stem,flush=True)
        watch=run(command,str(batch)+'.log',1800,16)
        write_json(str(batch)+'.watch.json',watch)
        if watch['status']!='exited' or not all(Path(r['output']+'.native-meta.json').exists() for r in requests):
            raise RuntimeError(f'{model.stem}: {watch["status"]}')


if __name__=='__main__':main()
