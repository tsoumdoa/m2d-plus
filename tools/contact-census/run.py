"""Replay before/after planar contact classifications from fresh composition-gate scenes."""
import argparse
import json
from pathlib import Path
import subprocess
import hashlib

ROOT=Path(__file__).resolve().parents[2]
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('gate',type=Path)
p.add_argument('--output',type=Path,required=True)
p.add_argument('--select',default='perf-M,facade')
p.add_argument('--repeats',type=int,choices=(1,2,3),default=1,help='Count once by default; repeat.py measures whole-output flicker separately')
p.add_argument('--resume',action='store_true')
a=p.parse_args();a.output.mkdir(parents=True,exist_ok=True)
rows=[]
executable=ROOT/'tools/contact-census/bin/Release/net8.0/M2DPlus.ContactCensus.dll'
census_hash=hashlib.sha256(executable.read_bytes()).hexdigest()
for file in sorted(a.gate.glob('*.gate.json')):
    if not any(s in file.stem for s in a.select.split(',')):continue
    gate=json.loads(file.read_text());prefix=str(file)[:-10]
    scene=Path(prefix+'.planar-scene.json')
    if not scene.exists():
        rows.append(dict(case=Path(prefix).name,model=gate['model'],view=gate['view'],scope='No planar extraction; native contacts are not certified'))
        continue
    output=a.output/(Path(prefix).name+'.json')
    prior=json.loads(output.read_text()) if a.resume and output.exists() else None
    reusable=prior and prior.get('metricVersion')==2 and prior.get('censusSha256','').lower()==census_hash and prior['coreSha256'].lower()==gate['coreSha256'].lower() and prior['inputSha256'].lower()==hashlib.sha256(scene.read_bytes()).hexdigest()
    if not reusable:subprocess.run(['dotnet',str(executable),str(scene.resolve()),str(gate['tolerance']),str(output.resolve()),str(a.repeats)],check=True)
    row=json.loads(output.read_text());row.update(case=Path(prefix).name,model=gate['model'],view=gate['view'])
    if row['coreSha256'].lower()!=gate['coreSha256'].lower():raise RuntimeError('Producer mismatch: '+str(file))
    rows.append(row)
    (a.output/'summary.json').write_text(json.dumps(rows,indent=2))
(a.output/'summary.json').write_text(json.dumps(rows,indent=2))
