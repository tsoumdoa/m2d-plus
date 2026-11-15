"""Prepare complete small uncut planar cases for the independent rational oracle.
Does not modify the whole-output gate or accept exceptions.
"""
import json,sys,subprocess
from pathlib import Path
import rhino3dm as R
ROOT=Path(__file__).resolve().parents[2]
folder=Path(sys.argv[1]);exe=ROOT/'tools/routing-audit/bin/Release/net8.0/M2DPlus.RoutingAudit.dll'
for gatefile in sorted(folder.glob('*.gate.json')):
 gate=json.loads(gatefile.read_text());prefix=str(gatefile)[:-10]
 if gate['passed'] or gate['NativeCalls']!=0 or 'section' in gate['view'].lower():continue
 sf=Path(prefix+'.planar-scene.json')
 if not sf.exists():continue
 scene=json.loads(sf.read_text())
 if len(scene['Edges'])>4096:continue
 actual=[]
 for piece in json.loads(Path(prefix+'.kernel-output.json').read_text())['Pieces']:
  if piece['Class'] not in (0,1,2):continue
  g=R.CommonObject.Decode(json.loads(piece['Geometry']))
  if not isinstance(g,R.Curve):continue
  a,b=g.PointAtStart,g.PointAtEnd
  def xy(p):
   delta=[getattr(p,k)-scene['origin'][i] for i,k in enumerate(('X','Y','Z'))]
   return [sum(delta[i]*scene[k][i] for i in range(3)) for k in ('x','y')]
  x,y=xy(a),xy(b)
  actual.append(dict(X0=x[0],Y0=x[1],X1=y[0],Y1=y[1],Class=piece['Class'],Source=piece['Source']))
 inputfile=Path(prefix+'.reference-input.json');out=Path(prefix+'.reference.json')
 inputfile.write_text(json.dumps(dict(Faces=scene['Faces'],Edges=scene['Edges'],Actual=actual,Tolerance=gate['tolerance'],Spacing=gate['spacing'])))
 subprocess.run(['dotnet',str(exe),str(inputfile),str(out)],check=False)
