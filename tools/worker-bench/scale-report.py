"""Summarise scale-bench evidence without loading Rhino. Outputs stay outside Git."""
import csv
import json
from pathlib import Path
import sys

root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path.home() / '.t3/m2d-plus-models/evidence/unit-scale'
final = root / 'final'
scenes = ('m-blocks', 'mm-blocks', 'm-exploded', 'mm-exploded', 'm-steel', 'mm-steel', 'offset-blocks-corrected')
data = {name: json.loads((final / name / 'scale.json').read_text()) for name in scenes}
lines = ['# Recorded scale measurements', '', 'Seconds; n=1 on a shared machine. See the investigation for method and limitations.', '',
         '| Scene | Capture | Local eight-view sum | Two-worker wall | Matching view hashes |', '|---|---:|---:|---:|---:|']
for name, scene in data.items():
    pool_file = final / name / 'pool-8-2.json'
    pool = json.loads(pool_file.read_text())[-1] if pool_file.exists() else None
    wall = f"{pool['seconds']:.3f}" if pool else 'pending'
    matches = f"{sum(v.get('match') is True for v in pool['views'])}/8" if pool else 'pending'
    lines.append(f"| {name} | {scene['captureSeconds']:.3f} | {sum(r['seconds'] for r in scene['rows']):.3f} | {wall} | {matches} |")
lines += ['', '| Scene | View | Local wall | Native solve | Verification | Kernel wall | Exact fallbacks |', '|---|---|---:|---:|---:|---:|---:|']
import re
for name, scene in data.items():
    for row in scene['rows']:
        phases = row['phases']
        match = re.search(r'exact fallbacks=(\d+)', row['info'])
        lines.append(f"| {name} | {row['Name']} | {row['seconds']:.3f} | {phases.get('native solve', 0):.3f} | {phases.get('native ray verification', 0):.3f} | {phases.get('kernel solve', 0):.3f} | {match[1] if match else 0} |")
(root / 'measurements.md').write_text('\n'.join(lines) + '\n')
with (root / 'phases.csv').open('w', newline='') as stream:
    writer = csv.writer(stream)
    writer.writerow(('scene', 'view', 'wall_seconds', 'phase', 'seconds'))
    for name, scene in data.items():
        for row in scene['rows']:
            for phase, seconds in row['phases'].items():
                writer.writerow((name, row['Name'], row['seconds'], phase, seconds))

bars = []
for i, view in enumerate(('North', 'South', 'East', 'West', 'Top', 'SW')):
    bars.append(f'<div class="group"><strong>{view}</strong>')
    for name, label, color in (('m-blocks', 'm', 1), ('mm-blocks', 'mm', 2), ('offset-blocks-corrected', 'mm + 1e7', 3)):
        seconds = data[name]['rows'][i]['seconds']
        bars.append(f'<div class="row"><span>{label}</span><div class="track"><div class="bar c{color}" style="width:{seconds/16*100:.2f}%"></div></div><b>{seconds:.2f}s</b></div>')
    bars.append('</div>')
chart = '''<!doctype html><meta charset="utf-8"><style>
:root{font-family:var(--font-sans);color:var(--foreground)}
body{margin:0}.group{margin:0 0 14px}.group strong{font-size:13px}.row{display:flex;gap:10px;align-items:center;font-size:12px;height:20px}
.row span{width:75px;color:var(--muted-foreground)}.row b{width:52px;text-align:right;font-weight:500}.track{flex:1}.bar{height:10px;border-radius:3px}.c1{background:var(--chart-1)}.c2{background:var(--chart-2)}.c3{background:var(--chart-3)}p{font-size:12px;color:var(--muted-foreground)}
</style><p>5,000-source block model · local single-view seconds · lower is faster</p>''' + ''.join(bars) + '<p>One run per case on a shared machine. Millimetre tolerance is 0.01 mm; the original metre fixture uses 0.0001 m.</p>'
(root / 'elevations.html').write_text(chart, encoding='utf-8')
print(root / 'measurements.md')
