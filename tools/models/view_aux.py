"""Queued, owned auxiliary view-case captures and real GH component checks. Local only."""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import time
from common import ROOT,SerialRhino,digest,initialize,write_json
from owned_job import run


def worker(args):
    if sys.stdin.readline().strip()!='go': raise RuntimeError('Use owned supervisor')
    admission=args.output/'admission.json'; write_json(admission,{'status':'waiting'})
    with SerialRhino(wait_seconds=None):
        write_json(admission,{'status':'admitted','admittedPerfCounter':time.perf_counter()})
        if args.mode=='capture':
            initialize(); import clr
            dll=ROOT/'tools/models/renderer/bin/Release/net8.0-windows/M2DPlus.ModelRenderer.dll'; clr.AddReference(str(dll))
            from M2DPlus.ModelRenderer import Renderer
            result=json.loads(Renderer.Render(str(args.model),str(args.model.with_suffix('.json')),str(args.output)))
            result.update(rendererSha256=digest(dll),fixtureSha256=digest(args.model))
            write_json(args.output/(args.model.stem+'.capture.json'),result)
        else:
            dll=ROOT/'artifacts/view-component-bin/M2DPlus.Render.dll'
            resolver=Path(sys.executable).parents[1]/'Lib/site-packages/rhinoinside/Rhino.Inside_v8_net8.0.dll'
            gha=ROOT/'src/M2DPlus.Grasshopper/bin/Release/net8.0-windows/M2DPlus.Grasshopper.gha'
            subprocess.run(['dotnet',str(dll),str(resolver),str(ROOT),str(args.output),'view-component',str(args.model),args.view,str(gha)],
                           check=True,creationflags=subprocess.CREATE_NO_WINDOW)


def main():
    p=argparse.ArgumentParser(description=__doc__); p.add_argument('mode',choices=['capture','component'])
    p.add_argument('--model',type=Path); p.add_argument('--view'); p.add_argument('--output',type=Path)
    p.add_argument('--worker',action='store_true'); args=p.parse_args()
    if args.worker: worker(args); return
    if args.mode=='capture':
        tasks=[(f,None,ROOT/'artifacts/view-models/screenshots') for f in sorted((ROOT/'artifacts/view-models').glob('*.3dm'))]
    else:
        chosen=[('view-planar-control','Planar oblique 17'),('view-planar-control','Planar oblique 45'),
                ('view-curved-steel','Normal CHS-arc station 3'),('view-curved-steel','Normal twisted-RHS station 3'),
                ('view-round-tower','Radial 015 forward'),('view-round-tower','Radial 015 reverse')]
        from view_report import slug
        tasks=[(ROOT/'artifacts/view-models'/(m+'.3dm'),v,ROOT/'artifacts/baseline/view-components'/(m+'__'+slug(v))) for m,v in chosen]
    for model,view,out in tasks:
        out.mkdir(parents=True,exist_ok=True); admission=out/'admission.json'; admission.unlink(missing_ok=True)
        command=[sys.executable,'-B','-u',__file__,args.mode,'--worker','--model',str(model),'--output',str(out)]
        if view: command+=['--view',view]
        print('start',args.mode,model.stem,view,flush=True)
        watch=run(command,out/(model.stem+'.log'),600,8,admission_path=admission,admission_timeout=3600)
        write_json(out/(model.stem+'.watch.json'),watch)
        print('done',watch['status'],watch['wallSeconds'],flush=True)
        time.sleep(5)


if __name__=='__main__': main()
