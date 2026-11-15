"""Quick look at a running pass: gate problems and isolation/culling per raster config. Usage: python peek.py <results.jsonl> [config...]"""
import json, sys

rows = [json.loads(l) for l in open(sys.argv[1], encoding='utf-8') if l.strip()]
configs = set(a for a in sys.argv[2:] if not a.startswith('-'))
for r in rows:
    if r.get('config') in (None, 'native') or (configs and r['config'] not in configs):
        if 'error' in r and r.get('config') is None:
            print('RUN ERROR', r['model'], r['error'][:200])
        continue
    c = r.get('compare', {}); m = r.get('metamorphic') or {}; p = r.get('prepass') or {}
    bad = c.get('SignificantSpans') or c.get('PointMismatches') or r.get('culledWithVisibleCount') or any(not v['pass'] for v in m.values()) or r.get('error')
    if bad or '-v' in sys.argv:
        print(r['model'][:24], r['view'][:22], r['config'], round(r['median'], 3), 'nat', r.get('nativeSeconds') and round(r['nativeSeconds'], 3),
              'iso', p.get('Isolated'), 'cull', p.get('Culled'), '/', p.get('Sources'), 'sig', c.get('SignificantSpans'), round(c.get('SignificantLength', 0), 4),
              'pts', c.get('PointMismatches'), 'cv', r.get('culledWithVisibleCount'), {k: v['pass'] for k, v in m.items()}, r.get('error') or '')
        if bad and c.get('spans'):
            for s in [s for s in c['spans'] if s['Kind'] != 'unresolved-swap'][:3]:
                print('     ', s)
