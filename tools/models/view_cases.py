"""Cases 5–8. Local serial generation; blocks tiers, no active Rhino document.

Stations cover distinct rail/profile families, not every repeated instance.
The source archives are regenerated with the existing committed generators.
"""
from __future__ import annotations
import argparse
import json
import math
from pathlib import Path
import subprocess
import sys
import generate as b
import curved as c
from common import ROOT, SerialRhino, digest, expand, initialize, plane_spec, view, write_json

STATIONS = (.125, .25, .5, .75, .875)
G = R = S = None


def xyz(p):
    return [p.X, p.Y, p.Z]


def spec(p):
    return plane_spec(xyz(p.Origin), xyz(p.XAxis), xyz(p.YAxis))


def plane(p):
    return G.Plane(G.Point3d(*p['origin']), G.Vector3d(*p['x']), G.Vector3d(*p['y']))


def bounds(m):
    bb = G.BoundingBox.Empty
    for g, t, _, _ in expand(m.file, R):
        box = g.GetBoundingBox(True); box.Transform(t); bb.Union(box)
    return bb


def add_view(m, name, p, case, expectation=None, exterior=False):
    bb = bounds(m)
    margin = max(.1, bb.Diagonal.Length * .04)
    if exterior:
        p.Origin += p.ZAxis * (max(p.DistanceTo(q) for q in bb.GetCorners()) + margin)
    local = [(q-p.Origin) for q in bb.GetCorners()]
    crop = [min(q*p.XAxis for q in local)-margin, max(q*p.XAxis for q in local)+margin,
            min(q*p.YAxis for q in local)-margin, max(q*p.YAxis for q in local)+margin]
    depth = max(margin, max(-p.DistanceTo(q) for q in bb.GetCorners())+margin)
    v = view(name, 'box', None, spec(p), crop=crop, depth=depth, case=case,
             expected=expectation or {'sectionTruth':'unknown; no independent closed-profile oracle'})
    # Three GH value forms plus a persisted Extrusion with an independently readable path.
    v['ghViews'] = {'plane':spec(p), 'rectangle':{'plane':spec(p),'x':crop[:2],'y':crop[2:]},
                    'box':{'plane':spec(p),'x':crop[:2],'y':crop[2:],'z':[-depth,0]}}
    v['settings']['farclip'] = 'noline'
    m.add_view(v)


def persist_boxes(m):
    # Match UX rev.4 and ViewBoxes.Build: profile X = -frame X, path = -frame Z.
    for v in m.views:
        p = plane(v['plane']); x0,x1,y0,y1 = v['crop']
        ext = G.Extrusion()
        assert ext.SetPathAndUp(p.Origin, p.Origin-p.ZAxis*v['depth'], p.YAxis)
        profile = G.PolylineCurve(b.arr([G.Point3d(x,y,0) for x,y in
            [(-x0,y0),(-x1,y0),(-x1,y1),(-x0,y1),(-x0,y0)]],G.Point3d))
        assert ext.SetOuterProfile(profile,True) and ext.IsValid
        a = m.attrs('View box '+v['name'],'View sources'); a.SetUserString('m2d.view','1')
        for key in ('name','farclip','beyond','fade','fade.start','fade.end','fade.limit','hidden',
                    'tangents','seams','glass','scale','weights','hatch','cuts'):
            a.SetUserString('m2d.'+key, 'default')
        oid = m.file.Objects.AddExtrusion(ext,a)
        v['viewBox'] = {'objectId':str(oid),'front':spec(p),'pathEnd':xyz(ext.PathEnd),'depth':v['depth']}
        profile.Dispose(); ext.Dispose()


def save(m, tier=None):
    geometry_stats = b.stats(m.file,R)
    persist_boxes(m)
    meta = m.save(False,tier,'blocks' if tier else None)
    meta['stats'] = geometry_stats
    meta['family'] = 'view-cases'
    meta['generator'] = dict(meta['generator'],script='tools/models/view_cases.py',sha256=digest(__file__),
        dependencies={n:digest(Path(__file__).with_name(n)) for n in ('generate.py','curved.py','common.py','archive.py')})
    write_json(m.out/(m.name+'.json'),meta)


