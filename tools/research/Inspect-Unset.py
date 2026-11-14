"""Serial licensed Unset census. Detached File3dm inputs; never touches live Rhino documents.
Uses the local test-suite adapter only to reproduce its exact camera/normalisation/clipping.
Unset access is diagnostic and guarded, never used as an ordinary drawing curve.
"""
import sys, json, hashlib, time
from pathlib import Path
from collections import Counter
suite = Path(sys.argv[1]).resolve()
sys.path.insert(0, str(suite / 'tools/models'))
from common import initialize, expand, SerialRhino
from native import resolve
out = Path(sys.argv[2]).resolve(); out.mkdir(parents=True, exist_ok=True)
with SerialRhino():
 R, S = initialize(); G=R.Geometry
 records=[]
 for tier in (() if '--extract' in sys.argv else ('S','M')):
  path = suite / f'artifacts/models/perf-{tier}-blocks.3dm'
  sidecar=json.loads(path.with_suffix('.json').read_text())
  for name in ('Section long overlap','South elevation'):
   print(tier,name,flush=True)
   model=R.FileIO.File3dm.Read(str(path)); leaves=list(expand(model,R))
   descriptor=next(v for v in sidecar['views'] if v['name']==name)
   pl,bounds,frame,clips,scope,depth,cut=resolve(model,descriptor,leaves,R)
   vp=R.DocObjects.ViewportInfo(); vp.IsParallelProjection=True
   distance=max(bounds.Diagonal.Length*2,10)
   vp.SetCameraLocation(bounds.Center+pl.ZAxis*distance);vp.SetCameraDirection(-pl.ZAxis);vp.SetCameraUp(pl.YAxis)
   vp.SetFrustum(-distance,distance,-distance,distance,.001,distance*4)
   p=G.HiddenLineDrawingParameters();p.AbsoluteTolerance=model.Settings.ModelAbsoluteTolerance;p.Flatten=False
   p.IncludeHiddenCurves=True;p.IncludeTangentEdges=False;p.IncludeTangentSeams=False;p.SetViewport(vp)
   for clip in clips:p.AddClippingPlane(clip)
   owned=[]
   for geo,transform,tag,attrs in leaves:
    if isinstance(geo,G.SubD):geo=geo.ToBrep();owned.append(geo)
    p.AddGeometry(geo,transform,tag)
   start=time.perf_counter(); drawing=G.HiddenLineDrawing.Compute(p,False)
   valid=Counter(); classes=Counter(); samples=[]; mapped={};parents={}
   for seg in drawing.Segments:
    if str(seg.SegmentVisibility)!='Unset':
     parent=seg.ParentCurve
     if parent is not None:
      parents[parent.Index]=parent
      for sibling in parent.Segments:mapped[sibling.Index]=str(parent.SourceObject.Tag) if parent.SourceObject else None
   for seg in drawing.Segments:
    vis=str(seg.SegmentVisibility);valid[vis]+=1
    if vis!='Unset':continue
    rec={'index':seg.Index,'siblingOwner':mapped.get(seg.Index)}
    try:
     parent=seg.ParentCurve
     rec['parent']=None if parent is None else {'index':parent.Index,'valid':parent.IsValid,'tag':str(parent.SourceObject.Tag) if parent.SourceObject else None,'kind':str(parent.SilhouetteType),'parameters':list(parent.Parameters)}
    except Exception as e:rec['parentError']=str(e).splitlines()[0]
    try:
     proxy=seg.CurveGeometry
     rec['proxy']=proxy is not None
     rec['valid']=proxy.IsValid if proxy is not None else False
     if rec['valid']:
      curve=proxy.DuplicateCurve()
      try:
       rec['length']=curve.GetLength(); rec['domain']=[curve.Domain.Min,curve.Domain.Max]
       rec['start']=[curve.PointAtStart.X,curve.PointAtStart.Y,curve.PointAtStart.Z]
       rec['end']=[curve.PointAtEnd.X,curve.PointAtEnd.Y,curve.PointAtEnd.Z]
      finally:curve.Dispose()
    except Exception as e:rec['geometryError']=str(e).splitlines()[0]
    key=('valid-zero' if rec.get('valid') and rec.get('length',1)<=p.AbsoluteTolerance else 'valid-real' if rec.get('valid') else 'invalid-proxy')
    key+='-parent' if rec.get('parent') else '-no-parent';classes[key]+=1
    if len(samples)<12:samples.append(rec)
   row={'model':str(path),'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'view':name,'raw':dict(valid),'classes':dict(classes),'samples':samples,'seconds':time.perf_counter()-start}
   print(json.dumps(row),flush=True);records.append(row)
   (out/'census.json').write_text(json.dumps({'rhino':str(R.RhinoApp.Version),'runtime':str(S.Environment.Version),'records':records},indent=2))
   drawing.Dispose();vp.Dispose();model.Dispose()
   for geo in owned:geo.Dispose()

 if '--extract' in sys.argv:
  path=suite/'artifacts/models/perf-S-blocks.3dm';model=R.FileIO.File3dm.Read(str(path));leaves=list(expand(model,R));sidecar=json.loads(path.with_suffix('.json').read_text());descriptor=next(v for v in sidecar['views'] if v['name']=='Section long overlap')
  pl,bounds,frame,clips,scope,depth,cut=resolve(model,descriptor,leaves,R);vp=R.DocObjects.ViewportInfo();vp.IsParallelProjection=True;distance=max(bounds.Diagonal.Length*2,10);vp.SetCameraLocation(bounds.Center+pl.ZAxis*distance);vp.SetCameraDirection(-pl.ZAxis);vp.SetCameraUp(pl.YAxis);vp.SetFrustum(-distance,distance,-distance,distance,.001,distance*4)

  def probe(selected):
   p=G.HiddenLineDrawingParameters();p.AbsoluteTolerance=.0001;p.Flatten=False;p.IncludeHiddenCurves=True;p.IncludeTangentEdges=False;p.IncludeTangentSeams=False;p.SetViewport(vp);owned=[]
   for clip in clips:p.AddClippingPlane(clip)
   for geo,t,tag,attrs in selected:
    if isinstance(geo,G.SubD):geo=geo.ToBrep();owned.append(geo)
    p.AddGeometry(geo,t,tag)
   d=G.HiddenLineDrawing.Compute(p,False);counts=Counter(str(s.SegmentVisibility) for s in d.Segments);bad=[s.Index for s in d.Segments if str(s.SegmentVisibility)=='Unset'];d.Dispose()
   for geo in owned:geo.Dispose()
   return bad,counts
  selected=leaves;granularity=2
  while len(selected)>1:
   changed=False;size=max(1,len(selected)//granularity)
   for start in range(0,len(selected),size):
    complement=selected[:start]+selected[start+size:]
    if complement and probe(complement)[0]:selected=complement;changed=True;granularity=max(2,granularity-1);print('reduce',len(selected),flush=True);break
   if not changed:
    if granularity>=len(selected):break
    granularity=min(len(selected),granularity*2)
  bad,counts=probe(selected);archive=R.FileIO.File3dm();archive.Settings.ModelAbsoluteTolerance=.0001;archive.Settings.ModelUnitSystem=R.UnitSystem.Meters;source_records=[]
  for geo,t,tag,attrs in selected:
   copy=geo.Duplicate();copy.Transform(t);archive.Objects.Add(copy,attrs);source_records.append({'source':tag,'bounds':str(copy.GetBoundingBox(True))});copy.Dispose()
  archive.Write(str(out/'unset-native-slot.3dm'),8);archive.Dispose()
  data={'sources':source_records,'raw':dict(counts),'indices':bad,'plane':descriptor['plane'],'clips':[{'origin':[c.OriginX,c.OriginY,c.OriginZ],'normal':[c.Normal.X,c.Normal.Y,c.Normal.Z]} for c in clips],'camera':{'location':[vp.CameraLocation.X,vp.CameraLocation.Y,vp.CameraLocation.Z],'direction':[vp.CameraDirection.X,vp.CameraDirection.Y,vp.CameraDirection.Z],'up':[vp.CameraUp.X,vp.CameraUp.Y,vp.CameraUp.Z],'distance':distance},'archiveSha256':hashlib.sha256((out/'unset-native-slot.3dm').read_bytes()).hexdigest()};print(json.dumps(data),flush=True);(out/'extracted.json').write_text(json.dumps(data,indent=2));vp.Dispose();model.Dispose()
