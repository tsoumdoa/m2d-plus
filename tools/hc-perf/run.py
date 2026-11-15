"""Owned serial PERF-A runner. Full manifest is immutable for a scored iteration."""
from pathlib import Path
import argparse
import hashlib
import json
import math
import os
import shutil
import subprocess
import sys
import time
import traceback

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/models'))
from common import initialize, digest, write_json

# Local-only generated models (never committed); see ~/.t3/m2d-plus-models/README.md.
MODELS_ROOT = Path(os.environ.get('M2D_MODELS') or Path.home() / '.t3' / 'm2d-plus-models')
DEFAULT_MODELS = MODELS_ROOT / 'buildings'


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
    p.add_argument('--native-only', action='store_true')
    p.add_argument('--native-batch', type=Path)
    p.add_argument('--composition-batch', type=Path)
    p.add_argument('--bench-bin', type=Path, default=ROOT / 'tools/hc-perf/bin/Release/net8.0-windows/M2DPlus.HcPerf.dll')
    p.add_argument('--select', help='Diagnostic only; cannot create a scored full run')
    p.add_argument('--wall', type=float, default=1800)
    p.add_argument('--memory', type=float, default=16)
    p.add_argument('--threads', type=int, default=8)
    p.add_argument('--licence-free', action='store_true', help='Native gate remains failed; no Rhino licence or mutex used')
    p.add_argument('--native-cache', type=Path, help='Hash-verified correctness-only native cache; reuse, never re-time native')
    p.add_argument('--oracle-cache', type=Path, help='Passed iteration with hash-verified, input/algorithm-keyed exact crop truth')
    p.add_argument('--reader-bin', type=Path, default=ROOT / 'tools/hc-perf/reader/bin/Release/net8.0-windows/M2DPlus.HcPerf.Reader.dll')
    args = p.parse_args()
    if args.worker:
        if sys.stdin.readline().strip() != 'go':
            raise RuntimeError('Owned-job handshake required')
        # Native work here is correctness-only. Serial owned workers deliberately
        # do not acquire the cross-suite timing mutex (A2 authorization).
        R, S = initialize()
        import clr
        clr.AddReference(str(args.bench_bin.resolve()))
        from M2DPlus.HcPerf import Bench
        try:
            if args.composition_batch:
                from M2DPlus.HcPerf import CompositionGate
                for case in json.loads(args.composition_batch.read_text()):
                    CompositionGate.Run(case['path'], case['view'], case['output'], case.get('freshNative', False),case.get('publication',False),case.get('traceNative',False))
            elif args.native_batch:
                for case in json.loads(args.native_batch.read_text()):
                    print('native correctness: ' + case['output'], flush=True)
                    Bench.NativeGate(case['path'], case['view'], case['output'])
            elif args.native_only:
                Bench.NativeGate(str(args.model), args.view, str(args.output))
            else:
                Bench.Run(str(args.model), args.view, str(args.output), args.oracle, args.native, 3, args.threads)
        except Exception:
            batch_path = args.composition_batch or args.native_batch
            failure_path = str(batch_path) + '.error.json' if batch_path else str(args.output) + '.json'
            write_json(failure_path, {'gate': {'passed': False, 'failures': [traceback.format_exc()]}})
            raise
        return
    args.oracle_fingerprint=hashlib.sha256(b''.join((ROOT/'src/M2DPlus.Core/Visibility'/name).read_bytes()
        for name in ('ExactVisibilityOracle.cs','Arithmetic.cs','Models.cs'))).hexdigest()
    if args.oracle_cache:
        reference=json.loads((args.oracle_cache/'score.json').read_text())
        hashes=reference['secondary'].get('oracleReferenceSha256',{})
        if not reference['gate']['passed'] or len(hashes)!=16:raise ValueError('Crop oracle cache requires a passed complete 16-tile reference')
        for name,sha in hashes.items():
            if digest(args.oracle_cache/name)!=sha:raise ValueError('Crop oracle cache hash mismatch: '+name)
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
        if args.licence_free or args.native_cache:
            command = ['dotnet', str(args.reader_bin),
                       case['path'], case['view'], str(target)]
            if case['oracle']: command.append('--oracle')
        else:
            command = [sys.executable, '-B', '-u', str(Path(__file__).resolve()), '--worker', '--model', case['path'],
                       '--view', case['view'], '--output', str(target), '--threads', str(args.threads), '--bench-bin', str(args.bench_bin)]
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
        if args.native_cache and case['native']:
            validate_native_cache(args, case, target, row)
            write_json(path, row)
        elif args.licence_free and case['native']:
            row['gate']['passed'] = False
            row['gate']['failures'].append('native gate unavailable: licence-free run')
        row['case'] = case
        rows.append(row)
        # Always checkpoint the score, including missing cases. An interrupted run never looks passed.
        score(args, manifest, rows, output, complete=False)
    score(args, manifest, rows, output, complete=not args.select)


