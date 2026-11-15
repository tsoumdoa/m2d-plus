"""Instrument an artifact-only copy of Fable for diagnosis. Never linked into product code."""
from pathlib import Path
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
FABLE = Path('C:/Users/tomosandego/.t3/scratch/m2d-plus-fable/proto/x')
MODELS = Path('C:/Users/tomosandego/.t3/worktrees/m2d-plus/t3-3a78edd1-1cd89a1b1b9b38a1/artifacts/models')
BASELINE = MODELS.parent / 'baseline'


def main():
    target = ROOT / 'artifacts/hc-perf/fable-probe'; target.mkdir(parents=True, exist_ok=True)
    text = (FABLE / 'Program.cs').read_text(encoding='utf-8-sig')
    marker = 'double tPost = sw.Elapsed.TotalSeconds;'
    export = '''
        var export = Environment.GetEnvironmentVariable("HC_FABLE_EXPORT");
        if (export != null) {
            var rows = new List<object>(); var candidates = new List<object>();
            for (int i=0; i<EA.Length; i++) {
                var e = EA[i]; var source = (inst[e.Obj].g.Curved ? "curved" : "planar") + "/" + e.Obj;
                object Segment(double a, double b, int c) => new { X0=e.x0+(e.x1-e.x0)*a, Y0=e.y0+(e.y1-e.y0)*a,
                    X1=e.x0+(e.x1-e.x0)*b, Y1=e.y0+(e.y1-e.y0)*b, Class=c, Source=source };
                candidates.Add(Segment(0,1,0));
                if (vis[i] != null) foreach(var (va,vb) in vis[i]) rows.Add(Segment(va,vb,0));
                if (hid[i] != null) foreach(var (ha,hb) in hid[i]) rows.Add(Segment(ha,hb,1));
            }
            File.WriteAllText(export, JsonSerializer.Serialize(rows));
            File.WriteAllText(export+".candidates.json", JsonSerializer.Serialize(candidates));
        }
        '''
    if marker not in text: raise RuntimeError('Fable changed; diagnostic insertion point missing')
    (target / 'Program.cs').write_text(text.replace(marker, export + marker), encoding='utf-8')
    (target / 'Probe.csproj').write_text((FABLE / 'x.csproj').read_text().replace('8.*', '8.17.0'), encoding='utf-8')
    subprocess.run(['dotnet', 'build', str(target / 'Probe.csproj'), '-c', 'Release'], check=True)
    tier = sys.argv[1] if len(sys.argv) > 1 else 'M'
    out = target / f'{tier}-SW.loci.json'
    env = dict(os.environ, HC_FABLE_EXPORT=str(out), CLOSED='1')
    with (target / f'{tier}-SW.log').open('w') as log:
        subprocess.run(['dotnet', str(target / 'bin/Release/net8.0-windows/win-x64/Probe.dll'),
                        str(MODELS / f'perf-{tier}-blocks.3dm'), 'SW', '8'], env=env, stdout=log, check=True)
    meta = json.loads((MODELS / f'perf-{tier}-blocks.json').read_text())
    plane = next(v['plane'] for v in meta['views'] if v['name'] == 'SW axo')
    dx = sum(a*b for a,b in zip(plane['origin'], plane['x']))
    dy = sum(a*b for a,b in zip(plane['origin'], plane['y']))
    loci = []
    svg = ET.parse(BASELINE / f'perf-{tier}-blocks__SW-axo__off__native.svg')
    for path in svg.getroot().iter('{http://www.w3.org/2000/svg}path'):
        hidden = 'hidden' in path.attrib.get('class', '')
        for subpath in path.attrib.get('d', '').split('M')[1:]:
            points = [tuple(map(float, p.split(','))) for p in subpath.split('L')]
            for a,b in zip(points, points[1:]):
                loci.append({'X0': a[0]+dx, 'Y0': -a[1]+dy, 'X1': b[0]+dx, 'Y1': -b[1]+dy,
                             'Class': int(hidden), 'Source': 'archived-native'})
    (target / f'{tier}-SW.native.json').write_text(json.dumps(loci), encoding='utf-8')
    print(target)


if __name__ == '__main__': main()
