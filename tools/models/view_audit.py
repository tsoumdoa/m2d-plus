"""Managed-Core-only locus audits and independent polygon profile checks; no Rhino host."""
import json
import math
from pathlib import Path
from common import ROOT


def initialize_core():
    from pythonnet import load
    load('coreclr')
    import clr
    clr.AddReference(str(ROOT/'src/M2DPlus.Core/bin/Release/net8.0/M2DPlus.Core.dll'))


def load_loci(path,tolerance):
    from M2DPlus.Core.Visibility import Locus,VisibilityClass,VisibilityLoci
    from System import Array
    result=[]
    for row in json.loads(Path(path).read_text(encoding='utf-8')):
        label=row['class']; label='Visible' if label=='Cut' else label
        if label not in ('Visible','Hidden','Unresolved'): continue
        for a,b in zip(row['points'],row['points'][1:]):
            result.append(Locus(a[0],a[1],b[0],b[1],getattr(VisibilityClass,label),row['source']))
    return VisibilityLoci.Union(Array[Locus](result),tolerance)


def compare(a,b,tolerance,spacing=.05):
    from M2DPlus.Core.Visibility import Locus,VisibilityClass,VisibilityLoci
    from System import Array
    left=load_loci(a,tolerance/4); right=load_loci(b,tolerance/4)
    def direction(expected,actual):
        full=VisibilityLoci.Compare(expected,actual,tolerance,spacing)
        # Same expected sample locations, with classes erased: distinguish missing ink from a class swap.
        visible=lambda rows:Array[Locus]([Locus(l.X0,l.Y0,l.X1,l.Y1,VisibilityClass.Visible,l.Source) for l in rows])
        ink=VisibilityLoci.Compare(visible(expected),visible(actual),tolerance,spacing)
        compatible=list(actual)
        for l in actual:
            kinds=(VisibilityClass.Visible,VisibilityClass.Hidden) if l.Class==VisibilityClass.Unresolved else (VisibilityClass.Unresolved,)
            compatible.extend(Locus(l.X0,l.Y0,l.X1,l.Y1,k,l.Source) for k in kinds)
        uncertainty=VisibilityLoci.Compare(expected,Array[Locus](compatible),tolerance,spacing)
        return {'samples':full.Samples,'missed':full.Missed,'missingInkSamples':ink.Missed,
                'classOnlySamples':full.Missed-ink.Missed,'uncertaintyClassSamples':full.Missed-uncertainty.Missed,
                'confidentVisibleHiddenSwapSamples':uncertainty.Missed-ink.Missed,'sampledMissingLength':full.SampledMissingLength,
                'firstFailedSegments':[{'a':[l.X0,l.Y0],'b':[l.X1,l.Y1],'class':str(l.Class),'source':l.Source} for l in list(full.FailedSegments)[:30]]}
    missing=direction(left,right); extra=direction(right,left)
    return {'passed':missing['missed']+extra['missed']==0,'tolerance':tolerance,'spacing':spacing,
            'method':'sampled bidirectional classed loci after support-line union; Unresolved > Visible/Cut > Hidden; not continuous curve proof',
            'expectedToActual':missing,'actualToExpected':extra}


