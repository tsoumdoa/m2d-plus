"""Mesh-tier spike runner: one owned Rhino process at a time, under the shared model-suite mutex.
Usage: python tools/perf-f/mt_run.py <label> <model relative to M2D_MODELS> <view> [switches ...]   (environment PERFF_MT* passes through)"""
import os, subprocess, sys
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools' / 'models'))
from common import SerialRhino
label, model, view, *switches = sys.argv[1:]
models = Path(os.environ.get('M2D_MODELS', Path.home() / '.t3' / 'm2d-plus-models'))
resolver = os.environ['M2D_RHINO_INSIDE']
exe = ROOT / 'tools/perf-f/bin/Release/net8.0-windows/M2DPlus.PerfF.dll'
out = ROOT / 'artifacts' / 'mesh-tier' / label
out.mkdir(parents=True, exist_ok=True)
with SerialRhino(wait_seconds=None):
    done = subprocess.run(['dotnet', str(exe), resolver, str(models / model), str(out), view, *switches], capture_output=True, text=True, encoding='utf-8', errors='replace')
(out / 'log.txt').write_text(done.stdout + done.stderr, encoding='utf-8')
keep = ('===', '---', '    off:', '    Dense', '    ray check', '    witnesses', '    mesh build', '    phases', '    engine length', '    unresolved', '    mesh tier', '    adjudication', '    native length', '    engine certain', '    sources')
print('\n'.join(line[:1800] for line in done.stdout.splitlines() if line.startswith(keep)))
