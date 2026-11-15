"""Record one completed candidate decision and preserve its exact source snapshot."""
from pathlib import Path
import argparse
from collections import Counter
import hashlib
import json
import math
import re
import shutil

ROOT=Path(__file__).resolve().parents[2]


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('iteration',type=int);a=p.parse_args()
    directory=ROOT/f'artifacts/hc-perf/iteration-{a.iteration}'
    path=directory/'score.json';score=json.loads(path.read_text())
    log=ROOT/'hillclimb/scores.jsonl';records=[json.loads(s) for s in log.read_text().splitlines()]
    previous=[r for r in records if r['iteration']!=a.iteration and r['gate']['passed'] and (r['iteration']==3 or r.get('kept'))]
    best=min(previous,key=lambda r:r['primary'])
    kept=score['gate']['passed'] and score['primary'] is not None and score['primary']<best['primary']
    score.update(kept=kept,comparisonIteration=best['iteration'],primaryImprovementVsBest=best['primary']/score['primary'] if score['primary'] else None)
    snapshot=ROOT/f'artifacts/hc-perf/snapshots/iteration-{a.iteration}';snapshot.mkdir(parents=True,exist_ok=True)
    sources={}
    for source in (ROOT/'src/M2DPlus.Core/Visibility').glob('*.cs'):
        shutil.copyfile(source,snapshot/source.name);sources[source.name]=hashlib.sha256(source.read_bytes()).hexdigest()
    score['sourceSha256']=sources
    counts=Counter();tier_times={}
    for row_path in directory.glob('*.json'):
        if row_path.name.count('.')!=1 or '__' not in row_path.stem:continue
        row=json.loads(row_path.read_text())
        counts.update(row.get('nativeValidation',{}).get('counts',{}))
        if row_path.stem.startswith('perf-') and not row_path.stem.startswith('perf-XS-') and 'seconds' in row:
            tier_times.setdefault(row_path.stem.split('-')[1],[]).append(row['seconds'])
    score['secondary']['nativeDisagreementCounts']=dict(counts)
    score['secondary']['tierGeomeanSeconds']={k:math.exp(sum(map(math.log,v))/len(v)) for k,v in tier_times.items()}
    path.write_text(json.dumps(score,indent=2,allow_nan=False)+'\n',encoding='utf-8')
    matches=[i for i,r in enumerate(records) if r['iteration']==a.iteration]
    if len(matches)!=1:raise RuntimeError('Expected exactly one completed score record')
    records[matches[0]]=score
    log.write_text(''.join(json.dumps(r,allow_nan=False)+'\n' for r in records),encoding='utf-8')
    primary=f"{score['primary']:.9f}" if score['primary'] else 'Invalid'
    perf=score['secondary'].get('perfTierGeomeanSeconds')
    perf_text=f'{perf:.6f}' if perf else 'Invalid'
    row=f"| {a.iteration} | {score['summary']} | {'Pass' if score['gate']['passed'] else 'Fail'} | {primary} | {perf_text} | {'Yes' if kept else 'No'} |"
    hill=ROOT/'HILLCLIMB.md';text=hill.read_text(encoding='utf-8')
    pattern=rf'^\| {a.iteration} \|.*$'
    text=re.sub(pattern,lambda _:row,text,flags=re.M) if re.search(pattern,text,re.M) else text+'\n'+row+'\n'
    hill.write_text(text,encoding='utf-8')
    print(json.dumps({'iteration':a.iteration,'kept':kept,'gate':score['gate'],'primary':score['primary'],
                      'perf':perf,'improvement':score['primaryImprovementVsBest'],'counts':dict(counts)},indent=2))


if __name__=='__main__':main()
