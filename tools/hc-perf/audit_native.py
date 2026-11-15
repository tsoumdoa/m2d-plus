"""Geometry witnesses for every failed native comparison sample (diagnostic, no score)."""
import argparse
from collections import Counter, defaultdict
import json
import math
from pathlib import Path


def read(p):
    return json.loads(Path(p).read_text(encoding='utf-8-sig'))


def distance(l, x, y):
    dx, dy = l[2]-l[0], l[3]-l[1]
    d = dx*dx+dy*dy
    t = max(0, min(1, ((x-l[0])*dx+(y-l[1])*dy)/d)) if d else 0
    return math.hypot(x-l[0]-t*dx, y-l[1]-t*dy), t


def line(l):
    return [l[k] for k in ('X0','Y0','X1','Y1')]


class Grid:
    def __init__(self, lines, pad, cell=1):
        self.lines, self.cell, self.grid = lines, cell, defaultdict(list)
        for i,l in enumerate(lines):
            for x in range(math.floor((min(l[0],l[2])-pad)/cell), math.floor((max(l[0],l[2])+pad)/cell)+1):
                for y in range(math.floor((min(l[1],l[3])-pad)/cell), math.floor((max(l[1],l[3])+pad)/cell)+1):
                    self.grid[x,y].append(i)

    def nearby(self, x, y):
        return self.grid.get((math.floor(x/self.cell),math.floor(y/self.cell)), [])


