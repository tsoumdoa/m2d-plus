"""Serial length census on external, read-only building and curved suites."""
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
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--select', default='')
    p.add_argument('--resume', action='store_true')
    p.add_argument('--baseline', type=Path, help='Audit Visible pieces from sources that had baseline Unresolved output')
    p.add_argument('--assembly', type=Path, help='Explicit archived harness/engine directory for a baseline cause replay')
    p.add_argument('--suite', choices=('all', 'buildings', 'curved'), default='all')
    a = p.parse_args()
    models = Path(os.environ.get('M2D_MODELS') or Path.home() / '.t3/m2d-plus-models')
    assembly = a.assembly.resolve() if a.assembly else ROOT / 'tools/perf-f/bin/Release/net8.0-windows/M2DPlus.PerfF.dll'
    engine = assembly.parent / 'M2DPlus.Rhino.dll'
    cases = [(models / 'buildings' / f'perf-{tier}-{form}.3dm', view)
             for tier in ('S', 'M') for form in ('blocks', 'exploded')
             for view in ('North', 'SW', 'Top', 'long', 'plan')]
    cases += [(models / 'curved' / f'curved-perf-S-{form}.3dm', view)
              for form in ('blocks', 'exploded') for view in ('North', 'SW', 'Top', 'long', 'plan')]
    cases += [(models / 'curved' / f'{name}.3dm', view)
              for name in ('curved-facade', 'curved-steel', 'diagrid-shell', 'space-frame')
              for view in ('North', 'SW', 'Top', 'long', 'plan')]
    rows = []
    for model, view in cases:
        if a.suite != 'all' and model.parent.name != a.suite:
            continue
        key = model.stem + '__' + view
        if a.select and not any(s in key for s in a.select.split(',')):
            continue
        folder = a.output.resolve() / key
        folder.mkdir(parents=True, exist_ok=True)
        receipt = folder / 'receipt.json'
        if a.resume and receipt.exists():
            row = json.loads(receipt.read_text())
            if row['engineSha256'] == digest(engine) and row['harnessSha256'] == digest(assembly) and row['modelSha256'] == digest(model) and row['watch']['status'] == 'exited' and row['watch']['exitCode'] == 0:
                rows.append(row)
                continue
        print(key, flush=True)
        if a.baseline:
            dumps = list((a.baseline / key).glob('*-1-census.json'))
            if len(dumps) != 1:
                raise RuntimeError('Missing unique baseline for ' + key)
            os.environ['PERFF_UNCERTAINTY_BASELINE'] = str(dumps[0].resolve())
        command = ['dotnet', str(assembly), str(a.resolver.resolve()), str(model), str(folder), view, 'census']
        with SerialRhino(wait_seconds=None):
            watch = run(command, folder / 'run.log', 1800, 16)
        row = dict(case=key, modelSha256=digest(model), engineSha256=digest(engine),
                   harnessSha256=digest(assembly), watch=watch)
        timing = next(folder.glob('*-timings.json'), None)
        if timing:
            row['timings'] = json.loads(timing.read_text())
            result = row['timings']['rows'][0]
            if result.get('summary'):
                dump = json.loads(Path(result['summary']['dump']).read_text())
                lengths = {}
                for piece in dump['pieces']:
                    lengths[piece['c']] = lengths.get(piece['c'], 0) + piece['length']
                lengths['Cut region boundaries'] = sum(loop['length'] for region in dump['regions'] for loop in region['loops'])
                row.update(lengths=lengths, quarantined=dump['quarantined'],
                           unresolvedSources=len({x['s'] for x in dump['pieces'] if x['c'] == 'Unresolved'}))
        write_json(receipt, row)
        rows.append(row)
        write_json(a.output / 'census.json', rows)
    write_json(a.output / 'census.json', rows)


if __name__ == '__main__':
    main()
