"""Certify the unchanged climb-A measurements using complete A2 native evidence."""
from pathlib import Path
import argparse
from collections import Counter
import json
import math
import subprocess
import sys
import time

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'tools/models'))
from common import digest, write_json


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--source',type=Path,required=True)
    p.add_argument('--native',type=Path,default=ROOT/'artifacts/hc-perf/native-a2')
    a=p.parse_args()
    subprocess.run(['git','diff','--exit-code','HEAD','--','src/M2DPlus.Core/Visibility','src/M2DPlus.Rhino/Services/PlanarExtraction.cs'],check=True,capture_output=True)
    manifest=json.loads((a.source/'manifest.json').read_text())
    score=json.loads((a.source/'score.json').read_text())
    if len(manifest)!=183 or any(not f.endswith('native gate unavailable: licence-free run') for f in score['gate']['failures']):
        raise RuntimeError('Original full gate contains a non-native failure or manifest changed')
    counts=Counter();slots=Counter();cases=[];tier_times={};ratios={}
    baseline=Path(manifest[0]['path']).parent.parent/'baseline'
    for case in manifest:
        stem=Path(case['path']).stem+'__'+case['view'];prefix=a.native/stem
        row=json.loads((a.source/(stem+'.json')).read_text())
        if case['primary'] and Path(case['path']).stem.startswith('perf-'):
            tier=Path(case['path']).stem.split('-')[1];tier_times.setdefault(tier,[]).append(row['seconds'])
            view={'North':'North-elevation','SW':'SW-axo'}.get(case['view'])
            ref=baseline/(Path(case['path']).stem+'__'+str(view)+'__off__native.json')
            if view and ref.exists():ratios[Path(case['path']).stem+'/'+case['view']]=json.loads(ref.read_text())['computeSeconds']/row['seconds']
        if not case['native']:continue
        meta=json.loads(Path(str(prefix)+'.native-meta.json').read_text())
        audit=json.loads(Path(str(prefix)+'.audit.json').read_text())
        oracle=json.loads(Path(str(prefix)+'.native-oracle.json').read_text())
        comparison=json.loads(Path(str(prefix)+'.canonical-comparison.json').read_text())
        provenance=json.loads(Path(str(prefix)+'.comparison-provenance.json').read_text())
        if not audit['passed'] or not oracle['passed']:raise RuntimeError(stem+': failed gate')
        if meta['modelSha256']!=row['modelSha256'] or meta['priority']!='Normal' or meta['multipleThreads']:
            raise RuntimeError(stem+': native model/priority/threading mismatch')
        scene=json.loads(Path(str(prefix)+'.scene.json').read_text())
        if len(scene['Edges'])!=row['edges'] or len(scene['Faces'])!=row['faces'] or sorted(meta['excluded'])!=sorted(row['excluded']):
            raise RuntimeError(stem+': native and measured kernel planar subsets differ')
        for suffix,key in [('.native.json','nativeSha256'),('.slots.json','slotsSha256'),('.scene.json','sceneSha256')]:
            if digest(str(prefix)+suffix)!=meta[key].lower():raise RuntimeError(stem+': native cache changed')
        if digest(a.source/(stem+'.loci.json'))!=digest(str(prefix)+'.loci.json'):
            raise RuntimeError(stem+': measured and validated kernel loci differ')
        if provenance['lociSha256']!=digest(str(prefix)+'.loci.json') or provenance['oracleSha256']!=digest(str(prefix)+'.native-oracle.json'):
            raise RuntimeError(stem+': validation provenance changed')
        if sum(audit['counts'].values())!=comparison['missing']['Missed']+comparison['extra']['Missed']:
            raise RuntimeError(stem+': incomplete disagreement accounting')
        counts.update(audit['counts'])
        records=json.loads(Path(str(prefix)+'.slots.json').read_text());slots.update(s['visibility'] for s in records)
        cases.append({'case':stem,'counts':audit['counts'],'nativeSha256':meta['nativeSha256'],
                      'auditSha256':digest(str(prefix)+'.audit.json'),'oracleSha256':digest(str(prefix)+'.native-oracle.json')})
    if len(cases)!=165:raise RuntimeError(f'Native gate incomplete: {len(cases)}/165')
    report={'passed':True,'kernelBugs':0,'unexplained':0,'counts':dict(counts),'nativeSlots':dict(slots),'cases':cases,
            'requiredMLCases':12,'supplementalCases':153,'sourceScore':str(a.source/'score.json'),
            'sourceScoreSha256':digest(a.source/'score.json'),'measuredUtc':score['utc'],
            'scope':'Same planar-only sources on both sides; native visible/hidden drawing loci with documented visible precedence. Every failed canonical sample has a retained witness and disputed-support exact reference.'}
    write_json(a.native/'gate.json',report)
    score['iteration']=3;score['summary']='A2 retrospective certification of unchanged climb-A full measurements; complete native correctness evidence, no native timing'
    score['gate']={'passed':True,'failures':[]};score['validatedAgainst']=str(a.native/'gate.json')
    score['measuredUtc']=score['utc'];score['utc']=time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime())
    score['secondary']['nativeRatios']=ratios
    score['secondary']['nativeRatioScope']='Archived mixed-model native compute / measured planar kernel total; contextual, no same-subset timing claim; no Top/XL baseline exists'
    score['secondary']['tierGeomeanSeconds']={k:math.exp(sum(map(math.log,v))/len(v)) for k,v in tier_times.items()}
    score['secondary']['nativeDisagreementCounts']=dict(counts)
    dest=ROOT/'artifacts/hc-perf/validated-baseline';write_json(dest/'score.json',score)
    log=ROOT/'hillclimb/scores.jsonl'
    if any(json.loads(line)['iteration']==3 for line in log.read_text().splitlines()):raise RuntimeError('Iteration 3 already recorded')
    with log.open('a',encoding='utf-8') as f:f.write(json.dumps(score,allow_nan=False)+'\n')
    print(json.dumps({'gate':True,'counts':dict(counts),'primary':score['primary'],'secondary':score['secondary']},indent=2))


if __name__=='__main__':main()
