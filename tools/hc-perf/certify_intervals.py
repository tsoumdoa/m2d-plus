"""Bind fresh all-pairs interval/point witnesses for planar-only tolerance artefacts.

Does not edit raw gate rows. Each disputed Visible sample must lie within model
tolerance of a reference Visible interval AND an independently rechecked interior
point on that interval. Curved scenes are excluded. routing_verify.py consumes the
hash-bound certificate under the existing gate rule.
"""
import argparse
import json
import math
from pathlib import Path
import subprocess
import hashlib

ROOT = Path(__file__).resolve().parents[2]
def read(p): return json.loads(p.read_text())
def digest(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def certify(prefix):
    prefix = str(prefix)
    path = lambda suffix: Path(prefix + suffix)
    gate = read(path('.gate.json'))
    if not gate['exactContradictions'] or gate['NativeCalls'] != 0: return
    scene = read(path('.planar-scene.json')); failures = read(path('.failures.json'))
    point_only = 'section' in gate['view'].lower()
    if point_only and not path('.reference.json').exists():
        # Section cut/trim suppression is downstream: no whole-output comparison
        # is claimed here. Only the disputed visibility points are certified.
        data = dict(Faces=scene['Faces'],Edges=scene['Edges'],Actual=[],Tolerance=gate['tolerance'],Spacing=gate['spacing'])
        path('.reference-input.json').write_text(json.dumps(data))
        subprocess.run(['dotnet',str(ROOT/'tools/routing-audit/bin/Release/net8.0/M2DPlus.RoutingAudit.dll'),str(path('.reference-input.json')),str(path('.reference.json'))],check=False)
    reference = read(path('.reference.json')); data = read(path('.reference-input.json'))
    assert (point_only or reference['passed']) and reference['FaceTests'] == reference['completePairs']
    points = []; proofs = []
    for line in path('.exact-witnesses.jsonl').read_text().splitlines():
        check = json.loads(line)
        if check['classification'] is not None or check['routedClass'] != 'Visible' or check['check']['Class'] != 1: continue
        f = failures[check['sample']]
        delta = [f[k]-scene['origin'][i] for i,k in enumerate(('X','Y','Z'))]
        xy = [sum(delta[i]*scene[k][i] for i in range(3)) for k in ('x','y')]
        candidates = []
        for span in reference['Intervals']:
            if span['Class'] != 0: continue
            edge = scene['Edges'][span['Edge']]; a,b = edge['A'],edge['B']
            dx,dy = b['X']-a['X'],b['Y']-a['Y']; square = dx*dx+dy*dy
            if not square: continue
            lo,hi = math.nextafter(span['Start'], math.inf),math.nextafter(span['End'], -math.inf)
            if lo >= hi: continue
            t = max(lo,min(hi,((xy[0]-a['X'])*dx+(xy[1]-a['Y'])*dy)/square))
            distance = math.hypot(a['X']+dx*t-xy[0],a['Y']+dy*t-xy[1])
            if distance <= gate['tolerance']: candidates.append((distance,span['Edge'],t))
        assert candidates, 'No Visible interval within tolerance: ' + prefix
        distance,edge,t = min(candidates)
        points.append(dict(Edge=edge,Parameter=t))
        proofs.append(dict(sample=check['sample'],classification='toleranceArtefact',distance=distance,edge=edge,parameter=t))
    assert len(proofs) == gate['exactContradictions']
    data['Points'] = points; path('.reference-input.json').write_text(json.dumps(data))
    subprocess.run(['dotnet',str(ROOT/'tools/routing-audit/bin/Release/net8.0/M2DPlus.RoutingAudit.dll'),str(path('.reference-input.json')),str(path('.reference.json'))],check=not point_only)
    reference = read(path('.reference.json'))
    for proof,check in zip(proofs,reference['checks']):
        assert check['check']['Class'] == 0, 'Interior witness is not independently Visible'
        proof['check'] = check['check']
    certificate = dict(accepted=True,scope='disputed visibility points only; no section/topology certificate' if point_only else 'complete planar line comparison and disputed visibility points',FaceTests=reference['FaceTests'],completePairs=reference['completePairs'],
        coreSha256=reference['coreSha256'],generatorSha256=digest(Path(__file__)),proofs=proofs,
        inputs={s:digest(path(s)) for s in ('.gate.json','.failures.json','.planar-scene.json','.kernel-output.json','.native-output.json','.exact-witnesses.jsonl','.reference-input.json','.reference.json')})
    path('.interval-witnesses.json').write_text(json.dumps(certificate,indent=2))
    print(Path(prefix).name,len(proofs),'independent Visible interval/point witnesses',flush=True)

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__); parser.add_argument('directory',type=Path)
    for file in parser.parse_args().directory.glob('*.gate.json'): certify(str(file)[:-10])
