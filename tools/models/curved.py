"""Curved/freeform extension. Local, serial Rhino geometry; no active document.

Run --features --tiers S,M,L,XL. Each group uses a fresh process, as generate.py.
"""
from __future__ import annotations

import argparse
import math
from pathlib import Path
import subprocess
import sys

import generate as base
from common import ROOT, SerialRhino, digest, initialize, plane_spec, view, write_json

FEATURES = ['curved-steel', 'diagrid-shell', 'space-frame', 'curved-facade',
            'tangent-continuity', 'subd-vs-nurbs']
TARGETS = {'S': 500, 'M': 5000, 'L': 20000, 'XL': 50000}
G = S = R = None


def points(values):
    return base.arr([G.Point3d(*p) for p in values], G.Point3d)


def checked(geo, solid=False):
    if geo is None or not geo.IsValid or (solid and not geo.IsSolid):
        raise ValueError('Invalid curved geometry or missing closed solid')
    return geo


def one(parts, solid=False):
    parts = list(parts or [])
    if len(parts) != 1:
        raise ValueError(f'Expected one connected Brep, got {len(parts)}')
    return checked(parts[0], solid)


def arc(radius=4, angle=1.5):
    return G.Arc(G.Plane.WorldXY, radius, angle).ToNurbsCurve()


def spline(values):
    return G.Curve.CreateInterpolatedCurve(points(values), 3)


def chs(rail, radius=.12, wall=.025):
    return one(G.Brep.CreateThickPipe(rail, radius-wall, radius, False,
               G.PipeCapMode.Flat, True, base.TOL, .01), True)


def rectangle(w, h):
    return G.Rectangle3d(G.Plane.WorldXY, G.Interval(-w/2, w/2), G.Interval(-h/2, h/2)).ToNurbsCurve()


def sweep(rail, profile, inner=None):
    ok, frame = rail.PerpendicularFrameAt(rail.Domain.Min)
    if not ok:
        raise ValueError('Cannot frame sweep')
    sections = [profile] + ([inner] if inner else [])
    sides = []
    for curve in sections:
        curve.Transform(G.Transform.PlaneToPlane(G.Plane.WorldXY, frame))
        sides.extend(G.Brep.CreateFromSweep(rail, curve, False, base.TOL))
    joined = one(G.Brep.JoinBreps(base.arr(sides, G.Brep), base.TOL)) if not inner else None
    if inner:
        # Cap the two nested boundary loops at each end as annular planar regions.
        boundaries = [c for b in sides for c in b.DuplicateNakedEdgeCurves(True, False)]
        caps = G.Brep.CreatePlanarBreps(base.arr(boundaries, G.Curve), base.TOL)
        joined = one(G.Brep.JoinBreps(base.arr(sides + list(caps), G.Brep), base.TOL))
    else:
        joined = joined.CapPlanarHoles(base.TOL)
    return checked(joined, True)


def segmented_sweep(rail, profile):
    """Retain each straight/fillet profile span as a face and its G1 edge."""
    ok, frame = rail.PerpendicularFrameAt(rail.Domain.Min)
    if not ok: raise ValueError('Cannot frame segmented sweep')
    profile.Transform(G.Transform.PlaneToPlane(G.Plane.WorldXY, frame))
    segments = list(profile.DuplicateSegments())
    if len(segments) < 12: raise ValueError('Filleted I profile lost its segment boundaries')
    faces = [b for c in segments for b in G.Brep.CreateFromSweep(rail,c,False,base.TOL)]
    joined = one(G.Brep.JoinBreps(base.arr(faces,G.Brep),base.TOL))
    return checked(joined.CapPlanarHoles(base.TOL),True)


