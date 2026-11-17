"""Exact output archives off/on; optional mutex-serialized building ABBA timings. No model writes."""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys

sys.dont_write_bytecode = True

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools' / 'models'))
from common import SerialRhino, digest, write_json


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--resolver', required=True)
    p.add_argument('--models', type=Path, default=Path(os.environ.get('M2D_MODELS', Path.home() / '.t3/m2d-plus-models')))
    p.add_argument('--out', type=Path, required=True)
    p.add_argument('--timings', action='store_true')
    p.add_argument('--timing-model', type=Path, help='Default: buildings/perf-M-blocks.3dm (no campus runs).')
    p.add_argument('--native', action='store_true')
    a = p.parse_args()
    assembly = ROOT / 'tools/perf-f/bin/Release/net8.0-windows/M2DPlus.PerfF.dll'
    cases = []
    if a.timings:
        cases = [(a.timing_model or a.models / 'buildings/perf-M-blocks.3dm', v) for v in ('tenthbox', 'Top')]
    else:
        cases += [(a.models / 'buildings/blocks.3dm', v) for v in ('SW', 'Top', 'North', 'long', 'plan')]
        for model in (a.models / 'buildings/curves-points.3dm', *(ROOT / 'tests/fixtures' / (n + '.3dm') for n in
                      ('brep_concave_polysurface', 'column_through_slab', 'block_nested_mirrored'))):
            cases += [(model, v) for v in ('SW', 'Top')]
        cases += [(a.models / 'curved' / (n + '.3dm'), 'SW') for n in
                  ('curved-perf-S-blocks', 'curved-perf-S-exploded', 'space-frame')]
        cases += [(a.models / 'owner/pipes-node-gdas.3dm', v) for v in ('Top', 'SW', 'nodebox')]
        cases += [(model, v) for model in sorted((ROOT / 'tests/fixtures/sections').glob('region_*.3dm'))
                  for v in ('section', 'sectionbox')]
    a.out.mkdir(parents=True, exist_ok=True)
    results = []
    def run(model, views):
        variants = ['cull-off-warmup', 'cull-off', 'cull-on', 'cull-on', 'cull-off'] if a.timings else ['cull-off', 'cull-on']
        if a.native:
            variants = ['native+' + v for v in variants]
        command = ['dotnet', str(assembly), a.resolver, str(model), str(a.out), ';'.join(views), *variants]
        print('START', model.stem, views, flush=True)
        for view in views:
            (a.out / (model.stem + '-' + view + '-timings.json')).unlink(missing_ok=True)
        with (a.out / (model.stem + '-' + views[0] + '.log')).open('w') as log:
            result = subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT)
        for view in views:
            timing = a.out / (model.stem + '-' + view + '-timings.json')
            data = json.loads(timing.read_text()) if timing.exists() else {}
            rows = data.get('rows', [])
            passed = result.returncode == 0 and len(rows) == len(variants) and not any(r['failure'] for r in rows) and len({r['hash'] for r in rows}) == 1
            results.append(dict(model=str(model), view=view, passed=passed, exit=result.returncode,
                                hashes=[r['hash'] for r in rows], rows=rows, timing=str(timing)))
            write_json(a.out / 'audit.json', dict(assemblySha256=digest(assembly), controllerSha256=digest(__file__),
                       timingMutex=a.timings, native=a.native, results=results))
            print('PASS' if passed else 'FAIL', model.stem, view, [(r['variant'], round(r['wall'], 3)) for r in rows], flush=True)
    # Hold exactly the shared model-suite mutex for the entire timing process; wait without a deadline.
    if a.timings:
        print('Waiting for Local\\M2DPlusLocalModelSuiteRhino', flush=True)
        with SerialRhino(wait_seconds=None):
            print('Timing mutex acquired', flush=True)
            for model, view in cases:
                run(model, [view])
    else:
        grouped = {}
        for model, view in cases:
            grouped.setdefault(model, []).append(view)
        for model, views in grouped.items():
            run(model, views)
    return 0 if all(r['passed'] for r in results) else 1


if __name__ == '__main__':
    raise SystemExit(main())
