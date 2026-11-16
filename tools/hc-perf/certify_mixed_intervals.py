"""Fresh mixed tolerance witnesses: all-pairs planar intervals/points + all curved-body rays.

Raw gate rows stay unchanged. This certifies only the disputed drawing samples,
within model tolerance, never a full mixed view or an unsampled native interval.
"""
import argparse
from collections import Counter
import hashlib
import json
import math
from pathlib import Path
import shutil
import subprocess
import sys
import os

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'tools/models'))
from common import SerialRhino
from owned_job import run

def read(p): return json.loads(p.read_text())
def digest(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def write(p,v): p.write_text(json.dumps(v,indent=2))

def certify(gatefile,resolver,prepare_only=False):
    prefix=str(gatefile)[:-10]; path=lambda s:Path(prefix+s); gate=read(gatefile)
    if not gate['exactContradictions'] or not gate['NativeCalls']:return
    assert gate['nativeError'] is None and gate['kernelError'] is None
    scene=read(path('.planar-scene.json')); failures=read(path('.failures.json'))
    disputed=[]; ids=set()
    for line in path('.exact-witnesses.jsonl').open():
        r=json.loads(line)
        if r['classification'] is None and r['routedClass']=='Visible' and r['check']['Class']==1:
            disputed.append(r)
            ids.update(c['edge'] for c in r['check']['Candidates'] if 'edge' in c)
    assert len(disputed)==gate['exactContradictions'] and ids,'Unsupported non-planar contradiction'
    ids=sorted(ids); edges=[scene['Edges'][i] for i in ids]
    data=dict(Faces=scene['Faces'],Edges=edges,Actual=[],Tolerance=gate['tolerance'],Spacing=gate['spacing'])
    inputfile=path('.mixed-reference-input.json'); referencefile=path('.mixed-reference.json')
    exe=ROOT/'tools/routing-audit/bin/Release/net8.0/M2DPlus.RoutingAudit.dll'
    def oracle():
        write(inputfile,data)
        subprocess.run(['dotnet',str(exe),str(inputfile),str(referencefile)],check=False)
        result=read(referencefile)
        assert result['inputSha256'].lower()==digest(inputfile) and result['coreSha256'].lower()==gate['coreSha256'].lower()
        assert result['FaceTests']==result['completePairs']
        return result
    reference=oracle(); proofs=[]; points=[]; world=[]; native_points=[]
    x,y=scene['x'],scene['y']
    z=[x[1]*y[2]-x[2]*y[1],x[2]*y[0]-x[0]*y[2],x[0]*y[1]-x[1]*y[0]]
    size=math.sqrt(sum(v*v for v in z));z=[v/size for v in z]
    for r in disputed:
        f=failures[r['sample']];delta=[f[k]-scene['origin'][i] for i,k in enumerate(('X','Y','Z'))]
        xy=[sum(delta[i]*axis[i] for i in range(3)) for axis in (x,y)]
        choices=[]
        for interval in reference['Intervals']:
            if interval['Class']!=0:continue
            i=interval['Edge'];a,b=edges[i]['A'],edges[i]['B']
            dx,dy=b['X']-a['X'],b['Y']-a['Y'];square=dx*dx+dy*dy
            if not square:continue
            # An interior margin avoids a second floating-point round trip onto
            # the exact event while remaining well within drawing tolerance.
            margin=gate['tolerance']/32/math.sqrt(square)
            lo=min((interval['Start']+interval['End'])/2,interval['Start']+margin)
            hi=max((interval['Start']+interval['End'])/2,interval['End']-margin)
            t=max(lo,min(hi,((xy[0]-a['X'])*dx+(xy[1]-a['Y'])*dy)/square))
            distance=math.hypot(a['X']+t*dx-xy[0],a['Y']+t*dy-xy[1])
            if distance<=gate['tolerance']/2:choices.append((distance,i,t))
        if not choices:
            # Native-derived curves need witnesses on their own saved world
            # support, rather than an unrelated extracted planar edge.
            assert not f['KernelOwned'], 'No independently Visible planar interval near disputed sample'
            native_points.append(r['sample']);continue
        distance,i,t=min(choices);a,b=edges[i]['A'],edges[i]['B']
        v=[a[k]+t*(b[k]-a[k]) for k in ('X','Y','Z')]
        p=[scene['origin'][k]+x[k]*v[0]+y[k]*v[1]+z[k]*v[2] for k in range(3)]
        world.append(p);points.append(dict(Edge=i,Parameter=t))
        proofs.append(dict(sample=r['sample'],classification='toleranceArtefact',distance=distance,
                           edge=ids[i],parameter=t,source=edges[i]['Source'],world=p))
    native_suffixes=[]
    if native_points:
        write(path('.native-point-requests.json'),native_points)
        helper=ROOT/'tools/perf-f/bin/Release/net8.0-windows/M2DPlus.PerfF.dll'
        with SerialRhino(wait_seconds=None):
            watch=run(['dotnet',str(helper),str(resolver.resolve()),'native-point-witness',prefix],path('.native-point-run.log'),180,8)
        write(path('.native-point-watch.json'),watch)
        assert watch['status']=='exited' and watch['exitCode']==0
        shutil.copyfile(helper,path('.native-point-helper.dll'))
        shutil.copyfile(ROOT/'tools/perf-f/NativePointWitnesses.cs',path('.native-point-source.cs'))
        candidates=read(path('.native-point-candidates.json'))
        for sample in native_points:
            choices=[c for c in candidates if c['sample']==sample and c['check']['Class']==0 and c['distance']<=gate['tolerance']/2]
            assert choices, 'No independently Visible point on the actual native-derived output curve'
            candidate=min(choices,key=lambda c:c['distance']);local=candidate['local'];i=len(edges)
            # A degenerate edge is only an exact point-query carrier. It creates
            # no interval or geometry and contributes no interval face pairs.
            edges.append(dict(A=local,B=local,Source=candidate['source']))
            points.append(dict(Edge=i,Parameter=0));world.append(candidate['world'])
            proofs.append(dict(sample=sample,classification='toleranceArtefact',distance=candidate['distance'],
                nativeOutputPiece=candidate['piece'],nativeParameter=candidate['parameter'],source=candidate['source'],world=candidate['world']))
        native_suffixes=['.native-point-requests.json','.native-point-candidates.json','.native-point-watch.json','.native-point-helper.dll','.native-point-source.cs']
    data['Points']=points;reference=oracle()
    assert all(c['check']['Class']==0 for c in reference['checks'])
    rays=path('.mixed-ray-points.json');write(rays,world)
    write(path('.mixed-planar-proofs.json'),dict(proofs=proofs,checks=reference['checks'],curvedDecision='not checked'))
    if prepare_only:
        print(Path(prefix).name,len(proofs),'planar point witnesses prepared; curved checks pending',flush=True)
        return
    folder=path('.mixed-trace');folder.mkdir(exist_ok=True)
    old=os.environ.get('PERFF_RAY_POINTS');old_timeout=os.environ.get('PERFF_TIMEOUT')
    try:
        os.environ['PERFF_RAY_POINTS']=str(rays.resolve());os.environ['PERFF_TIMEOUT']='120'
        command=['dotnet',str(ROOT/'tools/perf-f/bin/Release/net8.0-windows/M2DPlus.PerfF.dll'),str(resolver.resolve()),gate['model'],str(folder.resolve()),gate['view'],'trace-native']
        with SerialRhino(wait_seconds=None):watch=run(command,folder/'run.log',180,8)
    finally:
        for key,value in [('PERFF_RAY_POINTS',old),('PERFF_TIMEOUT',old_timeout)]:
            if value is None:os.environ.pop(key,None)
            else:os.environ[key]=value
    write(path('.mixed-trace-watch.json'),watch)
    assert watch['status']=='exited' and watch['exitCode']==0
    stem=Path(gate['model']).stem+'-'+gate['view']
    captured=read(folder/(stem+'-planar-scene.json'))
    assert all(captured[k]==scene[k] for k in ('Faces','Edges','origin','x','y')),'Different captured planar scene'
    native=read(folder/(stem+'-native-inputs.json'))
    assert native==read(path('.routed-order.json')),'Different native inputs'
    timings=read(folder/(stem+'-timings.json'))
    assert timings['modelSha256'].lower()==gate['modelSha256'].lower() and timings['engineSha256'].lower()==gate['engineSha256'].lower()
    assert timings['rows'][0]['failure'] is None and timings['rows'][0]['summary']['Error'] is None
    evidence=read(folder/(stem+'-ray-evidence.json'))
    sources={s['Identity']:s for s in read(path('.sources.json'))}
    wanted=Counter((i['source'],i['geometry']) for i in native if not i['KernelCandidate'] and sources[i['source']]['type'] in ('Brep','Mesh','Surface','Extrusion'))
    actual=Counter((b['Source'],b['Fingerprint']) for b in evidence['bodies'])
    assert actual==wanted and evidence['bodyRayPairs']==sum(wanted.values())*5*len(proofs)
    assert len(evidence['results'])==len(proofs)
    for i,(proof,check,result) in enumerate(zip(proofs,reference['checks'],evidence['results'])):
        assert result['world']==world[i] and result['clear'] and all(e['success'] and e['clear'] for e in result['evidence'])
        proof.update(check=check['check'],curvedDecision='Visible',curvedRayProof=i)
    shutil.copyfile(folder/(stem+'-foreign-records.json'),path('.foreign-records.json'))
    shutil.copyfile(folder/(stem+'-ray-evidence.json'),path('.mixed-curved-rays.json'))
    capture=dict(timings=timings,inputs=native,bodies=evidence['bodies'],planarSceneSha256=digest(folder/(stem+'-planar-scene.json')))
    write(path('.mixed-capture.json'),capture)
    suffixes=('.gate.json','.failures.json','.planar-scene.json','.sources.json','.routed-order.json','.kernel-output.json','.native-output.json','.exact-witnesses.jsonl','.mixed-reference-input.json','.mixed-reference.json','.mixed-ray-points.json','.mixed-curved-rays.json','.mixed-capture.json','.mixed-trace-watch.json','.foreign-records.json',*native_suffixes)
    write(path('.interval-witnesses.json'),dict(accepted=True,scope='disputed planar intervals or points on actual native-output curves; complete curved-body point rays; no full-view certificate',
        FaceTests=reference['FaceTests'],completePairs=reference['completePairs'],coreSha256=gate['coreSha256'],generatorSha256=digest(Path(__file__)),proofs=proofs,inputs={s:digest(path(s)) for s in suffixes}))
    print(Path(prefix).name,len(proofs),'mixed interval / complete curved-ray witnesses',flush=True)

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('directory',type=Path);parser.add_argument('--resolver',type=Path,required=True);parser.add_argument('--prepare-only',action='store_true')
    args=parser.parse_args()
    for file in sorted(args.directory.glob('*.gate.json')):certify(file,args.resolver,args.prepare_only)