def audit(prefix, tol=.0001, spacing=.05):
    prefix=str(prefix)
    comparison=read(prefix+'.canonical-comparison.json')
    native=read(prefix+'.native.json'); kernel=read(prefix+'.loci.json'); scene=read(prefix+'.scene.json')
    # Index in physical units; a one-millimetre grid makes face boxes explode.
    cell=max(spacing*20,tol*8)
    raw_grids=[Grid([line(l) for l in comparison['nativeCanonical'] if l['Class']==c],tol*3,cell) for c in (0,1)]
    kernel_grids=[Grid([line(l) for l in kernel if l['Class']==c],tol*3,cell) for c in (0,1)]
    edge_lines=[[e['A']['X'],e['A']['Y'],e['B']['X'],e['B']['Y']] for e in scene['Edges']]
    eg=Grid(edge_lines,tol*3,cell)
    oracle=read(prefix+'.native-oracle.json')
    if not oracle['passed']:
        raise RuntimeError('Cannot classify native exceptions when exact reference gate fails')
    truth=defaultdict(list)
    for span in oracle['oracleIntervals']:
        e=oracle['selectedEdges'][span['Edge']]
        truth[json.dumps(e,sort_keys=True)].append(span)
    slot_path=Path(prefix+'.slots.json')
    slots=read(slot_path) if slot_path.exists() else []
    duplicate_lines=[];duplicate_info=[]
    for si,slot in enumerate(slots):
        if slot['visibility']!='Duplicate':continue
        ps=slot['points']
        for a,b in zip(ps,ps[1:]):
            duplicate_lines.append([a['X'],a['Y'],b['X'],b['Y']])
            duplicate_info.append((si,slot['source'],a['Z'],b['Z']))
    dg=Grid(duplicate_lines,tol*3,cell)
    unset_lines=[];unset_info=[]
    for si,slot in enumerate(slots):
        if slot['visibility']!='Unset' or 'points' not in slot:continue
        for a,b in zip(slot['points'],slot['points'][1:]):
            unset_lines.append([a['X'],a['Y'],b['X'],b['Y']])
            unset_info.append((si,slot.get('source'),a['Z'],b['Z'],slot.get('kind')))
    ug=Grid(unset_lines,tol*3,cell)
    faces=[]; boxes=[]
    for f in scene['Faces']:
        loops=[[[p[k] for k in ('X','Y','Z')] for p in loop] for loop in f['Loops']]
        ps=[p for l in loops for p in l];a=ps[0];n=[0,0,0]
        for loop in loops:
            for i in range(1,len(loop)-1):
                u=[loop[i][k]-loop[0][k] for k in range(3)];v=[loop[i+1][k]-loop[0][k] for k in range(3)]
                n=[u[1]*v[2]-u[2]*v[1],u[2]*v[0]-u[0]*v[2],u[0]*v[1]-u[1]*v[0]]
                if sum(t*t for t in n)>0: a=loop[0];break
            if sum(t*t for t in n)>0:break
        faces.append((loops,n,a))
        boxes.append([min(p[0] for p in ps),min(p[1] for p in ps),max(p[0] for p in ps),max(p[1] for p in ps)])
    fg=Grid(boxes,tol*3,cell)
    counts=Counter(); evidence=[]
    for direction,key in [('native-to-kernel','missing'),('kernel-to-native','extra')]:
        grids=kernel_grids if key=='missing' else raw_grids
        for l in comparison[key]['FailedSegments']:
            ll=line(l);n=max(8,math.ceil(l['Length']/spacing));c=l['Class']
            for i in range(n+1):
                t=i/n;x=ll[0]+t*(ll[2]-ll[0]);y=ll[1]+t*(ll[3]-ll[1])
                near=[distance(grids[c].lines[j],x,y)[0] for j in grids[c].nearby(x,y)]
                same=min(near,default=math.inf)
                if same<=tol:continue
                record={'direction':direction,'locus':l,'sample':i,'xy':[x,y],'sameClassDistance':same if math.isfinite(same) else None}
                category='unexplained';witness=None
                if same<=tol*2.1:
                    category='tolerance artefact';witness={'kind':'native split endpoint or supporting line within 2.1 tolerance','distance':same}
                edges=[]
                class_distances={}
                kernel_class=(1-c) if key=='missing' else c
                for ei in eg.nearby(x,y):
                    d,u=distance(edge_lines[ei],x,y)
                    if d<=tol:
                        e=scene['Edges'][ei]
                        spans=truth.get(json.dumps(e,sort_keys=True),[])
                        length=math.hypot(edge_lines[ei][2]-edge_lines[ei][0],edge_lines[ei][3]-edge_lines[ei][1])
                        delta=min((max(v['Start']-u,u-v['End'],0)*length for v in spans if v['Class']==kernel_class),default=math.inf)
                        if delta<=tol:
                            class_distances[ei]=delta
                            z=e['A']['Z']+u*(e['B']['Z']-e['A']['Z']);edges.append((z,ei))
                if edges:
                    z,ei=max(edges)
                    record['frontCandidate']={'edge':ei,'z':z,'source':scene['Edges'][ei]['Source'],
                                              'referenceClassIntervalDistance':class_distances[ei]}
                    blockers=[];contacts=[];checked=0
                    for fi in fg.nearby(x,y):
                        loops,normal,a=faces[fi]
                        boundary_edges=[]
                        for loop in loops:
                            for p,q in zip(loop,loop[1:]+loop[:1]):
                                d,u=distance([p[0],p[1],q[0],q[1]],x,y)
                                boundary_z=p[2]+u*(q[2]-p[2])
                                if p[0]==q[0] and p[1]==q[1]:boundary_z=max(p[2],q[2])
                                boundary_edges.append((d,boundary_z-z))
                        for d,gap in boundary_edges:
                            if d<=tol*2.1 and gap>tol:
                                contacts.append({'kind':'nearer 3D polygon boundary, including edge-on faces','face':fi,
                                                 'boundaryDistance':d,'depthGap':gap,'edgeOn':normal[2]==0})
                        if normal[2]==0:continue
                        gap=a[2]-(normal[0]*(x-a[0])+normal[1]*(y-a[1]))/normal[2]-z
                        if gap<-tol*2:continue
                        checked+=1
                        boundary=min(d for d,_ in boundary_edges)
                        inside=False
                        for loop in loops:
                            for p,q in zip(loop,loop[1:]+loop[:1]):
                                if (p[1]>y)!=(q[1]>y) and x<(q[0]-p[0])*(y-p[1])/(q[1]-p[1])+p[0]:inside=not inside
                        if gap>tol and boundary<=tol*2.1:
                            contacts.append({'kind':'nearer occluder boundary','face':fi,'boundaryDistance':boundary,'depthGap':gap})
                        if gap>tol*2 and inside and boundary>tol*2.1:
                            blockers.append({'kind':'interior occluder','face':fi,'boundaryDistance':boundary,'depthGap':gap})
                        if inside and abs(gap-tol)<=tol*.1:
                            contacts.append({'kind':'depth threshold','face':fi,'depthGap':gap})
                    if category=='unexplained' and contacts:
                        witness=min(contacts,key=lambda w:w.get('boundaryDistance',tol))
                        category='tie-convention difference' if witness.get('boundaryDistance')==0 else 'tolerance artefact'
                    if category=='unexplained' and kernel_class==0:
                        for di in dg.nearby(x,y):
                            d,u=distance(duplicate_lines[di],x,y)
                            si,source,za,zb=duplicate_info[di]
                            dz=abs(za+u*(zb-za)-z)
                            source_matches=any(source.split('|')[0]==scene['Edges'][eidx]['Source'] and abs(zz-z)<=tol for zz,eidx in edges)
                            if d<=tol and dz<=tol and source_matches:
                                category='native quirk'
                                witness={'kind':'frontmost exact-visible candidate suppressed as native Duplicate','slot':si,
                                         'distance':d,'depthDifference':dz,'source':source};break
                    if category=='unexplained' and kernel_class==0:
                        for ui in ug.nearby(x,y):
                            d,u=distance(unset_lines[ui],x,y)
                            si,source,za,zb,kind=unset_info[ui]
                            dz=abs(za+u*(zb-za)-z)
                            source_matches=any(source==scene['Edges'][eidx]['Source'] and abs(zz-z)<=tol for zz,eidx in edges)
                            if d<=tol and dz<=tol and source_matches:
                                category='native quirk'
                                witness={'kind':'reference-visible front candidate retained only as native Unset','slot':si,
                                         'distance':d,'depthDifference':dz,'source':source,'nativeEvidence':kind};break
                    if category=='unexplained' and kernel_class==0 and not blockers and not contacts:
                        category='native quirk'
                        witness={'kind':'native lost the clear frontmost reference-visible candidate',
                                 'checkedDepthRelevantFaces':checked,'independentOraclePassed':True,
                                 'note':'No projected face contains or approaches this ray within 2.1 tolerance at a strictly nearer depth.'}
                    if category=='unexplained' and kernel_class==1 and blockers:
                        category='native quirk';witness=blockers[0]
                        witness['kind']='native visible despite a reference-certified interior occluder'
                record['category']=category;record['witness']=witness;counts[category]+=1;evidence.append(record)
    expected_misses=comparison['missing']['Missed']+comparison['extra']['Missed']
    if sum(counts.values())!=expected_misses:
        raise RuntimeError(f'Incomplete sample accounting: {sum(counts.values())}/{expected_misses}')
    result={'passed':not counts.get('unexplained',0),'counts':dict(counts),'samples':evidence,
            'note':'Diagnostic witnesses only; native quirk exceptions require separate slot/reproduction evidence.'}
    Path(prefix+'.audit.json').write_text(json.dumps(result,indent=2,allow_nan=False)+'\n')
    print(Path(prefix).name,dict(counts),flush=True)


if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('prefix');a=p.parse_args();audit(a.prefix)
