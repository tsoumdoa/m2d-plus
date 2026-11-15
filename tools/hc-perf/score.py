"""Scores one hc-perf run (results.jsonl) for one or more configs.

Primary metric: geometric-mean wall time (median of reps; one run when >= 10 s) over all timed benchmark views.
Gate (raster configs only): on every gate view, no significant locus disagreement with plain native HLD
(spans > 2*tau that are not native Unresolved/Unset flips), no visible-point mismatch, no culled object that has a
visible locus in the native reference, and the metamorphic checks (permutation, rigid rotations) pass.

Usage: python score.py <results.jsonl> <config> [--native-ref <dir>] [--append <scores.jsonl> --iteration N --summary TEXT --load TEXT]
"""
import argparse, glob, json, math, os, sys
from collections import defaultdict


def load(path):
    rows = []
    for line in open(path, encoding='utf-8'):
        line = line.strip()
        if line:
            rows.append(json.loads(line))
    return rows


def native_times(ref_dir):
    times = {}
    for f in glob.glob(os.path.join(ref_dir, '*.json')):
        r = json.load(open(f, encoding='utf-8'))
        times[(r['model'], r['view'])] = r
    return times


def geomean(xs):
    xs = [x for x in xs if x and x > 0]
    return math.exp(sum(math.log(x) for x in xs) / len(xs)) if xs else float('nan')


def score(rows, config, natives):
    mine = [r for r in rows if r.get('config') == config]
    errors = [r for r in rows if 'error' in r and r.get('config') is None]
    timed = [r for r in mine if r.get('timed')]
    failures = []; excused = []
    for r in errors:
        failures.append({'model': r['model'], 'kind': 'run error', 'detail': r['error'][:200]})
    for r in mine:
        if r.get('error'):
            failures.append({'model': r['model'], 'view': r['view'], 'kind': 'view error', 'detail': r['error']})
        if not r.get('gate') or config == 'native':
            continue
        c = r.get('compare')
        if c is None:
            failures.append({'model': r['model'], 'view': r['view'], 'kind': 'no reference'})
            continue
        if c['SignificantSpans'] or c['PointMismatches']:
            failures.append({'model': r['model'], 'view': r['view'], 'kind': 'locus', 'spans': c['SignificantSpans'],
                             'length': round(c['SignificantLength'], 4), 'points': c['PointMismatches'],
                             'top': [s for s in c['spans'] if s['Kind'] != 'unresolved-swap'][:3]})
        if r.get('culledWithVisibleCount'):
            failures.append({'model': r['model'], 'view': r['view'], 'kind': 'culled-with-visible', 'count': r['culledWithVisibleCount'],
                             'ids': r.get('culledWithVisible', [])[:3]})
        native_meta = (natives.get((r['model'], r['view'])) or {}).get('metamorphic') or {}
        for name, m in (r.get('metamorphic') or {}).items():
            if not m.get('pass') and native_meta.get(name, {}).get('pass') is False:
                excused.append({'model': r['model'], 'view': r['view'], 'kind': f'metamorphic {name}', 'reason': 'plain native fails the same check'})
                continue
            if not m.get('pass'):
                failures.append({'model': r['model'], 'view': r['view'], 'kind': f'metamorphic {name}', 'spans': m['SignificantSpans'],
                                 'length': round(m['SignificantLength'], 4), 'points': m['PointMismatches']})
    primary = geomean([r['median'] for r in timed])
    sec = {}
    pre = [r['prepass'] for r in timed if r.get('prepass')]
    if pre:
        sec['culledFraction'] = sum(p['Culled'] + p['Outside'] for p in pre) / max(1, sum(p['Sources'] for p in pre))
        sec['isolatedFraction'] = sum(p.get('Isolated', 0) for p in pre) / max(1, sum(p['Sources'] for p in pre))
        sec['nativeSourceFraction'] = sum(p.get('NativeSources', p['Sources']) for p in pre) / max(1, sum(p['Sources'] for p in pre))
        sec['meanCulledFractionPerView'] = sum((p['Culled'] + p['Outside']) / max(1, p['Sources']) for p in pre) / len(pre)
    tot = sum(r.get('visibleLength', 0) + r.get('hiddenLength', 0) for r in timed)
    if tot:
        sec['certifiedLengthFraction'] = sum(r.get('certifiedLength', 0) for r in timed) / tot
    sec['peakBytes'] = max((r.get('peakBytes', 0) for r in mine), default=0)
    ratios = []
    inrun = {(r['model'], r['view']): r['median'] for r in rows if r.get('config') == 'native' and r.get('median')}
    for r in timed:
        n = inrun.get((r['model'], r['view'])) or (natives.get((r['model'], r['view'])) or {}).get('median')
        if n:
            ratios.append(r['median'] / n)
    if ratios:
        sec['nativeTimeRatioGeomean'] = geomean(ratios)
    sec['timedViews'] = len(timed)
    by_group = defaultdict(list)
    for r in timed:
        by_group[r.get('group', '')].append(r['median'])
    sec['groupGeomean'] = {g: round(geomean(v), 4) for g, v in sorted(by_group.items())}
    gate_views = sum(1 for r in mine if r.get('gate'))
    return {'config': config, 'primary': primary, 'secondary': sec, 'gate': {'passed': not failures, 'gateViews': gate_views, 'failures': failures, 'excused': excused}}


if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('results'); ap.add_argument('config')
    ap.add_argument('--native-ref', default=os.path.join(os.path.dirname(__file__), '..', '..', 'artifacts', 'hc-perf', 'native-ref'))
    ap.add_argument('--append'); ap.add_argument('--iteration', type=int); ap.add_argument('--summary', default='')
    ap.add_argument('--load', default=''); ap.add_argument('--kept', default=None)
    a = ap.parse_args()
    s = score(load(a.results), a.config, native_times(a.native_ref))
    print(json.dumps({**s, 'gate': {**s['gate'], 'failures': s['gate']['failures'][:25], 'failureCount': len(s['gate']['failures'])}}, indent=1))
    if a.append:
        line = {'iteration': a.iteration, 'summary': a.summary, 'config': a.config,
                'gate': {'passed': s['gate']['passed'], 'failures': [f"{f.get('model')}/{f.get('view')}: {f['kind']}" for f in s['gate']['failures']][:50],
                         'excused': [f"{f.get('model')}/{f.get('view')}: {f['kind']} ({f['reason']})" for f in s['gate']['excused']]},
                'primary': round(s['primary'], 5), 'secondary': s['secondary'], 'load': a.load}
        if a.kept is not None:
            line['kept'] = a.kept == 'yes'
        with open(a.append, 'a', encoding='utf-8') as f:
            f.write(json.dumps(line) + '\n')
