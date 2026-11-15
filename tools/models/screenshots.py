"""Capture 1200x800 shaded references in a dedicated, hidden, owned Rhino.exe."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys

from common import ROOT, SerialRhino, digest, initialize, write_json


def child(args):
    if sys.stdin.readline().strip() != 'go':
        raise RuntimeError('Use the capture supervisor')
    with SerialRhino():
        if args.offscreen:
            request = json.loads(args.request.read_text())
            initialize()
            import clr
            assembly = Path(__file__).parent / 'renderer/bin/Release/net8.0-windows/M2DPlus.ModelRenderer.dll'
            clr.AddReference(str(assembly.resolve()))
            from M2DPlus.ModelRenderer import Renderer
            result = json.loads(Renderer.Render(request['model'], request['sidecar'], request['out']))
            result['rendererSha256'] = digest(assembly)
            write_json(request['result'], result)
            return
        info = subprocess.STARTUPINFO()
        info.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        info.wShowWindow = 0
        macro = '_-RunPythonScript "' + str(Path(__file__).with_name('capture_rhino.py')).replace('\\', '/') + '"'
        env = dict(os.environ, M2DPLUS_CAPTURE_REQUEST=str(args.request.resolve()))
        result = subprocess.run([args.rhino, '/nosplash', '/notemplate', '/netcore-8', '/runscript=' + macro],
                                env=env, startupinfo=info)
        raise SystemExit(result.returncode)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--models', type=Path, default=ROOT / 'artifacts/models')
    p.add_argument('--out', type=Path, default=ROOT / 'artifacts/models/screenshots')
    p.add_argument('--select', help='Comma-separated model IDs; default all models')
    p.add_argument('--rhino', default='C:/Program Files/Rhino 8/System/Rhino.exe')
    p.add_argument('--wall', type=float, default=600)
    p.add_argument('--memory', type=float, default=16)
    p.add_argument('--resume', action='store_true')
    p.add_argument('--offscreen', action='store_true', help='Use the explicit CPU mesh fallback; build renderer first')
    p.add_argument('--child', action='store_true', help=argparse.SUPPRESS)
    p.add_argument('--request', type=Path, help=argparse.SUPPRESS)
    args = p.parse_args()
    if args.child:
        child(args); return
    from owned_job import run
    args.out.mkdir(parents=True, exist_ok=True)
    renderer = Path(__file__).parent / 'renderer/bin/Release/net8.0-windows/M2DPlus.ModelRenderer.dll'
    renderer_hash = digest(renderer) if args.offscreen else None
    failed = []
    for path in sorted(args.models.glob('*.json')):
        meta = json.loads(path.read_text(encoding='utf-8'))
        if 'views' not in meta or (args.select and meta['id'] not in args.select.split(',')):
            continue
        captures = ['SW', 'Front'] + (['Top'] if meta['tier'] else [])
        target = args.out / (meta['id'] + '.capture.json')
        if args.resume and target.exists():
            previous = json.loads(target.read_text())
            if (previous.get('fixtureSha256') == meta['sha256'] and previous.get('sidecarSha256') == digest(path)
                    and previous.get('rendererSha256') == renderer_hash and previous.get('status') == 'ok'
                    and all(Path(p).exists() for p in previous['paths'])):
                continue
        request = {'id': meta['id'], 'model': str(path.with_suffix('.3dm').resolve()), 'sidecar': str(path.resolve()), 'views': meta['views'],
                   'captures': captures, 'objects': meta['stats']['objects'], 'out': str(args.out.resolve()), 'result': str(target.resolve())}
        request_path = args.out / (meta['id'] + '.request.json'); write_json(request_path, request)
        # A failed fresh capture must never inherit an earlier successful result.
        target.unlink(missing_ok=True)
        print('capture', meta['id'], flush=True)
        watch = run([sys.executable, '-B', '-u', str(Path(__file__).resolve()), '--child', '--request', str(request_path), '--rhino', args.rhino] + (['--offscreen'] if args.offscreen else []),
                    args.out / (meta['id'] + '.capture.log'), args.wall, args.memory)
        result = json.loads(target.read_text()) if target.exists() else {'status': 'no_capture', 'model': meta['id']}
        result.update(fixtureSha256=meta['sha256'], sidecarSha256=digest(path), watchdog=watch)
        if result['status'] != 'ok':
            failed.append(meta['id'])
        write_json(target, result)
        print(meta['id'], result['status'], round(watch['wallSeconds'], 2), flush=True)
    if failed:
        raise SystemExit('Capture failed: ' + ', '.join(failed))


if __name__ == '__main__':
    main()