def rhs(rail, twist=0):
    if not twist:
        return sweep(rail, rectangle(.4, .6), rectangle(.3, .5))
    # Smooth loft through rotating section frames, including the inner void.
    surfaces, ends = [], []
    for w, h in ((.4, .6), (.3, .5)):
        curves = []
        for i in range(9):
            t = rail.Domain.ParameterAt(i/8)
            ok, frame = rail.PerpendicularFrameAt(t)
            if not ok:
                raise ValueError('Cannot frame twisted member')
            frame.Rotate(twist*i/8, frame.ZAxis)
            c = rectangle(w, h); c.Transform(G.Transform.PlaneToPlane(G.Plane.WorldXY, frame)); curves.append(c)
        surfaces.extend(G.Brep.CreateFromLoft(base.arr(curves, G.Curve), G.Point3d.Unset,
                        G.Point3d.Unset, G.LoftType.Normal, False))
        ends.extend([curves[0], curves[-1]])
    caps = G.Brep.CreatePlanarBreps(base.arr(ends, G.Curve), base.TOL)
    return one(G.Brep.JoinBreps(base.arr(surfaces + list(caps), G.Brep), base.TOL), True)


def shell(width=12, length=10):
    # Tensor-product control net, smoothly doubly curved; U/V directions form
    # the two diagonal families of the roof's diamond plan.
    values = []
    for i in range(5):
        for j in range(5):
            u, v = (i-2)/2, (j-2)/2
            values.append((width*(u+v)/4, length*(u-v)/4, 4 + 1.6*(1-u*u) + .8*(1-v*v) + .3*u*v))
    return checked(G.NurbsSurface.CreateFromPoints(points(values), 5, 5, 3, 3))


def panel():
    return checked(G.NurbsSurface.CreateFromPoints(points([
        (i*.75, .18*math.sin(i*math.pi/3)+.12*j*j/9, j)
        for i in range(4) for j in range(4)]), 4, 4, 3, 3).ToBrep())


def fin():
    low = G.LineCurve(G.Point3d(0, 0, 0), G.Point3d(.8, 0, 0))
    high = G.LineCurve(G.Point3d(0, 0, 3), G.Point3d(.2, .77, 3))
    return checked(G.NurbsSurface.CreateRuledSurface(low, high).ToBrep())


def mesh_of(brep, coarse=False):
    mp = G.MeshingParameters()
    mp.Tolerance = .03 if coarse else .002
    mp.MaximumEdgeLength = .5 if coarse else .2
    mp.MinimumEdgeLength = .001
    mp.RefineGrid = True
    mesh = G.Mesh()
    for part in G.Mesh.CreateFromBrep(brep, mp):
        mesh.Append(part); part.Dispose()
    mesh.Normals.ComputeNormals(); mesh.Compact()
    return checked(mesh)


def standard(m, cut=2):
    for name in ('SW', 'Front', 'Top'):
        m.add_view(view(name, value=name))
    m.add_view(view('Plan cut', 'GH-style plane', None, plane_spec((0, 0, cut))))
    m.add_view(view('Grazing', 'vector', [.99985, .01745, .0001]))


def edge_inventory(m):
    inventory = {}
    for obj in m.file.Objects:
        if not isinstance(obj.Geometry, G.Brep):
            continue
        b = obj.Geometry
        seams = {t.Edge.EdgeIndex for f in b.Faces for loop in f.Loops for t in loop.Trims
                 if str(t.TrimType) == 'Seam' and t.Edge is not None}
        inventory[obj.Attributes.Name] = {
            'tangent': [e.EdgeIndex for e in b.Edges if e.IsSmoothManifoldEdge(.001) and e.EdgeIndex not in seams],
            'crease': [e.EdgeIndex for e in b.Edges if str(e.Valence) == 'Interior' and not e.IsSmoothManifoldEdge(.001) and e.EdgeIndex not in seams],
            'seam': sorted(seams), 'naked': [e.EdgeIndex for e in b.Edges if str(e.Valence) == 'Naked']}
    return inventory


