"""Licence-free, deterministic small-detail geometry + analytic truth (rhino3dm 8).

Run: python tools/models/small_features.py
Coordinates/truth are authored in mm; transforms are applied only on archive write.
No Rhino process, tolerance-dependent boolean, meshing or production code is used.
"""
import hashlib
import json
import math
from pathlib import Path
import uuid

import rhino3dm as R

ROOT = Path(__file__).resolve().parents[2]
DEST = ROOT / 'tests/fixtures/small'
NS = uuid.UUID('7bf5bddb-56ee-4b18-9aad-635bd4d697ef')


def polygon(points):
    return {'points': points, 'kind': 'polygon'}


def rounded(w, h, r, x=0, y=0):
    return {'kind': 'rounded', 'w': w, 'h': h, 'r': r, 'x': x, 'y': y}


def curve(p):
    if p['kind'] == 'polygon':
        pts = [R.Point3d(x, y, 0) for x, y in p['points']]
        return R.PolylineCurve(pts + [pts[0]])
    if p['kind'] == 'circle':
        return R.Circle(R.Point3d(p['x'], p['y'], 0), p['r']).ToNurbsCurve()
    w, h, r = p['w'], p['h'], p['r']
    result = R.PolyCurve()
    # CCW, four exact lines and four exact quarter arcs.
    q = math.sqrt(.5)
    pts = [(r, 0), (w-r, 0), (w, r), (w, h-r),
           (w-r, h), (r, h), (0, h-r), (0, r)]
    mids = [(w-r+r*q, r-r*q), (w-r+r*q, h-r+r*q),
            (r-r*q, h-r+r*q), (r-r*q, r-r*q)]
    def point(v): return R.Point3d(v[0]+p['x'], v[1]+p['y'], 0)
    for i in range(4):
        result.Append(R.Line(point(pts[2*i]), point(pts[2*i+1])))
        result.Append(R.Arc(point(pts[2*i+1]), point(mids[i]), point(pts[(2*i+2) % 8])))
    return result


def measures(p):
    if p['kind'] == 'circle':
        r = p['r']; return math.pi*r*r, [2*math.pi*r]
    if p['kind'] == 'rounded':
        w, h, r = p['w'], p['h'], p['r']
        return w*h-(4-math.pi)*r*r, [w-2*r, math.pi*r/2, h-2*r, math.pi*r/2]*2
    pts = p['points']; nxt = pts[1:]+pts[:1]
    return abs(sum(x*v-y*u for (x,y),(u,v) in zip(pts,nxt)))/2, [math.hypot(x-u,y-v) for (x,y),(u,v) in zip(pts,nxt)]


def cases(tol):
    result = []
    def add(name, profiles, depth=.1, z=None, mesh=False, contact=False):
        a = [measures(p) for p in profiles]
        result.append(dict(name=name, profiles=profiles, depth=depth, z=-depth/2 if z is None else z,
                           mesh=mesh, contact=contact, regions=0 if contact else 1,
                           holes=0 if contact else len(profiles)-1,
                           areaMm2=0 if contact else a[0][0]-sum(v[0] for v in a[1:]),
                           edgeLengthsMm=[n for _,ls in a for n in ls],
                           visibleClass='Visible', hiddenClass='Hidden',
                           # Through centre, well clear of fillets/chamfers; visible tails and hole interval.
                           probeY=.5))
    for r in [.1,.05,.01]:
        add(f'plate-fillet-{r}', [rounded(2,1,r)])
        add(f'rhs-fillet-{r}', [rounded(3,2,r),rounded(2.6,1.6,r,.2,.2)], depth=2)
    for c in [.1,.01]:
        add(f'chamfer-{c}', [polygon([(c,0),(2-c,0),(2,c),(2,1-c),(2-c,1),(c,1),(0,1-c),(0,c)])])
    for d in [1,.2]:
        add(f'bolt-hole-{d}', [polygon([(0,0),(2,0),(2,2),(0,2)]),dict(kind='circle',x=1,y=1,r=d/2)])
        result[-1]['probeY'] = 1
    for t in [.1,.02]:
        add(f'thin-plate-{t}', [polygon([(0,0),(2,0),(2,t),(0,t)])],depth=2)
        result[-1]['probeY'] = t/2
    add('gap-left',[polygon([(0,0),(1,0),(1,1),(0,1)])],depth=2)
    add('gap-right',[polygon([(1.01,0),(2.01,0),(2.01,1),(1.01,1)])],depth=2)
    add('facade-step',[polygon([(0,0),(2,0),(2,1),(1,1),(1,1.05),(0,1.05)])],depth=2)
    add('short-edge',[polygon([(0,0),(1,0),(1,.02),(2,.02),(2,1),(0,1)])],depth=2)
    add('coplanar-contact',[polygon([(0,0),(1,0),(1,1),(0,1)])],depth=1,z=-1,contact=True)
    add('near-contact',[polygon([(0,0),(1,0),(1,1),(0,1)])],depth=1,z=-1+tol/2,contact=True)
    add('mesh-thin-plate',[polygon([(0,0),(2,0),(2,.02),(0,.02)])],depth=2,mesh=True)
    result[-1]['probeY'] = .01
    return result