def validate_native_cache(args, case, target, row):
    """Reuse immutable native scene evidence; re-check this candidate's full locus gate."""
    source=args.native_cache/target.name
    try:
        meta=json.loads(Path(str(source)+'.native-meta.json').read_text())
        if meta['modelSha256'].lower()!=row['modelSha256'].lower() or meta['view']!=case['view']:
            raise ValueError('native/kernel model or view mismatch')
        scene=json.loads(Path(str(source)+'.scene.json').read_text())
        if len(scene['Edges'])!=row['edges'] or len(scene['Faces'])!=row['faces'] or sorted(meta['excluded'])!=sorted(row['excluded']):
            raise ValueError('native/kernel eligible planar subset mismatch')
        if meta['priority']!='Normal' or meta['multipleThreads']:
            raise ValueError('native evidence was not normal-priority serial HLD')
        for suffix,key in [('.native.json','nativeSha256'),('.slots.json','slotsSha256'),('.scene.json','sceneSha256')]:
            file=Path(str(source)+suffix)
            if digest(file)!=meta[key].lower():raise ValueError('native cache hash mismatch: '+suffix)
            shutil.copyfile(file,str(target)+suffix)
        shutil.copyfile(str(source)+'.native-meta.json',str(target)+'.native-meta.json')
        from owned_job import run
        command=[sys.executable,'-B','-u',str(ROOT/'tools/hc-perf/compare_a2.py'),str(target.parent),'--case',target.name]
        watch=run(command,str(target)+'.native-audit.log',args.wall,args.memory)
        write_json(str(target)+'.native-audit.watch.json',watch)
        if watch['status']!='exited':raise ValueError('native audit '+watch['status'])
        audit=json.loads(Path(str(target)+'.audit.json').read_text())
        if not audit['passed']:raise ValueError('unexplained native locus samples: '+str(audit['counts']))
        row['nativeValidation']={'passed':True,'counts':audit['counts'],'cache':str(source),
                                 'nativeUnsets':sum(s['visibility']=='Unset' for s in json.loads(Path(str(source)+'.slots.json').read_text()))}
    except (OSError,ValueError,KeyError) as e:
        row['gate']['passed']=False
        row['gate']['failures'].append('native cache gate: '+str(e))


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
                           str(model), view, str(tile_path), args.oracle_fingerprint, str(args.oracle_cache) if args.oracle_cache else '-']
                from owned_job import run
                watch = run(command, str(tile_path) + '.log', args.wall, args.memory)
                write_json(str(tile_path) + '.watch.json', watch)
                if watch['status'] != 'exited' or not tile_path.exists() or not json.loads(tile_path.read_text())['passed']:
                    failures.append(f'M 20m tile oracle incomplete/failed {form}/{view}')
                rigid_path = output / f'perf-M-{form}__{view}.rigid.json'
                command = ['dotnet', str(args.reader_bin), '--rigid', str(model), view, str(rigid_path)]
                watch = run(command, str(rigid_path) + '.log', args.wall, args.memory)
                write_json(str(rigid_path) + '.watch.json', watch)
                if watch['status'] != 'exited' or not rigid_path.exists() or not json.loads(rigid_path.read_text())['passed']:
                    failures.append(f'rounded rigid-transform drawing locus gate failed {form}/{view}')
    times = [r['seconds'] for r in rows if r['case']['primary'] and r.get('edges', 0) > 0]
    primary = math.exp(sum(math.log(t) for t in times) / len(times)) if times else None
    perf_times=[r['seconds'] for r in rows if r['case']['primary'] and Path(r['case']['path']).stem.startswith('perf-') and 'seconds' in r]
    baseline=MODELS_ROOT/'evidence'/'test-models'/'baseline'
    native_ratios={}
    for r in rows:
        if 'seconds' not in r:continue
        stem=Path(r['case']['path']).stem
        view={'North':'North-elevation','SW':'SW-axo'}.get(r['case']['view'])
        file=baseline/f'{stem}__{view}__off__native.json'
        if stem.startswith('perf-') and view and file.exists():
            native_ratios[stem+'/'+r['case']['view']]=json.loads(file.read_text())['computeSeconds']/r['seconds']
    value = {'iteration': args.iteration, 'summary': args.summary, 'gate': {'passed': not failures, 'failures': failures},
             'primary': primary if complete and len(rows) == len(manifest) and all('seconds' in r for r in rows) else None,
             'partialPrimarySeconds': primary, 'secondary': {
                 'maxManagedBytes': max((r.get('managedBytes', 0) for r in rows), default=0),
                 'maxPrivateBytes': max((r.get('privateBytes', 0) for r in rows), default=0),
                 'memoryScope': 'Observed full worker peaks including validation; owned job peak samples are retained per view.',
                 'nativeRatios': native_ratios,
                 'nativeRatioScope':'Archived mixed-model HLD compute / current complete planar-kernel seconds; contextual, not same-subset timing. No Top or XL match.',
                 'perfTierGeomeanSeconds': math.exp(sum(math.log(t) for t in perf_times)/len(perf_times)) if perf_times else None},
             'load_note': 'Shared 16C/32T 64GB host; serial owned processes, eight kernel threads; median of 3 back-to-back kernel runs if under 10s. Native correctness cache only; never re-timed.',
             'manifestSha256': digest(output / 'manifest.json'), 'rows': len(rows), 'expectedRows': len(manifest),
             'utc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}
    value['secondary']['oracleReferenceSha256']={p.name:digest(p) for p in output.glob('*.oracle-reference.json')}
    value['secondary']['oracleFingerprint']=args.oracle_fingerprint
    value['secondary']['oracleCache']=str(args.oracle_cache) if args.oracle_cache else None
    write_json(output / 'score.json', value)
    if complete and not args.select:
        dest = ROOT / 'hillclimb/scores.jsonl'; dest.parent.mkdir(exist_ok=True)
        with dest.open('a', encoding='utf-8') as f: f.write(json.dumps(value, allow_nan=False) + '\n')


if __name__ == '__main__': main()
