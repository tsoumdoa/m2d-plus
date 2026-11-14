"""Owned serial PERF-A runner. Full manifest is immutable for a scored iteration."""
from pathlib import Path
import argparse
import json
import math
import subprocess
import sys
import time
import traceback

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/models'))
from common import SerialRhino, initialize, digest, write_json

DEFAULT_MODELS = Path('C:/Users/tomosandego/.t3/worktrees/m2d-plus/t3-3a78edd1-1cd89a1b1b9b38a1/artifacts/models')


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--models', type=Path, default=DEFAULT_MODELS)
    p.add_argument('--iteration', type=int, default=0)
    p.add_argument('--summary', default='Filtered exact interval kernel, BVH, rational oracle')
    p.add_argument('--worker', action='store_true')
    p.add_argument('--model', type=Path)
    p.add_argument('--view')
    p.add_argument('--output', type=Path)
    p.add_argument('--oracle', action='store_true')
    p.add_argument('--native', action='store_true')
    p.add_argument('--select', help='Diagnostic only; cannot create a scored full run')
    p.add_argument('--wall', type=float, default=1800)
    p.add_argument('--memory', type=float, default=16)
    p.add_argument('--threads', type=int, default=8)
    p.add_argument('--licence-free', action='store_true', help='Native gate remains failed; no Rhino licence or mutex used')
    p.add_argument('--reader-bin', type=Path, default=ROOT / 'tools/hc-perf/reader/bin/Release/net8.0-windows/M2DPlus.HcPerf.Reader.dll')
    args = p.parse_args()
    if args.worker:
        if sys.stdin.readline().strip() != 'go':
            raise RuntimeError('Owned-job handshake required')
        with SerialRhino():
            R, S = initialize()
            import clr
            clr.AddReference(str(ROOT / 'tools/hc-perf/bin/Release/net8.0-windows/M2DPlus.HcPerf.dll'))
            from M2DPlus.HcPerf import Bench
            try:
                Bench.Run(str(args.model), args.view, str(args.output), args.oracle, args.native, 3, args.threads)
            except Exception:
                write_json(str(args.output) + '.json', {'gate': {'passed': False, 'failures': [traceback.format_exc()]}})
                raise
        return
    from owned_job import run
    output = ROOT / f'artifacts/hc-perf/iteration-{args.iteration}'
    output.mkdir(parents=True, exist_ok=True)
    manifest = []
    for tier in ('S', 'M', 'L', 'XL', 'XS'):
        for form in ('blocks', 'exploded'):
            for view in ('North', 'SW', 'Top'):
                manifest.append({'path': str(args.models / f'perf-{tier}-{form}.3dm'), 'view': view,
                                 'oracle': tier in ('XS', 'S'), 'native': tier in ('M', 'L'), 'primary': tier != 'XS'})
    for path in sorted((ROOT / 'tests/fixtures').glob('*.3dm')):
        for view in ('Top', 'Front', 'SW'):
            manifest.append({'path': str(path), 'view': view, 'oracle': True, 'native': True, 'primary': True})
    for path in sorted(args.models.glob('*.3dm')):
        if path.name.startswith('perf-'):
            continue
        for view in ('Top', 'Front', 'SW'):
            manifest.append({'path': str(path), 'view': view, 'oracle': True, 'native': True, 'primary': True})
    write_json(output / 'manifest.json', manifest)
    rows = []
    for index, case in enumerate(manifest):
        if args.select and args.select not in Path(case['path']).stem:
            continue
        target = output / f"{Path(case['path']).stem}__{case['view']}"
        for suffix in ('.json', '.loci.json', '.native.json', '.mixed-native.json'):
            Path(str(target) + suffix).unlink(missing_ok=True)
        if args.licence_free:
            command = ['dotnet', str(args.reader_bin),
                       case['path'], case['view'], str(target)]
            if case['oracle']: command.append('--oracle')
        else:
            command = [sys.executable, '-B', '-u', str(Path(__file__).resolve()), '--worker', '--model', case['path'],
                       '--view', case['view'], '--output', str(target), '--threads', str(args.threads)]
            if case['oracle']: command.append('--oracle')
            if case['native']: command.append('--native')
        print(f"[{index+1}/{len(manifest)}] {target.stem}", flush=True)
        watch = run(command, str(target) + '.log', args.wall, args.memory)
        write_json(str(target) + '.watch.json', watch)
        path = Path(str(target) + '.json')
        row = json.loads(path.read_text()) if path.exists() else {'gate': {'passed': False, 'failures': ['worker incomplete']}}
        if watch['status'] != 'exited':
            row['gate']['passed'] = False
            row['gate']['failures'].append('owned worker: ' + watch['status'])
        if args.licence_free and case['native']:
            row['gate']['passed'] = False
            row['gate']['failures'].append('native gate unavailable: licence-free run')
        row['case'] = case
        rows.append(row)
        # Always checkpoint the score, including missing cases. An interrupted run never looks passed.
        score(args, manifest, rows, output, complete=False)
    score(args, manifest, rows, output, complete=not args.select)