def load_source(source, name, out):
    m = b.Model(name,20261114,out)
    b.OPEN_MODELS.remove(m.file); m.file.Dispose()
    m.file = R.FileIO.File3dm.Read(str(source)); b.OPEN_MODELS.append(m.file)
    m.layers = {l.Name:l.Index for l in m.file.AllLayers}
    for obj in list(m.file.Objects):
        if isinstance(obj.Geometry,G.ClippingPlaneSurface) or obj.Attributes.GetUserString('m2d.view'):
            m.file.Objects.Delete(obj.Id)
    meta = json.loads(source.with_suffix('.json').read_text())
    if digest(source) != meta['sha256']: raise ValueError('Source archive differs from its sidecar: '+str(source))
    m.source_fixtures[source.name] = digest(source)
    m.expected['sourceArchive'] = {'id':meta['id'],'sha256':meta['sha256']}
    return m,meta


def grid(source, angle, out):
    m,meta = load_source(source,f'view-grid-{source.stem}-{angle:02}',out)
    rotation = G.Transform.Rotation(math.radians(angle),G.Vector3d.ZAxis,G.Point3d.Origin)
    for obj in list(m.file.Objects):
        if obj.Attributes.IsInstanceDefinitionObject: continue
        geo = obj.Geometry.Duplicate(); assert geo.Transform(rotation)
        attrs = obj.Attributes.Duplicate(); m.file.Objects.Delete(obj.Id); m.file.Objects.Add(geo,attrs); geo.Dispose()
    originals = [v for v in meta['views'] if v['name'] != 'Grazing']
    # Full four axos, using the same source-space origin as the existing SW.
    source_center = plane(originals[-1]['plane']).Origin
    for label,d in [('SE',(-1,1,-1)),('NE',(-1,-1,-1)),('NW',(1,-1,-1))]:
        normal = -G.Vector3d(*d); normal.Unitize()
        x = G.Vector3d.CrossProduct(G.Vector3d.ZAxis,normal); x.Unitize()
        originals.append(view(label+' axo','named view',label,spec(G.Plane(source_center,x,G.Vector3d.CrossProduct(normal,x)))))
    for v in originals:
        p = plane(v['plane']); p.Transform(rotation)
        add_view(m,v['name'],p,5,{'metamorphic':{'referenceModel':f'view-grid-{source.stem}-00',
            'referenceView':v['name'],'rotationDegrees':angle,'axis':'world Z','expected':'equal classed drawing loci up to rigid 2D transform',
            'comparisonCoordinates':'co-rotated frame; origin translation included, no fitted scaling'}},
            exterior=v['source']['kind']=='named view')
    m.expected['rotationDegrees'] = angle
    save(m,meta['tier'])


def stations(m, rail, source, profile, area=None, loops=None, twist=0):
    for i,s in enumerate(STATIONS,1):
        t=rail.Domain.ParameterAt(s); ok,p = rail.PerpendicularFrameAt(t)
        assert ok
        # A perpendicular frame uses the path tangent as plane normal. Record both axes.
        p.Rotate(twist*s,p.ZAxis)
        expected={'source':source.split('/')[0],'sourcePath':source,'profile':profile,'station':s,'parameter':t,
            'pathTangent':xyz(rail.TangentAt(t)),'up':'parallel-transport path frame normal/binormal',
            'twistRadians':twist*s,'sectionTruth':'analytic profile at station' if area is not None else 'unknown'}
        if area is not None: expected.update(area=area,loops=loops,areaTolerance=max(2e-5,area*.002))
        if profile=='RHS': expected['profileDimensions']={'outer':[.4,.6],'inner':[.3,.5]}
        add_view(m,f'Normal {source} station {i}',p,6,expected)


def translated(rail,x=0,y=0,z=0):
    rail.Transform(G.Transform.Translation(x,y,z)); return rail