def save(m, tier=None, flavor=None):
    m.expected['edgeInventory'] = edge_inventory(m) if tier is None else {}
    meta = m.save(hook=False, tier=tier, flavor=flavor)
    meta['family'] = 'curved'
    meta['generator'] = dict(meta['generator'], script='tools/models/curved.py', sha256=digest(__file__),
                             dependencies={n: digest(Path(__file__).with_name(n)) for n in ('generate.py', 'common.py', 'archive.py')})
    write_json(m.out / (m.name+'.json'), meta)
    return meta


def torus_cut_area(radius, tube, slope, samples):
    # Independent parametrization of torus intersected by y=slope*z.
    # X=sqrt((R+r*cos(t))^2-(slope*r*sin(t))^2), V=sqrt(1+slope^2)*r*sin(t).
    # Polygon line integral converges to the enclosed section area; no Rhino/HLD.
    coords = []
    for i in range(samples):
        t = 2*math.pi*i/samples; z = tube*math.sin(t)
        coords.append((math.sqrt((radius+tube*math.cos(t))**2-(slope*z)**2), math.sqrt(1+slope*slope)*z))
    return abs(sum(a[0]*b[1]-b[0]*a[1] for a,b in zip(coords, coords[1:]+coords[:1])))/2


def steel(m):
    m.add(chs(arc()), 'CHS-arc')
    rail = spline([(0,0,0),(1,1,.4),(3,1,1),(5,0,1.8),(6,-1,2)])
    m.add(base.moved(chs(rail), 0, 7), 'CHS-3d-chord')
    m.add(base.moved(rhs(arc()), 8, 0), 'RHS-arc')
    m.add(base.moved(rhs(rail), 0, 10), 'RHS-3d-chord')
    polygon = G.PolylineCurve(points([(-.5,-.5,0),(.5,-.5,0),(.5,-.35,0),(.1,-.35,0),(.1,.35,0),(.5,.35,0),(.5,.5,0),(-.5,.5,0),(-.5,.35,0),(-.1,.35,0),(-.1,-.35,0),(-.5,-.35,0),(-.5,-.5,0)]))
    filleted = G.Curve.CreateFilletCornersCurve(polygon, .04, base.TOL, .01)
    m.add(base.moved(segmented_sweep(arc(), filleted), 16, 0), 'I-beam-filleted-web-flanges')
    helix = spline([(2*math.cos(t),2*math.sin(t),t*.5) for t in [i*2*math.pi/24 for i in range(25)]])
    m.add(base.moved(rhs(helix), 12, 9), 'helical-stair-stringer')
    straight = G.LineCurve(G.Point3d.Origin, G.Point3d(0,0,6))
    m.add(base.moved(rhs(straight, math.pi*.75), 20, 9), 'twisted-RHS')
    rail = arc(4, math.pi/2); rail.Transform(G.Transform.Rotation(-math.pi/4, G.Vector3d.ZAxis, G.Point3d.Origin))
    torus = chs(rail, .6, .15); torus.Transform(G.Transform.Translation(25,0,2)); m.add(torus, 'oblique-curved-CHS')
    p = plane_spec((29,0,2),(1,0,0),(0,1/math.sqrt(2),1/math.sqrt(2)))
    area = lambda n: torus_cut_area(4,.6,1,n)-torus_cut_area(4,.45,1,n)
    m.expected['analyticSections'] = {'oblique-curved-CHS': {'regions':1,'holes':1,'area':area(65536),
        'areaTolerance':2e-5,'plane':p,'method':'Independent torus-plane polygon line integral, 65536 samples per loop',
        'convergenceDelta':abs(area(65536)-area(32768)), 'outerRadius':.6,'innerRadius':.45,'bendRadius':4}}
    standard(m, 1)
    m.add_view(view('Oblique hollow cut','GH-style plane',None,p,crop=[-.9,.9,-.9,.9]))