def profile_check(path,expectation,tolerance):
    """Reassemble only output cut polylines, and integrate them with shoelace (no engine area API)."""
    source=expectation.get('source')
    if not source or 'area' not in expectation: return {'status':'unknown'}
    rows=json.loads(Path(path).read_text(encoding='utf-8'))
    pieces=[list(r['points']) for r in rows if r['class']=='Cut' and source in r['source']]
    distance=lambda a,b:math.hypot(a[0]-b[0],a[1]-b[1])
    loops=[]; open_curves=0
    while pieces:
        chain=pieces.pop()
        while distance(chain[0],chain[-1])>tolerance*4:
            found=False
            for i,p in enumerate(pieces):
                if distance(chain[-1],p[0])<=tolerance*4: chain+=p[1:]; pieces.pop(i); found=True; break
                if distance(chain[-1],p[-1])<=tolerance*4: chain+=list(reversed(p))[1:]; pieces.pop(i); found=True; break
                if distance(chain[0],p[-1])<=tolerance*4: chain=p[:-1]+chain; pieces.pop(i); found=True; break
                if distance(chain[0],p[0])<=tolerance*4: chain=list(reversed(p))[:-1]+chain; pieces.pop(i); found=True; break
            if not found: break
        if distance(chain[0],chain[-1])<=tolerance*4: loops.append(chain)
        else: open_curves+=1
    def inside(p,poly):
        yes=False
        for a,b in zip(poly,poly[1:]+poly[:1]):
            if (a[1]>p[1])!=(b[1]>p[1]) and p[0]<(b[0]-a[0])*(p[1]-a[1])/(b[1]-a[1])+a[0]: yes=not yes
        return yes
    areas=[abs(sum(a[0]*b[1]-a[1]*b[0] for a,b in zip(poly,poly[1:]+poly[:1])))/2 for poly in loops]
    area=sum(a*(-1 if sum(inside(poly[0],other) for j,other in enumerate(loops) if j!=i)%2 else 1) for i,(poly,a) in enumerate(zip(loops,areas)))
    ok=not open_curves and len(loops)==expectation['loops'] and abs(area-expectation['area'])<=expectation['areaTolerance']
    shape=None
    if expectation.get('profile')=='RHS' and len(loops)==2:
        dimensions=sorted([(max(p[0] for p in l)-min(p[0] for p in l),max(p[1] for p in l)-min(p[1] for p in l)) for l in loops])
        # Planes for twisted members co-rotate by the prescribed station twist; the expected rectangle is axis-aligned here.
        shape=all(abs(w-ew)<=tolerance*8 and abs(h-eh)<=tolerance*8 for (w,h),(ew,eh) in zip(dimensions,[(.3,.5),(.4,.6)]))
        ok=ok and shape
    return {'status':'pass' if ok else 'fail','area':area,'loops':len(loops),'openChains':open_curves,'rotatedProfileShapePassed':shape,
            'method':'independent polygon line integral + containment; RHS frame-aligned bounding dimensions; display chord approximation'}


def planar_control_witness(descriptor,paths):
    """Independent analytic ray witness on the known two-box fixture, not a kernel-derived oracle.

    At (1,-1.5,z), z=0/1, the cross-box side edge is outside the other box's y>=-1
    slab. The viewing ray has dx>0, dy<0: it immediately exits the cross-box at
    x=1 and can never enter the other box. Both witnesses must be visible.
    """
    p=descriptor['plane']; x,y=p['x'],p['y']
    normal=[x[1]*y[2]-x[2]*y[1],x[2]*y[0]-x[0]*y[2],x[0]*y[1]-x[1]*y[0]]
    if not (normal[0]>0 and normal[1]<0): raise ValueError('Witness applies only to this control orientation')
    def present(rows,q,label):
        for row in rows:
            if row['class']!=label: continue
            for a,b in zip(row['points'],row['points'][1:]):
                dx,dy=b[0]-a[0],b[1]-a[1]; d=dx*dx+dy*dy
                t=max(0,min(1,((q[0]-a[0])*dx+(q[1]-a[1])*dy)/d)) if d else 0
                if math.hypot(q[0]-a[0]-t*dx,q[1]-a[1]-t*dy)<4e-4: return True
        return False
    results=[]
    witnesses=[([1,-1.5,z],'Visible') for z in (0,1)]
    # At 45 degrees the frontmost cross-box preimage at x=1,y=-.5 is
    # strictly inside box [-2,2] x [-1,1] x [-1,2]. Its ray exits that
    # occluder at (1.5,-1,z), so the source edge must be Hidden. The other
    # preimage (-1,1.5,z) is farther away and also blocked by the same box.
    if abs(normal[0]+normal[1])<1e-12:
        witnesses += [([1,-.5,z],'Hidden') for z in (0,1)]
    for world,expected in witnesses:
        delta=[a-b for a,b in zip(world,p['origin'])]
        q=[sum(a*b for a,b in zip(delta,axis)) for axis in (x,y)]
        observed={}
        for engine,path in paths.items():
            if not Path(path).exists(): continue
            rows=json.loads(Path(path).read_text())
            observed[engine]=[label for label in ('Visible','Hidden','Unresolved') if present(rows,q,label)]
        results.append({'world':world,'viewCoordinates':q,'expected':expected,'observed':observed})
    return {'method':'analytic box halfspaces: exposed rays move away from y>=-1; 45-degree interior rays hit foreground box at (1.5,-1,z); no native/kernel intersections',
            'viewRay':normal,'witnesses':results}