def main():
    DEST.mkdir(parents=True, exist_ok=True)
    manifest = dict(schema=1, generator='tools/models/small_features.py',
                    generatorSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                    truthUnits='mm', offsetAxes='all XYZ; 1 km = 1000000 mm',
                    archiveDeterminism='Geometry, names, UUIDs and truth deterministic; archive timestamps may differ.', models=[])
    for tol in [.1,.01,.001]:
        for units, factor, unit in [('mm',1,R.UnitSystem.Millimeters),('m',.001,R.UnitSystem.Meters)]:
            for offset in [0,1000000]:
                key = f'small-t{tol}-{units}-o{offset}'
                file = R.File3dm(); file.Settings.ModelUnitSystem = unit
                file.Settings.ModelAbsoluteTolerance = tol*factor
                file.Settings.ModelAngleToleranceRadians = .001
                truth = cases(tol)
                for i, case in enumerate(truth):
                    # Gap pair shares the same cell. All other details are isolated.
                    cell = i if i < 13 else i-1
                    case['originMm'] = [5*(cell % 5),5*(cell // 5),0]
                    x,y,_ = case['originMm']
                    profiles = [curve(p) for p in case['profiles']]
                    if case['mesh']:
                        geo = R.Mesh(); geo.Vertices.UseDoublePrecisionVertices = True
                        pts = case['profiles'][0]['points']
                        for z in [case['z'],case['z']+case['depth']]:
                            for a,b in pts: geo.Vertices.Add(a,b,z)
                        for f in [(3,2,1,0),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)]: geo.Faces.AddFace(*f)
                        geo.Normals.ComputeNormals()
                    else:
                        ex = R.Extrusion.Create(profiles[0],case['depth'],True)
                        for p in profiles[1:]:
                            # AddInnerProfile consumes extrusion-local XY, not the world
                            # plane of the outer input (whose origin can be its first point).
                            p.Transform(ex.GetProfileTransformation(0).TryGetInverse())
                            assert ex.AddInnerProfile(p)
                        geo = ex
                        geo.Transform(R.Transform.Translation(0,0,case['z']))
                    geo.Transform(R.Transform.Translation(x,y,0))
                    geo.Transform(R.Transform.Scale(R.Point3d(0,0,0),factor))
                    geo.Transform(R.Transform.Translation(offset*factor,offset*factor,offset*factor))
                    assert geo.IsValid, case['name']
                    attr = R.ObjectAttributes(); attr.Name = case['name']
                    attr.Id = uuid.uuid5(NS,key+'/'+case['name'])
                    file.Objects.Add(geo,attr)
                path = DEST/(key+'.3dm'); assert file.Write(str(path),8)
                manifest['models'].append(dict(path=path.name,sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                    sizeBytes=path.stat().st_size,units=units,unitsPerMm=factor,toleranceMm=tol,offsetMm=offset,cases=truth))
    (DEST/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf8')
    print(f'Wrote {len(manifest["models"])} archives (including 0.1 mm baseline).')


if __name__ == '__main__': main()


