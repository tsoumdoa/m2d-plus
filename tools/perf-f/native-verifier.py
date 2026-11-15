"""Serial bounded verifier measurements; audit-off and enabled runs share the same prepared scene.

Only the controller's owned Job Objects may be terminated. Source models are read-only.
Run with the research Python venv (for the shared cross-worktree Rhino mutex).
"""
import argparse
import json
import os
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/models'))
from common import SerialRhino, digest, write_json
from owned_job import run


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--resolver', type=Path, required=True)
    p.add_argument('--output', type=Path, default=ROOT / 'artifacts/native-verifier/measurements')
    p.add_argument('--select', default='', help='Optional exact model__view key')
    a = p.parse_args()
    source = Path(os.environ.get('M2D_MODELS') or Path.home() / '.t3' / 'm2d-plus-models') / 'curved'
    cases = [(source / 'curved-perf-S-blocks.3dm', v) for v in ('SW', 'North')]
    cases += [(source / (name + '.3dm'), 'SW') for name in ('curved-facade', 'curved-steel', 'diagrid-shell')]
    assembly = ROOT / 'tools/perf-f/bin/Release/net8.0-windows/M2DPlus.PerfF.dll'
    a.output.mkdir(parents=True, exist_ok=True)
    rows = []
    for model, view in cases:
        if a.select and a.select != model.stem + '__' + view:
            continue
        print(model.stem, view, flush=True)
        folder = a.output / (model.stem + '__' + view)
        folder.mkdir(parents=True, exist_ok=True)
        command = ['dotnet', str(assembly), str(a.resolver.resolve()), str(model), str(folder), view,
                   'audit-off', 'audit-on', 'audit-on-repeat']
        os.environ['PERFF_TIMEOUT'] = '120'
        with SerialRhino(wait_seconds=None):
            watch = run(command, folder / 'run.log', 400, 8)
        write_json(folder / 'watch.json', watch)
        timings = next(folder.glob('*-timings.json'), None)
        row = {'model': model.stem, 'view': view, 'modelSha256': digest(model), 'watch': watch,
               'harnessSha256': digest(assembly), 'controllerSha256': digest(__file__)}
        if timings:
            data = json.loads(timings.read_text())
            row['timings'] = data
            for r in data['rows']:
                if r.get('summary'):
                    dump = json.loads(Path(r['summary']['dump']).read_text())
                    r['verifier'] = [d for d in dump['diagnostics'] if d.startswith('Native ray verifier:')]
                    r['contradictions'] = sum(d.startswith('Native ray contradiction:') for d in dump['diagnostics'])
            enabled = [r for r in data['rows'] if r['variant'].startswith('audit-on')]
            row['repeatHashIdentical'] = len(enabled) == 2 and enabled[0]['hash'] == enabled[1]['hash']
        rows.append(row)
        write_json(a.output / 'measurements.json', rows)


if __name__ == '__main__':
    main()
