"""Independent rational ray/contact witnesses. Proposals never change gate verdicts.
Only planar-owned disagreements are considered. Curved/native ambiguity stays unexplained.
"""
import argparse,json,math,hashlib
from pathlib import Path
from fractions import Fraction as F
from collections import defaultdict,Counter

def sub(a,b): return tuple(x-y for x,y in zip(a,b))
def dot(a,b): return sum(x*y for x,y in zip(a,b))
def cross(a,b): return (a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0])
def vertex(p):return tuple(F(p[k]) for k in ('X','Y','Z'))
def distance(a,b,p):
 d=sub(b,a);square=dot(d,d);t=max(0.,min(1.,dot(sub(p,a),d)/square)) if square else 0.
 return math.sqrt(sum((a[i]+t*d[i]-p[i])**2 for i in range(2))),t
class Grid:
 def __init__(self,boxes,cell):
  self.cell=cell;self.items=defaultdict(list)
  for i,(x0,y0,x1,y1) in enumerate(boxes):
   for x in range(math.floor(x0/cell),math.floor(x1/cell)+1):
    for y in range(math.floor(y0/cell),math.floor(y1/cell)+1):self.items[x,y].append(i)
 def at(self,x,y,pad=0):
  return set(i for a in range(math.floor((x-pad)/self.cell),math.floor((x+pad)/self.cell)+1) for b in range(math.floor((y-pad)/self.cell),math.floor((y+pad)/self.cell)+1) for i in self.items.get((a,b),()))