def diagrid(m):
    roof = shell(); m.add(roof.ToBrep(), 'open-roof-shell', 'Glass')
    parameters = [.15,.38,.62,.85]
    for direction in (0,1):
        for i,t in enumerate(parameters):
            rail = roof.IsoCurve(direction, roof.Domain(1-direction).ParameterAt(t))
            m.add(chs(rail,.085,.018), f'diagrid-iso-{direction}-{i}')
    for i,u in enumerate(parameters):
        for j,v in enumerate(parameters):
            p = roof.PointAt(roof.Domain(0).ParameterAt(u),roof.Domain(1).ParameterAt(v))
            m.add(G.Sphere(p,.16).ToBrep(),f'node-{i}-{j}', 'Fixtures' if (i+j)%2 else 'Structure')
    m.expected.update(openShell={'source':'open-roof-shell','solid':False,'hatch':None,'cut':'outline only'},
                      members='Surface isocurves (not claimed geodesics)',merge='Keep overlapping members/nodes separate; merge only identical resolved style')
    standard(m,5)
    m.add_view(view('Roof section','GH-style plane',None,plane_spec((0,0,0),(1,0,0),(0,0,1))))


def frame(m):
    unit = chs(G.LineCurve(G.Point3d.Origin,G.Point3d(0,0,1)),.065,.015)
    m.definition('CHS-unit',[unit]); m.definition('sphere-node',[G.Sphere(G.Point3d.Origin,.1).ToBrep()])
    nodes = {}
    for layer in (0,1):
        for i in range(13):
            for j in range(7):
                a = -.85+1.7*i/12; r=6-layer*.6
                nodes[layer,i,j] = G.Point3d(r*math.sin(a),j*1.2,r*math.cos(a))
    for key,p in nodes.items():
        m.instance('sphere-node',G.Transform.Translation(p-G.Point3d.Origin),'node-'+'-'.join(map(str,key)))
    count=0
    for (layer,i,j),a in nodes.items():
        targets=[(layer,i+1,j),(layer,i,j+1)]
        if layer==0:
            targets += [(1,i,j),(1,i+1,j+1)]
        for key in targets:
            if key not in nodes: continue
            b=nodes[key]; d=b-a; plane=G.Plane(a,d)
            t=G.Transform.PlaneToPlane(G.Plane.WorldXY,plane)*G.Transform.Scale(G.Plane.WorldXY,1,1,d.Length)
            m.instance('CHS-unit',t,f'CHS-{count:04}'); count+=1
    m.expected.update(chsMembers=count,sphericalNodes=len(nodes),highOverlap=True)
    standard(m,4.5)


def facade(m):
    glass=panel(); mesh=mesh_of(glass)
    for i in range(5):
        theta=(i-2)*.3
        t=G.Transform.Translation(8*math.sin(theta),8*(1-math.cos(theta)),0)*G.Transform.Rotation(theta,G.Vector3d.ZAxis,G.Point3d.Origin)
        for geo,name,z in ((glass,'Brep',0),(mesh,'mesh',5)):
            g=geo.Duplicate(); g.Transform(G.Transform.Translation(0,0,z)*t); m.add(g,f'glass-{name}-{i}','Glass')
        mullion=spline([(0,0,0),(.05,.04,1),(.1,.1,2),(.15,.12,3)])
        g=chs(mullion,.045,.01); g.Transform(t); m.add(g,f'curved-mullion-{i}','Envelope')
        g=fin(); g.Transform(G.Transform.Translation(0,-.3,0)*t); m.add(g,f'twisting-fin-{i}','Envelope')
        for z in (0,3):
            rail=spline([(0,0,z),(.75,.16,z),(1.5,.16,z),(2.25,0,z)])
            g=chs(rail,.045,.01); g.Transform(t); m.add(g,f'curved-transom-{i}-{z}','Envelope')
    for z in (1,2):
        rail=arc(8,1.5)
        g=sweep(rail,rectangle(.45,.06)); m.add(base.moved(g,0,9,z),f'curved-louvre-{z}','Envelope')
    outer=rectangle(3,3); outer.Transform(G.Transform.Translation(12,0,0))
    loops=[outer]+[G.Circle(G.Point3d(12+x,y,0),.25).ToNurbsCurve() for x in (-.8,0,.8) for y in (-.8,0,.8)]
    plate=one(G.Brep.CreatePlanarBreps(base.arr(loops,G.Curve),base.TOL))
    m.add(plate,'perforated-cassette-9-circular-holes','Envelope')
    m.expected.update(meshBrepPairs=[{'brep':f'glass-Brep-{i}','mesh':f'glass-mesh-{i}','translation':[0,0,5]} for i in range(5)],
                      meshing={'tolerance':.002,'maximumEdgeLength':.2},cassette={'holes':9,'area':9-9*math.pi*.25**2},
                      fins={'ruled':True,'solid':False},glass='Opaque in raw HLD; mesh approximation is intentional')
    standard(m,1.5)