def score(args, manifest, rows, output, complete):
    failures = []
    if not complete or len(rows) != len(manifest): failures.append(f'full benchmark incomplete: {len(rows)}/{len(manifest)}')
    for r in rows:
        failures.extend(f"{Path(r['case']['path']).stem}/{r['case']['view']}: {f}" for f in r['gate']['failures'])
    # Blocks/exploded locus gate is evaluated from both sets, not just aggregate lengths.
    for tier in ('XS', 'S', 'M', 'L', 'XL'):
        for view in ('North', 'SW', 'Top'):
            a, b = (output / f'perf-{tier}-{form}__{view}.loci.json' for form in ('blocks', 'exploded'))
            if complete and a.exists() and b.exists():
                meta = json.loads((args.models / f'perf-{tier}-blocks.json').read_text())
                tolerance = meta['tolerance']
                units = meta['units'].lower()
                unit = 1000 if 'millimeter' in units else 3.280839895013123 if 'feet' in units else 1
                comparison = subprocess.run(['dotnet', str(args.reader_bin),
                                             '--compare', str(a), str(b), str(tolerance), str(.05 * unit)], capture_output=True, text=True)
                try:
                    report = json.loads(comparison.stdout)
                    write_json(output / f'pair-{tier}-{view}.json', report)
                    if not report['passed']: failures.append(f'blocks/exploded locus disagreement {tier}/{view}')
                except (ValueError, KeyError): failures.append(f'blocks/exploded comparison failed {tier}/{view}: {comparison.stderr}')
            elif complete:
                failures.append(f'blocks/exploded gate missing {tier}/{view}')
    if complete:
        for form in ('blocks', 'exploded'):
            for view in ('North', 'SW'):
                tile_path = output / f'perf-M-{form}__{view}.tiles.json'
                model = args.models / f'perf-M-{form}.3dm'
                command = ['dotnet', str(args.reader_bin), '--tiles',
                           str(model), view, str(tile_path)]
                from owned_job import run
                watch = run(command, str(tile_path) + '.log', args.wall, args.memory)
                write_json(str(tile_path) + '.watch.json', watch)
                if not tile_path.exists() or not json.loads(tile_path.read_text())['passed']:
                    failures.append(f'M 20m tile oracle incomplete/failed {form}/{view}')
                rigid_path = output / f'perf-M-{form}__{view}.rigid.json'
                command = ['dotnet', str(args.reader_bin), '--rigid', str(model), view, str(rigid_path)]
                watch = run(command, str(rigid_path) + '.log', args.wall, args.memory)
                write_json(str(rigid_path) + '.watch.json', watch)
                if not rigid_path.exists() or not json.loads(rigid_path.read_text())['passed']:
                    failures.append(f'rounded rigid-transform drawing locus gate failed {form}/{view}')
    times = [r['seconds'] for r in rows if r['case']['primary'] and r.get('edges', 0) > 0]
    primary = math.exp(sum(math.log(t) for t in times) / len(times)) if times else None
    value = {'iteration': args.iteration, 'summary': args.summary, 'gate': {'passed': not failures, 'failures': failures},
             'primary': primary if complete and len(rows) == len(manifest) and all('seconds' in r for r in rows) else None,
             'partialPrimarySeconds': primary, 'secondary': {
                 'maxManagedBytes': max((r.get('managedBytes', 0) for r in rows), default=0),
                 'maxPrivateBytes': max((r.get('privateBytes', 0) for r in rows), default=0),
                 'memoryScope': 'Observed full worker peaks including validation; owned job peak samples are retained per view.',
                 'nativeRatios': {Path(r['case']['path']).stem + '/' + r['case']['view']: r['nativeRatio'] for r in rows if r.get('nativeRatio') is not None}},
             'load_note': 'Shared 16C/32T 64GB host; serial owned processes, eight kernel threads; median of 3 back-to-back kernel runs if under 10s, native 3 if under 10s.',
             'manifestSha256': digest(output / 'manifest.json'), 'rows': len(rows), 'expectedRows': len(manifest),
             'utc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}
    write_json(output / 'score.json', value)
    if complete and not args.select:
        dest = ROOT / 'hillclimb/scores.jsonl'; dest.parent.mkdir(exist_ok=True)
        with dest.open('a', encoding='utf-8') as f: f.write(json.dumps(value, allow_nan=False) + '\n')


if __name__ == '__main__': main()
