"""Finish the owner-requested M budget after feature/S attempts settle. Local only."""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import time
from common import ROOT,write_json
from view_report import prefix


def main():
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('--tier',choices=('S','M'),default='M'); args=parser.parse_args()
    cap=600 if args.tier=='S' else 120
    models=ROOT/'artifacts/view-models'; out=ROOT/'artifacts/baseline/view-cases'
    def owner_stopped(): return (out/'owner-stop.json').exists()
    if owner_stopped(): return
    metas=[json.loads(p.read_text()) for p in sorted(models.glob('*.json'))]
    def settled(m,v):
        path=Path(str(prefix(out,m['id'],v['name']))+'.json')
        watch=Path(str(prefix(out,m['id'],v['name']))+'.watch.json')
        if not path.exists() or not watch.exists(): return False
        r=json.loads(path.read_text())
        return r.get('fixtureSha256')==m['sha256'] and (r.get('phase')=='complete' or r.get('status') in ('exception','wall_timeout','private_memory_limit','worker_crash'))
    priority=[(m,v) for m in metas if m['tier']!='M' and (args.tier=='M' or m['id']!='view-curved-perf-S-blocks') for v in m['views']]
    while True:
        if owner_stopped(): return
        pending=[(m['id'],v['name']) for m,v in priority if not settled(m,v)]
        write_json(out/f'remaining-{args.tier}-admission.json',{'status':'waiting for priority views' if pending else 'ready','priorityPending':len(pending),'capSeconds':cap})
        if not pending: break
        time.sleep(15)
    # Only this task's handoff gate, never another worktree's coordination.
    control=out/'coordination-control.json'
    if control.exists():
        state=json.loads(control.read_text()); state['pauseBeforeModels']=[m for m in state.get('pauseBeforeModels',[]) if m!=f'view-curved-perf-{args.tier}-blocks']
        write_json(control,state)
    targets=[m for m in metas if (m['tier']=='M' if args.tier=='M' else m['id']=='view-curved-perf-S-blocks')]
    for m in targets:
        if owner_stopped(): return
        views=[v['name'] for v in m['views'] if not settled(m,v)]
        if not views: continue
        print(args.tier,'remaining',m['id'],len(views),'views,',cap,'seconds each',flush=True)
        subprocess.run([sys.executable,'-B','-u',str(ROOT/'tools/models/run.py'),'--models',str(models),'--out',str(out),
            '--select',m['id'],'--views',','.join(views),'--engine','view_engine','--threads','off','--wall',str(cap),'--memory','8','--yield-seconds','5'],check=True)
    write_json(out/f'remaining-{args.tier}-admission.json',{'status':'finished','capSeconds':cap})


if __name__=='__main__': main()