def normal_views(m, family, meta):
    if family=='curved-steel':
        make_spline=lambda:c.spline([(0,0,0),(1,1,.4),(3,1,1),(5,0,1.8),(6,-1,2)])
        for name,rail,prof,area,loops in [
            ('CHS-arc',c.arc(),'CHS',math.pi*(.12**2-.095**2),2),
            ('CHS-3d-chord',translated(make_spline(),0,7),'CHS',math.pi*(.12**2-.095**2),2),
            ('RHS-arc',translated(c.arc(),8),'RHS',.4*.6-.3*.5,2),
            ('RHS-3d-chord',translated(make_spline(),0,10),'RHS',.4*.6-.3*.5,2),
            # 8 convex and 4 concave right-angle fillets: net -4*r²*(1-pi/4).
            ('I-beam-filleted-web-flanges',translated(c.arc(),16),'filleted I',.44-4*.04**2*(1-math.pi/4),1),
            ('helical-stair-stringer',translated(c.spline([(2*math.cos(t),2*math.sin(t),t*.5) for t in [i*2*math.pi/24 for i in range(25)]]),12,9),'RHS',None,None)]:
            stations(m,rail,name,prof,area,loops)
        stations(m,translated(G.LineCurve(G.Point3d.Origin,G.Point3d(0,0,6)),20,9),'twisted-RHS','RHS',.09,2,math.pi*.75)
        rail=c.arc(4,math.pi/2); rail.Transform(G.Transform.Rotation(-math.pi/4,G.Vector3d.ZAxis,G.Point3d.Origin))
        stations(m,translated(rail,25,0,2),'oblique-curved-CHS','CHS',math.pi*(.6**2-.45**2),2)
    elif family=='diagrid-shell':
        roof=c.shell()
        for d in (0,1):
            for i,t in enumerate((.15,.38,.62,.85)):
                stations(m,roof.IsoCurve(d,roof.Domain(1-d).ParameterAt(t)),f'diagrid-iso-{d}-{i}',
                         'CHS',math.pi*(.085**2-.067**2),2)
    elif family=='space-frame':
        # Existing vault uses straight chords, not swept curved members. Sample all four link families.
        leaves=list(expand(m.file,R))
        for g,t,name,_ in [x for x in leaves if x[2].startswith('CHS-')][:4]:
            a=G.Point3d.Origin; z=G.Point3d(0,0,1); a.Transform(t); z.Transform(t)
            stations(m,G.LineCurve(a,z),name,'straight CHS chord',math.pi*(.065**2-.05**2),2)
    elif family=='curved-facade':
        rail=c.spline([(0,0,0),(.05,.04,1),(.1,.1,2),(.15,.12,3)])
        stations(m,rail,'curved-mullion-2','CHS',math.pi*(.045**2-.035**2),2)
        stations(m,c.spline([(0,0,0),(.75,.16,0),(1.5,.16,0),(2.25,0,0)]),'curved-transom-2-0','CHS',math.pi*(.045**2-.035**2),2)
    else:
        # First instance of each distinct curved member prototype; preserve full-scene occlusion.
        rails={'mullion':c.spline([(0,0,0),(.03,.04,1),(.08,.09,2),(.12,.12,3)]),
               'roof-diagonal':c.spline([(0,0,0),(1,1,.4),(2,2,.5),(3,3,0)]),'transom':c.arc(8,.3)}
        rails['transom'].Transform(G.Transform.Translation(0,8,0)*G.Transform.Rotation(-math.pi/2,G.Vector3d.ZAxis,G.Point3d.Origin))
        for key,rail in rails.items():
            obj=next(o for o in m.file.Objects if not o.Attributes.IsInstanceDefinitionObject and '-'+key+'-' in o.Attributes.Name)
            rail.Transform(obj.Geometry.Xform)
            r,w=(.08,.018) if key=='roof-diagonal' else (.04,.01)
            stations(m,rail,obj.Attributes.Name,'CHS',math.pi*(r*r-(r-w)**2),2)
    if family=='curved-facade' or 'perf-' in family:
        # Plan guide of facade/envelope: tangent lies in plan, so section normal is tangent and up is Z.
        for i,s in enumerate(STATIONS,1):
            theta=(-.6+1.2*s) if family=='curved-facade' else 2*math.pi*s
            if family=='curved-facade':
                point=G.Point3d(8*math.sin(theta),8*(1-math.cos(theta)),1.5); tangent=G.Vector3d(math.cos(theta),math.sin(theta),0)
            else:
                r=8+1.2*math.cos(3*theta); dr=-3.6*math.sin(3*theta)
                point=G.Point3d(r*math.cos(theta),r*math.sin(theta),1.5)
                tangent=G.Vector3d(dr*math.cos(theta)-r*math.sin(theta),dr*math.sin(theta)+r*math.cos(theta),0)
            tangent.Unitize(); x=G.Vector3d.CrossProduct(G.Vector3d.ZAxis,tangent)
            add_view(m,f'Facade normal station {i}',G.Plane(point,x,G.Vector3d.ZAxis),7,
                     {'station':s,'planCurveTangent':xyz(tangent),'sectionTruth':'unknown; facade/envelope guide, not a closed profile'})
    bb=bounds(m)
    for angle in (15,30,60):
        p=G.Plane(G.Point3d(bb.Center.X,bb.Center.Y,bb.Max.Z-.3),G.Vector3d.XAxis,G.Vector3d.ZAxis)
        p.Rotate(math.radians(angle),p.XAxis)
        add_view(m,f'Tilted roof {angle}',p,7,{'angleFromVerticalDegrees':angle,'sectionTruth':'unknown; open shell gives outlines, no filled region'})