def continuity(m):
    b=base.box(0,0,0,2,2,2)
    fillets=G.Brep.CreateFilletEdges(b,base.arr(range(b.Edges.Count),S.Int32),base.arr([.25]*b.Edges.Count,S.Double),
              base.arr([.25]*b.Edges.Count,S.Double),G.BlendType.Fillet,G.RailType.RollingBall,base.TOL)
    m.add(one(fillets,True),'G1-filleted-box')
    for degree,label,offset in ((2,'G1',4),(3,'G2',8)):
        # z=.6*x^degree: matches plane position/tangent; cubic also matches curvature.
        curved=G.NurbsSurface.CreateFromPoints(points([(i/degree,j*2,.6 if i==degree else 0)
                    for i in range(degree+1) for j in range(2)]),degree+1,2,degree,1).ToBrep()
        planar=G.PlaneSurface(G.Plane.WorldXY,G.Interval(-1,0),G.Interval(0,2)).ToBrep()
        joined=one(G.Brep.JoinBreps(base.arr([planar,curved],G.Brep),base.TOL))
        m.add(base.moved(joined,offset),label+'-plane-polynomial-blend')
    for i in range(2):
        torus=G.Torus(G.Plane.WorldXY,1,.3).ToBrep(); sphere=G.Sphere(G.Point3d.Origin,.9).ToBrep()
        rotation=G.Transform.Rotation(i*math.pi/2,G.Vector3d.XAxis,G.Point3d.Origin)
        torus.Transform(rotation); sphere.Transform(rotation)
        m.add(base.moved(torus,i*4,5,1),f'torus-seam-{i}')
        m.add(base.moved(sphere,8+i*3,5,1),f'sphere-seam-{i}')
    m.add(G.PlaneSurface(G.Plane.WorldXY,G.Interval(0,2),G.Interval(8,10)).ToBrep(),'exact-edge-on-sheet')
    m.expected.update(continuity={'G1-plane-polynomial-blend':'z=0 joins z=0.6*x^2; C1/G1, not G2',
                    'G2-plane-polynomial-blend':'z=0 joins z=0.6*x^3; C2/G2'},
                    edgeClassification='Archived Brep edge indices; IsSmoothManifoldEdge at 0.001 radians; seam trims recorded separately')
    for camera in ('Front','SW','Top'):
        for tangent,seam in (('off','off'),('on','off'),('on','on'),('off','on')):
            v=view(f'{camera} tangents {tangent} seams {seam}',value=camera)
            v['settings'].update(tangents=tangent,seams=seam); m.add_view(v)
    m.add_view(view('Grazing','vector',[1,.00001,.00001]))


