"""Freeze measured small-feature evidence and append one honest score row (no Rhino)."""
import argparse
import collections
import gzip
import hashlib
import json
from pathlib import Path
import statistics

ROOT = Path(__file__).resolve().parents[2]


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('results',type=Path)
    p.add_argument('--iteration',required=True)
    p.add_argument('--change',required=True)
    p.add_argument('--disposition',required=True)
    args=p.parse_args()
    dest=ROOT/'hillclimb/small-features';(dest/'evidence').mkdir(parents=True,exist_ok=True)
    results=[json.loads(f.read_text()) for f in sorted(args.results.glob('net*-t*.json'))]
    if not results: raise ValueError('No completed measurements')
    by_runtime=collections.defaultdict(list)
    for result in results: by_runtime[result['Runtime']].append(result)
    summary=[]
    for runtime,measurements in sorted(by_runtime.items()):
        rows=[r for m in measurements for r in m['Rows']]
        scored=[r for r in rows if r['ToleranceMm']<.1]
        engines={}
        for engine in sorted({r['Engine'] for r in rows}):
            subset=[r for r in scored if r['Engine']==engine]
            timing={}
            for tol in [.01,.001]:
                ratios=[]
                for r in subset:
                    if r['ToleranceMm']!=tol: continue
                    baseline=next((b for b in rows if b['ToleranceMm']==.1 and (b['Engine'],b['Detail'],b['Units'],b['OffsetMm'])==(engine,r['Detail'],r['Units'],r['OffsetMm'])),None)
                    if baseline and baseline['Seconds']>0: ratios.append(r['Seconds']/baseline['Seconds'])
                timing[str(tol)]=dict(pairs=len(ratios),medianFineOverCoarse=statistics.median(ratios) if ratios else None)
            engines[engine]=dict(expected=sum(r['Expected'] for r in subset),correct=sum(r['Correct'] for r in subset),
                                 failedRows=sum(not r['Gate'] for r in subset),unresolved=sum(r['Unresolved'] for r in subset),
                                 invalid=sum(r['Invalid'] for r in subset),timing=timing)
        evidence=dest/'evidence'/f'{args.iteration}-net{runtime.split(".")[0]}.json.gz'
        payload=json.dumps(measurements,sort_keys=True,separators=(',',':')).encode()
        evidence.write_bytes(gzip.compress(payload,mtime=0))
        summary.append(dict(runtime=runtime,rhino=measurements[0]['Rhino'],
                            expected=sum(r['Expected'] for r in scored),correct=sum(r['Correct'] for r in scored),
                            fraction=sum(r['Correct'] for r in scored)/sum(r['Expected'] for r in scored),
                            gate=all(r['Gate'] for r in scored) and all(m['Metamorphic'] for m in measurements if m['Rows'][0]['ToleranceMm']<.1),
                            metamorphic=all(m['Metamorphic'] for m in measurements if m['Rows'][0]['ToleranceMm']<.1),
                            wrongFill=sum(r['WrongFill'] for r in scored),engines=engines,
                            evidence=str(evidence.relative_to(ROOT)),evidenceSha256=sha(evidence)))
    entry=dict(iteration=args.iteration,change=args.change,disposition=args.disposition,
               oracleRevision=2 if any(r['Detail']=='gap-pair-probe' for m in results for r in m['Rows']) else 1,
               fixtureManifestSha256=sha(ROOT/'tests/fixtures/small/manifest.json'),results=summary)
    with (dest/'scores.jsonl').open('a',encoding='utf8') as f: f.write(json.dumps(entry,sort_keys=True)+'\n')
    print(json.dumps(entry,indent=2))


if __name__=='__main__': main()
