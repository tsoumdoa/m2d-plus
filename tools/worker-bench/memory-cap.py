"""Fast SW calibration: one owned Rhino at a time, small fixtures only, shared timing mutex."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/models'))
from common import SerialRhino, write_json


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--resolver', required=True)
    p.add_argument('--out', type=Path, required=True)
    p.add_argument('--cap-gib', type=int, default=8, help='0 uses the production policy')
    p.add_argument('--runtimes', default='8,10')
    a = p.parse_args()
    models = Path(os.environ.get('M2D_MODELS', Path.home() / '.t3/m2d-plus-models')) / 'curved'
    bench = ROOT / 'tools/worker-bench/bin/Release/net8.0-windows/M2DPlus.WorkerBench.dll'
    rows = []
    print('Waiting for Local\\M2DPlusLocalModelSuiteRhino', flush=True)
    with SerialRhino(wait_seconds=None):
        for runtime in a.runtimes.split(','):
            env = dict(os.environ, DOTNET_ROLL_FORWARD='LatestMajor' if runtime == '10' else 'LatestPatch')
            directory = ROOT / f'src/M2DPlus.Worker/bin/Release/net{runtime}.0-windows'
            for model in ('curved-perf-S-blocks', 'diagrid-shell', 'curved-steel'):
                target = a.out / f'net{runtime}' / model
                target.mkdir(parents=True, exist_ok=True)
                print('START', runtime, model, flush=True)
                commands = [
                    ['dotnet', str(bench), 'memory-export', a.resolver, str(models / (model + '.3dm')), str(target)],
                    ['dotnet', str(bench), 'memory-worker', str(target), str(directory), str(a.cap_gib)]]
                for i, command in enumerate(commands):
                    with (target / ('export.log' if i == 0 else 'worker.log')).open('w') as log:
                        subprocess.run(command, env=env, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, check=True)
                result = json.loads((target / 'worker-result.json').read_text())
                rows.append(dict(model=model, runtime=runtime, **result))
                write_json(a.out / 'calibration.json', rows)
                print('DONE', runtime, model, 'peak GiB', round(result['peakCommitBytes'] / 2**30, 3),
                      'identical', result['matches'], flush=True)


if __name__ == '__main__':
    main()
