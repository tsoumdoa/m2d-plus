"""Three-run whole-visibility record comparison against an untouched baseline build.

Uses serial owned Rhino processes, never timing calibration. Record matching uses
the existing geometric fingerprint; class flicker and changing geometry records
are reported separately. This does not certify arbitrary curved coincidence.
"""
import argparse
from collections import Counter,defaultdict
import json
import os
from pathlib import Path
import sys

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'tools/models'))
from common import digest,write_json
from owned_job import run

def measure(dumps):
    records=[];counts=[];duplicates=[];zero=[]
    for path in dumps:
        data=json.loads(path.read_text())
        if data['error']:raise RuntimeError(data['error'])
        groups=defaultdict(set);supports=Counter()
        for piece in data['pieces']:
            if piece['length']<=0:continue
            key=piece['key']
            groups[key].add(piece['c']);supports[key]+=1
        records.append(groups);counts.append(dict(Counter(p['c'] for p in data['pieces'])))
        duplicates.append(sum(n-1 for n in supports.values()))
        zero.append(sum(p['length']<=0 for p in data['pieces']))
    common=set.intersection(*(set(r) for r in records));union=set.union(*(set(r) for r in records))
    return dict(metricVersion=2,classes=counts,duplicateGeometryRecords=duplicates,zeroLengthRecords=zero,
        matchedGeometryRecords=len(common),classFlickerRecords=sum(len({tuple(sorted(r[k])) for r in records})>1 for k in common),
        changingGeometryRecords=len(union-common),dumps=[dict(path=str(p),sha256=digest(p)) for p in dumps])

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--resolver',type=Path,required=True)
    p.add_argument('--before',type=Path,required=True,help='Untouched baseline PerfF DLL')
    p.add_argument('--after',type=Path,default=ROOT/'tools/perf-f/bin/Release/net8.0-windows/M2DPlus.PerfF.dll')
    p.add_argument('--output',type=Path,required=True)
    p.add_argument('--resume',action='store_true')
    a=p.parse_args();a.output.mkdir(parents=True,exist_ok=True)
    models=Path(os.environ.get('M2D_MODELS') or Path.home()/'.t3/m2d-plus-models')
    paths=[models/'buildings'/f'perf-M-{form}.3dm' for form in ('blocks','exploded')]
    paths += [models/'curved/curved-facade.3dm',models/'views/view-curved-facade.3dm']
    rows=[]
    for model in paths:
        for view in ('Top','North','SW'):
            key=model.stem+'__'+view;row=dict(case=key,model=str(model),modelSha256=digest(model),view=view)
            for label,assembly in [('before',a.before),('after',a.after)]:
                assembly=assembly.resolve();folder=(a.output/key/label).resolve();folder.mkdir(parents=True,exist_ok=True)
                receipt=folder/'receipt.json';engine=digest(assembly.parent/'M2DPlus.Rhino.dll');harness=digest(assembly)
                if a.resume and receipt.exists():
                    prior=json.loads(receipt.read_text())
                    if prior['engineSha256']==engine and prior['harnessSha256']==harness and prior['modelSha256']==row['modelSha256'] and prior['watch']['status']=='exited':
                        prior.update(measure(sorted(folder.glob('*-repeat.json'))));write_json(receipt,prior)
                        row[label]=prior;continue
                print(key,label,flush=True)
                watch=run(['dotnet',str(assembly),str(a.resolver.resolve()),str(model),str(folder),view,'repeat','repeat','repeat'],folder/'run.log',1800,16)
                if watch['status']!='exited' or watch['exitCode']!=0:raise RuntimeError(watch)
                dumps=sorted(folder.glob('*-repeat.json'))
                if len(dumps)!=3:raise RuntimeError('Missing repeats: '+str(folder))
                row[label]=dict(engineSha256=engine,harnessSha256=harness,modelSha256=row['modelSha256'],watch=watch,**measure(dumps))
                write_json(receipt,row[label])
            rows.append(row);write_json(a.output/'summary.json',rows)
    write_json(a.output/'summary.json',rows)

if __name__=='__main__':main()