def audit(prefix):
 prefix=str(prefix);gate=json.loads(Path(prefix+'.gate.json').read_text());scene_path=Path(prefix+'.planar-scene.json')
 if not scene_path.exists():return
 scene=json.loads(scene_path.read_text());tol=gate['tolerance'];edges=scene['Edges'];faces=scene['Faces']
 if not edges:return
 points=[(e[k]['X'],e[k]['Y']) for e in edges for k in ('A','B')]
 extent=max(max(p[0] for p in points)-min(p[0] for p in points),max(p[1] for p in points)-min(p[1] for p in points));cell=max(extent/64,tol*64)
 edge_boxes=[(min(e['A']['X'],e['B']['X']),min(e['A']['Y'],e['B']['Y']),max(e['A']['X'],e['B']['X']),max(e['A']['Y'],e['B']['Y'])) for e in edges]
 eg=Grid(edge_boxes,cell);fb=[]
 for face in faces:
  ps=[p for loop in face['Loops'] for p in loop];fb.append((min(p['X'] for p in ps),min(p['Y'] for p in ps),max(p['X'] for p in ps),max(p['Y'] for p in ps)))
 fg=Grid(fb,cell);prepared={}
 def face_data(i):
  if i not in prepared:
   loops=[[vertex(p) for p in l] for l in faces[i]['Loops']];ps=[p for l in loops for p in l];a=ps[0];n=(F(0),)*3
   for j in range(1,len(ps)-1):
    n=cross(sub(ps[j],a),sub(ps[j+1],a))
    if any(n):break
   prepared[i]=(loops,a,n)
  return prepared[i]
 def ray(ei,t):
  e=edges[ei];a=vertex(e['A']);b=vertex(e['B']);p=tuple(a[i]+F(t)*(b[i]-a[i]) for i in range(3));blockers=[];contacts=[];checked=0
  for fi in fg.at(float(p[0]),float(p[1]),tol*2.1):
   loops,a,n=face_data(fi);inside=False;boundary=False;boundary_distance=math.inf
   for loop in loops:
    winding=0
    for u,v in zip(loop,loop[1:]+loop[:1]):
     det=(v[0]-u[0])*(p[1]-u[1])-(v[1]-u[1])*(p[0]-u[0])
     on=det==0 and min(u[0],v[0])<=p[0]<=max(u[0],v[0]) and min(u[1],v[1])<=p[1]<=max(u[1],v[1]);boundary|=on
     if u[1]<=p[1]<v[1] and det>0:winding+=1
     elif v[1]<=p[1]<u[1] and det<0:winding-=1
     d,ut=distance(tuple(map(float,u[:2])),tuple(map(float,v[:2])),tuple(map(float,p[:2])));boundary_distance=min(boundary_distance,d)
     if d<=tol*2.1:
      gap=float(u[2]+F(ut)*(v[2]-u[2])-p[2])
      if gap>tol:contacts.append(dict(face=fi,kind='edge-on boundary' if n[2]==0 else 'polygon boundary',distance=d,depthGap=gap))
    if winding!=0:inside=not inside
   if n[2]==0:continue
   checked+=1;gap=(dot(n,a)-dot(n,p))/n[2]
   if (inside or boundary) and gap==0:contacts.append(dict(face=fi,kind='equal-depth closed support',distance=boundary_distance,depthGap=0.))
   if (inside or boundary) and F(0)<gap<=F(tol):contacts.append(dict(face=fi,kind='strict depth tolerance band',distance=boundary_distance,depthGap=float(gap)))
   if (inside or boundary) and gap>F(tol):blockers.append(dict(face=fi,kind='closed polygon ray',depthGap=float(gap),distance=boundary_distance))
   if (inside or boundary) and abs(float(gap)-tol)<=tol*.1:contacts.append(dict(face=fi,kind='strict depth threshold',depthGap=float(gap),distance=boundary_distance))
  return ('Hidden' if blockers else 'Visible'),blockers,contacts,checked
 failures=json.loads(Path(prefix+'.failures.json').read_text());evidence=[];counts=Counter();already={x['sample'] for x in json.loads(Path(prefix+'.order-witnesses.json').read_text())}
 for index,f in enumerate(failures):
  if index in already:continue
  category='unexplained';witness=None
  if f['KernelOwned'] and not f['Point'] and f['Expected']!='Unresolved' and f['Actual']!='Unresolved':
   world=(f['X'],f['Y'],f['Z']);delta=sub(world,scene['origin']);xy=(dot(delta,scene['x']),dot(delta,scene['y']));candidates=[]
   for ei in eg.at(*xy,tol):
    e=edges[ei];a=(e['A']['X'],e['A']['Y']);b=(e['B']['X'],e['B']['Y']);d,t=distance(a,b,xy)
    if d<=tol:candidates.append((d,ei,t))
   # The frontmost actual contributor must be ray-certified; do not excuse a
   # contact on an unrelated edge elsewhere on this supporting line.
   if candidates:
    results=[(ray(ei,t),ei,t) for _,ei,t in candidates]
    def priority(result):
     reference,ei,t=result;e=edges[ei]
     z=e['A']['Z']+t*(e['B']['Z']-e['A']['Z'])
     return (0 if reference[0]=='Visible' else 1,-z)
    (truth,blockers,contacts,checked),ei,t=min(results,key=priority)
    routed=f['Expected'] if f['Direction']=='kernel-to-native' else f['Actual']
    planar_only=gate['NativeEdges']==0
    if truth==routed and (planar_only or truth=='Hidden'):
     front=edges[ei];front_z=F(front['A']['Z'])+F(t)*(F(front['B']['Z'])-F(front['A']['Z']))
     external=[]
     for distance_xy,other,parameter in candidates:
      edge=edges[other]
      if edge['Source']==front['Source']:continue
      other_z=F(edge['A']['Z'])+F(parameter)*(F(edge['B']['Z'])-F(edge['A']['Z']))
      if abs(other_z-front_z)<=F(tol):external.append(dict(edge=other,source=edge['Source'],parameter=parameter,distance=distance_xy,depthGap=float(other_z-front_z),kind='distinct registered source at equal/contact depth'))
     if external and planar_only:
      witness=min(external,key=lambda w:abs(w['depthGap']));category='tie-convention-proposal' if witness['depthGap']==0 else 'tolerance-artefact-proposal'
     elif contacts:
      witness=min(contacts,key=lambda w:w['distance']);category='tie-convention-proposal' if witness['distance']==0 or witness['depthGap']==0 else 'tolerance-artefact-proposal'
     elif truth=='Hidden' and blockers:
      category='native-occlusion-quirk-proposal';witness=blockers[0]
     if witness:witness.update(edge=ei,source=edges[ei]['Source'],parameter=t,independentRayClass=truth,depthRelevantFaces=checked)
  counts[category]+=1
  evidence.append(dict(sample=index,classification=category,witness=witness))
 inputs={suffix:hashlib.sha256(Path(prefix+suffix).read_bytes()).hexdigest() for suffix in ('.gate.json','.failures.json','.planar-scene.json','.native-output.json','.kernel-output.json','.order-witnesses.json')}
 result=dict(proposalsOnly=True,accepted=False,inputSha256=inputs,auditSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),counts=dict(counts),producer={k:gate[k] for k in ['engineSha256','coreSha256','harnessSha256']},evidence=evidence)
 Path(prefix+'.contact-audit.json').write_text(json.dumps(result,allow_nan=False))
 print(Path(prefix).name,dict(counts),flush=True)
if __name__=='__main__':
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('prefix');a=p.parse_args();audit(a.prefix)
