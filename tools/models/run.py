"""Serial local baseline controller and isolated one-view worker."""
from __future__ import annotations

import argparse
import hashlib
import importlib
import json
from pathlib import Path
import platform
import re
import sys
import time
import traceback

from common import ROOT, SerialRhino, digest, initialize, write_json


def worker(args):
    out = Path(args.result)
    base = {'model': args.model.stem, 'view': args.view, 'multipleThreads': args.threads == 'on',
            'engine': args.engine, 'status': 'starting', 'workerCount': 1, 'route': 'owned Rhino.Inside process',
            'cacheState': 'none', 'temperature': 'cold process and scene',
            'fixtureSha256': digest(args.model), 'sidecarSha256': digest(args.model.with_suffix('.json')),
            'harnessHashes': {p.name: digest(p) for p in Path(__file__).parent.glob('*.py')},
            'utc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}
    base['coordination'] = {'session': args.coordination_note, 'admissionWaitSeconds': args.admission_wait,
                            'mutexScope': 'one view; released before the next worker',
                            'timingQualification': 'Indicative shared-session timing; other headless work was not monitored.'}
    write_json(out, base)
    def phase(name, extra=None):
        base.update(phase=name, **(extra or {})); write_json(out, base)
    model = None
    start = time.perf_counter(); cpu = time.process_time()
    try:
        phase('initialize'); R, S = initialize()
        import clr
        dll = Path(str(clr.GetClrType(R.Geometry.HiddenLineDrawing).Assembly.Location))
        base.update(rhinoBuild=str(R.RhinoApp.Version), runtime=str(S.Environment.Version),
                    architecture=platform.machine(), engineSha256=digest(dll), startupSeconds=time.perf_counter() - start)
        # .NET hosts place RhinoCommon in System/netcore, below native binaries.
        native_dir = next((d for d in (dll.parent, dll.parent.parent) if (d / 'Rhino.exe').exists()), dll.parent)
        base['nativeBinaryHashes'] = {p.name: digest(p) for p in (native_dir / 'Rhino.exe', native_dir / 'rhcommon_c.dll') if p.exists()}
        sidecar = json.loads(args.model.with_suffix('.json').read_text(encoding='utf-8'))
        if digest(args.model) != sidecar['sha256']:
            raise RuntimeError('Model differs from its sidecar SHA256')
        base.update(units=sidecar['units'], tolerance=sidecar['tolerance'], stats=sidecar['stats'],
                    tier=sidecar['tier'], flavor=sidecar['flavor'])
        descriptor = next(v for v in sidecar['views'] if v['name'] == args.view)
        base['viewDescriptor'] = descriptor
        base['oracleSha256'] = hashlib.sha256(json.dumps({'model': sidecar['expected'], 'view': descriptor.get('expected', {})}, sort_keys=True).encode()).hexdigest()
        phase('read'); t = time.perf_counter()
        model = R.FileIO.File3dm.Read(str(args.model.resolve()))
        if model is None:
            raise RuntimeError('File3dm.Read returned null')
        base['readSeconds'] = time.perf_counter() - t
        adapter = importlib.import_module(args.engine)
        if hasattr(adapter, 'dependency_hashes'):
            base['adapterHashes'] = adapter.dependency_hashes()
        result, svg = adapter.run(model, sidecar, descriptor, args.threads == 'on', phase)
        out.with_suffix('.svg').write_text(svg, encoding='utf-8')
        base.update(result)
        base.update(phase='complete', cpuSeconds=time.process_time() - cpu, workerWallSeconds=time.perf_counter() - start,
                    firstPreviewSeconds=None, completeResultSeconds=time.perf_counter() - start)
        write_json(out, base)
    except Exception as e:
        base.update(status='exception', error=str(e), traceback=traceback.format_exc(), workerWallSeconds=time.perf_counter() - start)
        write_json(out, base); raise
    finally:
        if model:
            model.Dispose()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--models', type=Path, default=ROOT / 'artifacts/models')
    parser.add_argument('--out', type=Path, default=ROOT / 'artifacts/baseline')
    parser.add_argument('--features', action='store_true')
    parser.add_argument('--tiers', default='XS,S,M,L')
    parser.add_argument('--select', help='Comma-separated exact model IDs')
    parser.add_argument('--views', help='Comma-separated exact view names')
    parser.add_argument('--engine', default='native', help='Python adapter module exposing run(); native by default')
    parser.add_argument('--wall', type=float, default=600)
    parser.add_argument('--memory', type=float, default=8, help='Hard aggregate private-memory cap, GiB')
    parser.add_argument('--resume', action='store_true', help='Skip only completed matching-input rows, including honestly capped attempts')
    parser.add_argument('--xl-once', action='store_true', help='One high-overlap XL view per flavor; always capped at 600s')
    parser.add_argument('--worker', action='store_true', help=argparse.SUPPRESS)
    parser.add_argument('--model', type=Path, help=argparse.SUPPRESS)
    parser.add_argument('--view', help=argparse.SUPPRESS)
    parser.add_argument('--threads', choices=['on', 'off'], help='Override standard threading matrix')
    parser.add_argument('--result', help=argparse.SUPPRESS)
    parser.add_argument('--yield-seconds', type=float, default=30, help='Mutex-free handoff between views; outside measured worker wall time')
    parser.add_argument('--admission-timeout', type=float, default=3600, help='Maximum queued mutex wait, excluded from the execution cap')
    parser.add_argument('--coordination-note', default='shared local research session')
    parser.add_argument('--admission', type=Path, help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.worker:
        if sys.stdin.readline().strip() != 'go':
            raise RuntimeError('Worker must be started by the owned-job supervisor')
        # A live controller can request a stop at a model boundary. This gate
        # runs before Rhino initialization or mutex acquisition, never mid-solve.
        control = Path(args.result).parent / 'coordination-control.json'
        admission = args.admission or Path(args.result).with_suffix('.admission.json')
        queued = time.perf_counter()
        write_json(admission, {'status': 'waiting', 'modelId': args.model.stem})
        while control.exists() and args.model.stem in json.loads(control.read_text()).get('pauseBeforeModels', []):
            write_json(admission, {'status': 'handoff_paused', 'modelId': args.model.stem})
            time.sleep(1)
        with SerialRhino(wait_seconds=None):
            admitted = time.perf_counter()
            args.admission_wait = admitted - queued
            write_json(admission, {'status': 'admitted', 'modelId': args.model.stem,
                                  'admittedPerfCounter': admitted, 'mutexWaitSeconds': args.admission_wait,
                                  'utc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())})
            worker(args)
        return
    from owned_job import run
    args.out.mkdir(parents=True, exist_ok=True)
    rows = []
    if args.yield_seconds < 0 or args.admission_timeout <= 0:
        parser.error('Yield must be nonnegative and admission timeout positive')
    for side_path in sorted(args.models.glob('*.json')):
        meta = json.loads(side_path.read_text(encoding='utf-8'))
        if 'views' not in meta:
            continue
        selected = meta['id'] in args.select.split(',') if args.select else (
            (args.features and meta['tier'] is None) or meta['tier'] in args.tiers.split(',') or (args.xl_once and meta['tier'] == 'XL'))
        if not selected:
            continue
        for v in meta['views']:
            if args.views and v['name'] not in args.views.split(','):
                continue
            if args.xl_once and meta['tier'] == 'XL' and v['name'] != 'Section long overlap':
                continue
            modes = [args.threads] if args.threads else (['off', 'on'] if meta['tier'] in ('XS', 'S', 'M') else ['off'])
            for threads in modes:
                slug = re.sub(r'[^a-zA-Z0-9_-]+', '-', v['name']).strip('-')
                name = f"{meta['id']}__{slug}__{threads}__{args.engine.replace('.', '-')}"
                target = args.out / (name + '.json')
                watch_path = args.out / (name + '.watch.json')
                admission_path = args.out / (name + '.admission.json')
                wall = min(args.wall, 600) if meta['tier'] == 'XL' else args.wall
                if args.resume and target.exists() and watch_path.exists():
                    existing = json.loads(target.read_text())
                    watch = json.loads(watch_path.read_text())
                    modules = ['common.py', 'owned_job.py', 'run.py', args.engine.replace('.', '/') + '.py']
                    code_matches = all(existing.get('harnessHashes', {}).get(n) == digest(Path(__file__).parent / n)
                                       for n in modules if (Path(__file__).parent / n).exists())
                    adapter = importlib.import_module(args.engine)
                    if hasattr(adapter, 'dependency_hashes'):
                        code_matches = code_matches and existing.get('adapterHashes') == adapter.dependency_hashes()
                    caps_match = watch.get('limits') == {'wallSeconds': wall, 'privateGiB': args.memory}
                    if code_matches and caps_match and existing.get('fixtureSha256') == meta['sha256'] and existing.get('sidecarSha256') == digest(side_path) and (existing.get('phase') == 'complete' or watch['status'] in ('wall_timeout', 'private_memory_limit')):
                        print('resume', name, flush=True); continue
                command = [sys.executable, '-B', '-u', str(Path(__file__).resolve()), '--worker', '--model', str(side_path.with_suffix('.3dm')),
                           '--view', v['name'], '--threads', threads, '--result', str(target), '--engine', args.engine,
                           '--admission', str(admission_path), '--coordination-note', args.coordination_note]
                # A failed fresh attempt cannot reuse an older drawing or status.
                # Adapter checkpoints must not survive a fresh, possibly capped attempt.
                suffixes = ('.json', '.svg', '.png', '.watch.json', '.admission.json',
                            '.native.json', '.native-loci.json', '.native.svg', '.m2d.json',
                            '.m2d-loci.json', '.planar-loci.json', '.engine-phase.json',
                            '.engine-phase.json.tmp', '.agreement.json', '.audit.json',
                            '.native-metamorphic.json', '.m2d-metamorphic.json',
                            '.analytic-rays.json', '.profile.svg', '.profile.png')
                for old in (target.with_suffix(suffix) for suffix in suffixes):
                    old.unlink(missing_ok=True)
                print('start', name, flush=True)
                watch = run(command, target.with_suffix('.log'), wall, args.memory,
                            admission_path=admission_path, admission_timeout=args.admission_timeout)
                write_json(watch_path, watch)
                result = json.loads(target.read_text()) if target.exists() else {'model': meta['id'], 'view': v['name'], 'status': 'no_result'}
                # Keep failed startup attempts in their tier's denominator too.
                for key, value in {'tier': meta['tier'], 'flavor': meta['flavor'], 'stats': meta['stats'],
                                   'engine': args.engine, 'multipleThreads': threads == 'on',
                                   'sidecarSha256': digest(side_path)}.items():
                    result.setdefault(key, value)
                if 'fixtureSha256' not in result:
                    result['fixtureSha256'] = digest(side_path.with_suffix('.3dm'))
                if watch['status'] != 'exited':
                    result.update(status=watch['status'], lastWorkerStatus=result.get('status'))
                result.update(wallSeconds=watch['wallSeconds'], elapsedSeconds=watch['elapsedSeconds'],
                              admissionSeconds=watch['admissionSeconds'], peakPrivateBytes=watch['peakPrivateBytes'], jobPeakMemoryBytes=watch['jobPeakMemoryBytes'])
                write_json(target, result); rows.append(result)
                print('done', name, result['status'], round(watch['wallSeconds'], 3), round(watch['peakPrivateBytes'] / 1024**3, 3), flush=True)
                if args.yield_seconds:
                    print('handoff: mutex free for', args.yield_seconds, 'seconds', flush=True)
                    time.sleep(args.yield_seconds)
    print('completed attempts', len(rows), flush=True)


if __name__ == '__main__':
    main()