def radial(m, periodic=False):
    for angle in range(0,180,15):
        for direction in (1,-1):
            a=math.radians(angle); x=G.Vector3d(math.cos(a)*direction,math.sin(a)*direction,0)
            add_view(m,f'Radial {angle:03} '+('forward' if direction==1 else 'reverse'),
                     G.Plane(G.Point3d.Origin,x,G.Vector3d.ZAxis),8,
                     {'centreAxis':[[0,0,0],[0,0,1]],'azimuthDegrees':angle,'direction':direction,
                      'symmetry':{'periodDegrees':30,'expected':'classed loci equal for angles modulo 30 and opposed directions'} if periodic else
                                 {'expected':'none; model is not rotationally periodic; diagnostic radial series'}})
    for z in (1.2,4.2): add_view(m,f'Radial reference plan {z}',G.Plane(G.Point3d(0,0,z),G.Vector3d.ZAxis),8)


def tower(out):
    m=b.Model('view-round-tower',20261114,out)
    # Twelve equal sectors; seam orientation can change native drawing output, but not the solid.
    core=c.chs(G.LineCurve(G.Point3d.Origin,G.Point3d(0,0,9)),2,.25); m.add(core,'cylindrical-core')
    for z in (0,3,6,9):
        slab=c.chs(G.LineCurve(G.Point3d(0,0,z-.2),G.Point3d(0,0,z)),8,6); m.add(slab,f'annular-slab-{z}')
    for i in range(12):
        a=i*math.pi/6
        m.add(b.moved(b.cylinder(.18,9),6.8*math.cos(a),6.8*math.sin(a)),f'column-{i:02}')
        rail=G.Arc(G.Plane.WorldXY,8,math.pi/6).ToNurbsCurve(); rail.Transform(G.Transform.Rotation(a,G.Vector3d.ZAxis,G.Point3d.Origin))
        m.add(G.Surface.CreateExtrusion(rail,G.Vector3d(0,0,9)).ToBrep(),f'curtain-wall-{i:02}','Glass')
    radial(m,True); m.expected['rotationalPeriodDegrees']=30
    for v in m.views:
        # Independent material area of a diametral cut: two .25 x 9 rectangles.
        plan='reference plan' in v['name']
        v['expected'].update(source='cylindrical-core',profile='annular core' if plan else 'two core walls',
            area=math.pi*(2**2-1.75**2) if plan else 2*.25*9,loops=2,areaTolerance=2e-4,
            sectionTruth='analytic annular core; other sources checked separately')
    save(m)


def control(out):
    m=b.Model('view-planar-control',20261114,out)
    m.add(b.box(-2,-1,-1,4,2,3),'box'); m.add(b.box(-1,-2,0,2,4,1),'cross-box')
    for angle in (17,45):
        p=G.Plane(G.Point3d.Origin,G.Vector3d.XAxis,G.Vector3d.ZAxis); p.Rotate(math.radians(angle),G.Vector3d.ZAxis)
        add_view(m,f'Planar oblique {angle}',p,5,exterior=True)
    save(m)


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--sources',type=Path,default=ROOT/'artifacts/models')
    p.add_argument('--out',type=Path,default=ROOT/'artifacts/view-models')
    p.add_argument('--single'); args=p.parse_args()
    tasks=[f'grid:{f}:{t}:{a}' for f in ('orthogonal','curved') for t in ('S','M') for a in (0,17,45)]
    tasks += ['normal:'+s for s in ('curved-steel','space-frame','diagrid-shell','curved-facade','curved-perf-S-blocks','curved-perf-M-blocks')]+['tower','control']
    if not args.single:
        for task in tasks:
            subprocess.run([sys.executable,'-B','-u',__file__,'--sources',str(args.sources),'--out',str(args.out),'--single',task],check=True)
        return
    global G,R,S
    with SerialRhino(wait_seconds=3600):
        R,S=initialize(); G=R.Geometry
        b.R=c.R=R; b.S=c.S=S; b.G=c.G=G
        if args.single=='tower': tower(args.out)
        elif args.single=='control': control(args.out)
        elif args.single.startswith('grid:'):
            _,family,tier,angle=args.single.split(':'); prefix='curved-' if family=='curved' else ''
            grid(args.sources/f'{prefix}perf-{tier}-blocks.3dm',int(angle),args.out)
        else:
            family=args.single.split(':')[1]; m,meta=load_source(args.sources/(family+'.3dm'),'view-'+family,args.out)
            normal_views(m,family,meta)
            if family=='diagrid-shell' or 'perf-' in family: radial(m)
            save(m,meta['tier'])


if __name__=='__main__': main()
