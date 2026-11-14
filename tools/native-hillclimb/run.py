"""Full-set, serial owned-worker native HLD hill climb. No Rhino process-name operations.

Run with the existing Python venv. Full manifests are immutable per run; failed,
refused, cancelled and unvisited rows remain failures, never disappear from score.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import statistics
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/models'))
from supervise import run

ORTH = Path('C:/Users/tomosandego/.t3/worktrees/m2d-plus/t3-3a78edd1-1cd89a1b1b9b38a1/artifacts/models')
CURVED = Path('C:/Users/tomosandego/.t3/worktrees/m2d-plus/t3-d6bdd8e8-f1e6b3c68a0e104e/artifacts/models')
RESOLVER = Path('C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/.venv/Lib/site-packages/rhinoinside/Rhino.Inside_v8_net8.0.dll')
EXE = Path(os.environ.get('M2D_NATIVE_BENCH_EXE', str(ROOT / 'tools/native-hillclimb/bin/Release/net8.0-windows/NativeHillclimb.exe')))
OUT = ROOT / 'artifacts/native-hillclimb'
MODES = ['baseline', 'threads', 'cull', 'boxmesh']
VIEWS = ['North elevation', 'SW axo', 'Top', 'Section long overlap']


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(path.suffix + '.tmp')
    temp.write_text(json.dumps(value, indent=2, allow_nan=False) + '\n')
    temp.replace(path)


def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for block in iter(lambda: f.read(2**20), b''): h.update(block)
    return h.hexdigest()


def manifest():
    files = []
    for directory, prefix in [(ORTH, ''), (CURVED, 'curved-')]:
        for tier in ['S', 'M', 'L', 'XL']:
            for flavor in ['blocks', 'exploded']:
                files.append((directory / f'{prefix}perf-{tier}-{flavor}.3dm', True))
    files += [(f, True) for f in sorted((ROOT / 'tests/fixtures').rglob('*.3dm'))]
    files += [(ORTH / f'perf-XXL-{f}.3dm', False) for f in ['blocks', 'exploded']]
    return [{'id': f'{i:03d}-{f.stem}', 'path': str(f), 'sha256': digest(f), 'primary': primary,
             'rows': [{'view': view, 'hidden': hidden, 'key': f'v{j}-h{str(hidden)}'} for hidden in [True, False] for j, view in enumerate(VIEWS)]}
            for i, (f, primary) in enumerate(files)]


def score(iteration, models):
    mode = MODES[iteration]; records = []; peak = 0; cold = []; warm = []; control_cold = []; control_warm = []
    failures = []; completed = 0; gate_pass = 0
    for model in models:
        output = OUT / f'iteration-{iteration}' / model['id']
        identity_path = output / 'identity.json'; identity = json.loads(identity_path.read_text()) if identity_path.exists() else {}
        watcher_path = output / 'watch.json'; watcher = json.loads(watcher_path.read_text()) if watcher_path.exists() else {}
        peak = max(peak, watcher.get('jobPeakMemoryBytes', 0) + watcher.get('supervisorPeakPrivateBytes', 0), watcher.get('peakPrivateBytes', 0))
        overhead = identity.get('startup', 0) + identity.get('captureSeconds', 0)
        for row in model['rows']:
            path = output / (row['key'] + '.json'); data = json.loads(path.read_text()) if path.exists() else None
            ok = watcher.get('status') == 'exited' and data is not None and bool(data.get('rows')) and all(not r.get('error') for r in data['rows'])
            if ok and iteration:
                ok = all(r.get('check', {}).get('Passed', False) and not r.get('baselineError') and not r.get('candidateError') for r in data['rows'])
            elif ok:
                ok = all(not r.get('Error') for r in data['rows'])
            a = statistics.median(data['baselineTimes']) if data and data['baselineTimes'] else None
            b = statistics.median(data['candidateTimes']) if data and data['candidateTimes'] else None
            if iteration == 0: b = a
            if data: completed += 1
            if ok: gate_pass += 1
            else: failures.append({'model': model['id'], **row, 'artifact': str(path)})
            if model['primary'] and a and b and ok:
                warm.append(b); cold.append(b + overhead / 6)
                control_warm.append(a); control_cold.append(a + overhead / 6)
            records.append({'model': model['id'], **row, 'primary': model['primary'], 'gate': ok, 'baseline': a, 'candidate': b,
                            'productAdmission': identity.get('productAdmission'), 'supervisorStatus': watcher.get('status')})
    primary_total = sum(len(m['rows']) for m in models if m['primary'])
    full_primary = len(warm) == primary_total
    license_file = OUT/'licensed.json'
    licensed = json.loads(license_file.read_text()) if license_file.exists() else {'passed':None}
    primary_passed = sum(r['gate'] for r in records if r['primary'])
    gm = lambda x: math.exp(statistics.mean(map(math.log, x))) if x else None
    result = {'iteration': iteration, 'hypothesis': ['Current monolithic native', 'Internal threading with stable canonical order and exact-copy removal', 'Certified whole-box occlusion hidden-off and outside-crop culling both modes', 'Exactly recognized AABB Breps pre-meshed into closed six-quad boxes'][iteration],
              'mode': mode, 'totalRows': len(records), 'completedRows': completed, 'gatePassedRows': gate_pass,
              'primaryTotalRows':primary_total, 'primaryGatePassedRows':primary_passed, 'licensedTests':licensed,
              'gate': full_primary and all(r['gate'] for r in records if r['primary']) and (iteration == 0 or licensed.get('passed') is True),
              'coldGeomeanSeconds': gm(cold) if full_primary else None,
              'warmGeomeanSeconds': gm(warm) if full_primary else None,
              'controlColdGeomeanSeconds': gm(control_cold) if full_primary else None,
              'controlWarmGeomeanSeconds': gm(control_warm) if full_primary else None,
              'diagnosticCompletedWarmGeomeanSeconds': gm(warm), 'peakAggregateWorkerJobBytes': peak,
              'memoryNote': 'Owned worker tree hard-capped at 15.875 GiB, plus supervisor private memory monitored within 128 MiB reserve; sampled aggregate includes both. No interactive host involved.',
              'coldDefinition': 'startup + capture amortised over six views, plus per-view median. OS disk cache uncontrolled.',
              'warmDefinition': 'retained captured scene; full native visibility recomputed (no result cache).',
              'xxl': [r for r in records if not r['primary']], 'failures': failures, 'rows': records, 'kept': False}
    if iteration and result['gate']:
        result['kept'] = result['coldGeomeanSeconds'] < result['controlColdGeomeanSeconds'] and result['warmGeomeanSeconds'] < result['controlWarmGeomeanSeconds']
    write(OUT / f'iteration-{iteration}' / 'score.json', result)
    return result


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--iteration', type=int, choices=range(4), required=True)
    p.add_argument('--pilot', action='store_true', help='Diagnostic only: first root box; never logs a shared score')
    p.add_argument('--resume', action='store_true')
    args = p.parse_args(); OUT.mkdir(parents=True, exist_ok=True)
    mpath = OUT / 'manifest.json'
    if mpath.exists(): models = json.loads(mpath.read_text())
    else: models = manifest(); write(mpath, models)
    if args.pilot: models = [next(m for m in models if m['path'].endswith('fixtures\\brep_box.3dm'))]
    iteration = args.iteration; mode = MODES[iteration]
    identity_path = OUT / (f'pilot-{iteration}' if args.pilot else f'iteration-{iteration}') / 'run-identity.json'
    sources = list(Path(__file__).parent.glob('*.*')) + list((ROOT/'src/M2DPlus.Rhino').rglob('*.cs'))
    hashes = {str(f.relative_to(ROOT)): digest(f) for f in sources if 'bin' not in f.parts and 'obj' not in f.parts}
    hashes.update({str(EXE):digest(EXE), str(EXE.with_suffix('.dll')):digest(EXE.with_suffix('.dll')), 'Core.dll':digest(EXE.parent/'M2DPlus.Core.dll')})
    write(identity_path, {'hashes':hashes,'utc':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),'mode':mode})
    for model in models:
        output = OUT / (f'pilot-{iteration}' if args.pilot else f'iteration-{iteration}') / model['id']; output.mkdir(parents=True, exist_ok=True)
        if args.resume and (output / 'watch.json').exists():
            old_identity = output/'identity.json'
            if old_identity.exists() and json.loads(old_identity.read_text())['engineSha256'].lower() != digest(EXE.with_suffix('.dll')).lower():
                raise RuntimeError('Refusing resume across different executable hashes: '+str(output))
            continue
        print(f"START {iteration} {model['id']}", flush=True)
        if digest(Path(model['path'])) != model['sha256']:
            write(output / 'watch.json', {'status':'manifest_hash_changed'}); continue
        watcher = run([str(EXE), str(RESOLVER), model['path'], str(output), mode], str(output / 'worker.log'), 21600, 16)
        write(output / 'watch.json', watcher)
        print(f"END {iteration} {model['id']} {watcher['status']} {watcher['wallSeconds']:.2f}s", flush=True)
    if args.pilot: return
    result = score(iteration, models)
    # Compact append-only shared score; detailed rows stay with the evidence.
    compact = {k: v for k, v in result.items() if k not in ['rows', 'failures']}
    log = ROOT / 'hillclimb/scores.jsonl'
    with log.open('a') as f: f.write(json.dumps(compact, allow_nan=False) + '\n')
    md = ROOT/'HILLCLIMB.md'
    lines = md.read_text().splitlines()
    for i,line in enumerate(lines):
        if line.startswith(f'| {iteration} |'):
            cells=line.split('|')
            cells[4] = f" **{'PASS' if result['gate'] else 'FAIL'}**, {result['primaryGatePassedRows']}/{result['primaryTotalRows']} primary rows "
            cells[5] = f" {result['coldGeomeanSeconds']:.4f} / {result['warmGeomeanSeconds']:.4f} s " if result['coldGeomeanSeconds'] else ' null / null; full-set score unavailable '
            cells[6] = f" {result['peakAggregateWorkerJobBytes']/1024**3:.3f} GiB; XXL {sum(r['gate'] for r in result['xxl'])}/16 rows "
            cells[7] = ' Baseline only ' if iteration == 0 else (' Yes ' if result['kept'] else ' No; isolated candidate rejected ')
            lines[i]='|'.join(cells)
    md.write_text('\n'.join(lines)+'\n')
    print(json.dumps(compact), flush=True)

if __name__ == '__main__': main()