def subd(m):
    organic=base.canopy(); brep=organic.ToBrep(); mesh=G.Mesh.CreateFromSubD(organic,3)
    m.add(organic,'canopy-SubD','Glass'); m.add(base.moved(brep,7),'canopy-NURBS','Envelope')
    m.add(base.moved(mesh,14),'canopy-mesh','Fixtures')
    m.expected.update(representations={'SubD':[0,0,0],'NURBS':[7,0,0],'mesh':[14,0,0]},
                      sameLimitSurface=True,meshSubdivisionDensity=3,rawHldSubD='Explicit ToBrep normalization; compare with converted NURBS',
                      meshFidelity='Approximation of the same SubD limit surface, not exact equality')
    standard(m,.35)


def performance(tier, flavor, seed, out):
    target=TARGETS[tier]; m=base.Model(f'curved-perf-{tier}-{flavor}',seed,out)
    # Curved ring bays: a rotated array of identical double-curved panels,
    # fins and CHS roof diagonals, with floors rotating to form twisted towers.
    bays=20; capacity_floors={'S':2,'M':10,'L':20,'XL':25}[tier]
    buildings=max(1,math.ceil(target/(bays*capacity_floors*12)))
    per_building=math.ceil(target/buildings)
    # Use the occupied storey count, not the nominal capacity used to choose
    # building count. This keeps the roof and upper plan cut on actual geometry.
    floors=math.ceil(per_building/(bays*12))
    bay_rail=arc(8,.3)
    bay_rail.Transform(G.Transform.Translation(0,8,0)*G.Transform.Rotation(-math.pi/2,G.Vector3d.ZAxis,G.Point3d.Origin))
    prototypes={'glass':panel(),'fin':fin(),
        'mullion':chs(spline([(0,0,0),(.03,.04,1),(.08,.09,2),(.12,.12,3)]),.04,.01),
        'transom':chs(bay_rail,.04,.01),
        'column':chs(G.LineCurve(G.Point3d.Origin,G.Point3d(0,0,3)),.1,.02),
        'roof-diagonal':chs(spline([(0,0,0),(1,1,.4),(2,2,.5),(3,3,0)]),.08,.018),
        'node':G.Sphere(G.Point3d.Origin,.14).ToBrep(),
        'slab':sweep(bay_rail,rectangle(2.5,.18))}
    def layer(key): return 'Glass' if key=='glass' else ('Envelope' if key in ('fin','mullion','transom') else 'Structure')
    if flavor=='blocks':
        for key,g in prototypes.items(): m.definition(key,[g],[m.attrs(key+'-member',layer(key))])
    parts=[('glass',0,0,0),('fin',0,-.3,0),('mullion',0,0,0),('column',0,1,0),
           ('transom',0,0,0),('slab',0,0,0),('roof-diagonal',0,0,0),('node',0,0,0),
           ('fin',1,-.3,0),('mullion',2.2,0,0),('roof-diagonal',0,0,0),('node',3,3,0)]
    count=0
    for i in range(target):
        building=i//per_building; local=i%per_building
        cell=local//len(parts); bay=cell%bays; level=(cell//bays)%floors
        theta=2*math.pi*bay/bays+level*.035
        radius=8+1.2*math.cos(3*theta)
        key,x,y,z=parts[local%len(parts)]
        # Roof steel sits on each stepped wing's roof, not floating on every floor.
        if key in ('roof-diagonal','node'):
            # Distinct concentric roof rings avoid accidental coincident copies
            # when the same bay occurs on several storeys.
            y += level*.25
            z += (floors-level)*3 + .6*math.sin(2*theta) + .12*level
        twist=G.Transform.Rotation(theta+math.pi/2,G.Vector3d.ZAxis,G.Point3d.Origin)
        t=G.Transform.Translation((building%3)*30+radius*math.cos(theta),(building//3)*30+radius*math.sin(theta),level*3)*twist*G.Transform.Translation(x,y,z)
        # Alternate crossing diagonal families at the roof.
        if local%len(parts)==10: t=t*G.Transform.Rotation(math.pi/2,G.Vector3d.ZAxis,G.Point3d.Origin)
        name=f'b{building:02}-f{level:02}-bay{bay:02}-{key}-{i:06}'
        if flavor=='blocks': m.instance(key,t,name,layer=layer(key))
        else:
            g=prototypes[key].Duplicate(); g.Transform(t); m.add(g,name,layer(key))
        count+=1
    center=((min(buildings,3)-1)*15,((buildings-1)//3)*15,floors*1.5); half=max(45,floors*2,buildings*15)
    for name,x,y in [('South elevation',(1,0,0),(0,0,1)),('North elevation',(-1,0,0),(0,0,1)),
                     ('East elevation',(0,1,0),(0,0,1)),('West elevation',(0,-1,0),(0,0,1))]:
        m.add_view(view(name,'named view',name,plane_spec(center,x,y),frame=[-half,half,-half,half]))
    for name,z in [('Plan level 1',1.2),('Plan upper',(floors-1)*3+1.2)]:
        m.add_view(view(name,'clipping plane name',name,plane_spec((center[0],center[1],z))))
    for name,p in [('Section transverse',plane_spec((0,0,0),(0,1,0),(0,0,1))),
                   ('Section long overlap',plane_spec((0,0,floors*3),(1,0,0),(0,0,1)))]:
        m.add_view(view(name,'clipping plane name',name,p))
    a=math.sqrt(.5); b=1/math.sqrt(6)
    m.add_view(view('SW axo','named view','SW axo',plane_spec(center,(a,-a,0),(b,b,2*b)),frame=[-half,half,-half,half]))
    a=.01
    m.add_view(view('Grazing','named view','Grazing',plane_spec(center,(math.sin(a),-math.cos(a),0),(0,0,1)),frame=[-half,half,-half,half]))
    m.expected.update(targetObjects=target,buildings=buildings,floors=floors,plan='r=8+1.2*cos(3*theta)',
                      storeyTwistRadians=.035,roofSection='Section long overlap',
                      equivalentFlavor=f'curved-perf-{tier}-'+('exploded' if flavor=='blocks' else 'blocks'),
                      fullVisibilityTruth='unknown')
    return save(m,tier,flavor)


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--features',action='store_true'); p.add_argument('--only')
    p.add_argument('--tiers',default=''); p.add_argument('--flavors',default='blocks,exploded')
    p.add_argument('--seed',type=int,default=20261114); p.add_argument('--out',type=Path,default=ROOT/'artifacts/models')
    p.add_argument('--single',action='store_true',help=argparse.SUPPRESS); args=p.parse_args()
    selected=args.only.split(',') if args.only else FEATURES if args.features else []
    tiers=list(filter(None,args.tiers.split(',')))
    if any(n not in FEATURES for n in selected) or any(t not in TARGETS for t in tiers): p.error('Unknown model/tier')
    if any(f not in ('blocks','exploded') for f in args.flavors.split(',')): p.error('Unknown flavor')
    if not args.single:
        jobs=[['--only',n] for n in selected]+[['--tiers',t,'--flavors',f] for t in tiers for f in args.flavors.split(',')]
        for job in jobs:
            subprocess.run([sys.executable,'-B','-u',str(Path(__file__).resolve()),'--single','--seed',str(args.seed),
                            '--out',str(args.out.resolve()),*job],check=True,stdout=sys.stdout,stderr=sys.stderr,
                           creationflags=subprocess.CREATE_NO_WINDOW)
        return
    global R,S,G
    with SerialRhino():
        R,S=initialize(); G=R.Geometry; base.R,base.S,base.G=R,S,G
        try:
            constructors=dict(zip(FEATURES,[steel,diagrid,frame,facade,continuity,subd]))
            for name in selected:
                m=base.Model(name,args.seed,args.out); constructors[name](m); save(m)
            for t in tiers:
                for f in args.flavors.split(','): performance(t,f,args.seed,args.out)
        finally:
            for model in base.OPEN_MODELS: model.Dispose()


if __name__=='__main__': main()
