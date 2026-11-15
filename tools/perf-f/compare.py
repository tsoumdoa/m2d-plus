"""PERF-F independent drawing comparison. Shares no code with the product or the harness solver.

Usage: python compare.py baseline.json candidate.json [--tol-mult 1.0] [--step-mult 20]

Compares two PerfF dumps (section dump or curved raw-HLD dump):
  * every piece of A is sampled by arc length along its polyline (both ends plus spacing <= step) and each sample must lie within
    budget of some polyline segment of the same class in B, and vice versa (bidirectional locus cover);
  * per-class total lengths and piece counts are reported (not gated: splits may differ);
  * section regions are matched by (sources, loop count, hole flags) and compared by area and loop length;
  * diagnostics lists must be equal.
Budget = tol-mult * tolerance + 2 * (tolerance / 8) chord allowance for non-polyline curves.
Exit code 0 only when nothing is missed in either direction.
"""
import json, math, sys
from collections import defaultdict


def segments(piece, dim):
    p = piece['p']
    pts = [tuple(p[i:i + dim]) for i in range(0, len(p), dim)]
    return pts


def length(pts):
    return sum(math.dist(a, b) for a, b in zip(pts, pts[1:]))


class Grid:
    def __init__(self, cell):
        self.cell = cell
        self.cells = defaultdict(list)

    def add(self, a, b):
        steps = max(1, int(math.dist(a, b) / self.cell) + 1)
        seen = set()
        for i in range(steps + 1):
            t = i / steps
            q = tuple(a[k] + (b[k] - a[k]) * t for k in range(len(a)))
            key = tuple(int(math.floor(c / self.cell)) for c in q)
            if key not in seen:
                seen.add(key)
                self.cells[key].append((a, b))

    def distance(self, q, limit):
        base = tuple(int(math.floor(c / self.cell)) for c in q)
        best = float('inf')
        offsets = (-1, 0, 1)
        dims = len(q)
        def walk(i, key):
            nonlocal best
            if i == dims:
                for a, b in self.cells.get(tuple(key), ()):
                    best = min(best, point_segment(q, a, b))
                return
            for o in offsets:
                walk(i + 1, key + [base[i] + o])
                if best <= limit:
                    return
        walk(0, [])
        return best


def point_segment(q, a, b):
    ab = [b[k] - a[k] for k in range(len(a))]
    aq = [q[k] - a[k] for k in range(len(a))]
    den = sum(c * c for c in ab)
    t = 0.0 if den == 0 else max(0.0, min(1.0, sum(x * y for x, y in zip(ab, aq)) / den))
    return math.dist(q, [a[k] + ab[k] * t for k in range(len(a))])


def cover(a_pieces, b_pieces, dim, budget, step):
    """Length of A's loci (by class) not covered by B's loci of the same class."""
    grids = {}
    cell = max(step / 4, budget * 4)
    for piece in b_pieces:
        pts = segments(piece, dim)
        grid = grids.setdefault(piece['c'], Grid(cell))
        if len(pts) == 1:
            grid.add(pts[0], pts[0])
        for a, b in zip(pts, pts[1:]):
            grid.add(a, b)
    missed = defaultdict(float); worst = 0.0; examples = []
    for piece in a_pieces:
        pts = segments(piece, dim)
        grid = grids.get(piece['c'])
        # Arc-length sampling along the whole polyline: both ends, every vertex-free stretch <= step.
        samples = [(pts[0], 0.0)]
        carried = 0.0
        for a, b in zip(pts, pts[1:]):
            d = math.dist(a, b)
            if d == 0: continue
            pos = step - carried
            while pos < d:
                t = pos / d
                samples.append((tuple(a[k] + (b[k] - a[k]) * t for k in range(dim)), step)); pos += step
            carried = (carried + d) % step
        if len(pts) > 1: samples.append((pts[-1], carried))
        for q, weight in samples:
            dist = grid.distance(q, budget) if grid else float('inf')
            if dist > budget:
                missed[piece['c']] += weight if weight > 0 else step
                worst = max(worst, dist if dist != float('inf') else 1e99)
                if len(examples) < 5:
                    examples.append({'class': piece['c'], 'source': piece.get('s'), 'point': q, 'distance': dist})
    return dict(missed), worst, examples


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    opts = {a.split('=')[0]: a.split('=')[1] for a in sys.argv[1:] if a.startswith('--') and '=' in a}
    a = json.load(open(args[0])); b = json.load(open(args[1]))
    tol = a['tolerance']
    dim = 2 if a.get('planar2d') else 3
    budget = float(opts.get('--tol-mult', 1.0)) * tol + 2 * tol / 8
    step = float(opts.get('--step-mult', 20)) * tol * 25  # default 0.05 model units at tol 1e-4
    report = {'tolerance': tol, 'budget': budget, 'sampleStep': step}
    ok = True
    for name, x in (('baseline', a), ('candidate', b)):
        stats = defaultdict(lambda: [0, 0.0])
        for piece in x['pieces']:
            stats[piece['c']][0] += 1; stats[piece['c']][1] += length(segments(piece, dim))
        report[name] = {k: {'pieces': v[0], 'length': v[1]} for k, v in sorted(stats.items())}
    for name, x, y in (('baselineNotInCandidate', a, b), ('candidateNotInBaseline', b, a)):
        missed, worst, examples = cover(x['pieces'], y['pieces'], dim, budget, step)
        report[name] = {'missedLengthByClass': missed, 'worstDistance': worst, 'examples': examples}
        ok &= not missed
    if 'regions' in a:
        def key(r):
            return (tuple(r['sources']), len(r['loops']), tuple(l['hole'] for l in r['loops']), r['status'], r['hatch'])
        ra = defaultdict(list); rb = defaultdict(list)
        for r in a['regions']: ra[key(r)].append(r)
        for r in b['regions']: rb[key(r)].append(r)
        mismatch = []
        for k in set(ra) | set(rb):
            xs = sorted(ra.get(k, []), key=lambda r: r['area']); ys = sorted(rb.get(k, []), key=lambda r: r['area'])
            if len(xs) != len(ys):
                mismatch.append({'key': str(k), 'baseline': len(xs), 'candidate': len(ys)}); continue
            for x, y in zip(xs, ys):
                lx = sum(l['length'] for l in x['loops']); ly = sum(l['length'] for l in y['loops'])
                if abs(x['area'] - y['area']) > max(16 * tol * tol, abs(x['area']) * 1e-9) or abs(lx - ly) > max(tol, lx * 1e-9):
                    mismatch.append({'key': str(k), 'areas': [x['area'], y['area']], 'lengths': [lx, ly]})
        report['regions'] = {'baseline': len(a['regions']), 'candidate': len(b['regions']), 'mismatches': mismatch[:10], 'mismatchCount': len(mismatch)}
        ok &= not mismatch
        same_diag = a.get('diagnostics') == b.get('diagnostics')
        report['diagnosticsEqual'] = same_diag
        report['flags'] = {k: [a.get(k), b.get(k)] for k in ('error', 'quarantined', 'uncertain', 'invalid', 'hiddenPoints')}
        ok &= same_diag and all(v[0] == v[1] for v in report['flags'].values())
    report['pass'] = bool(ok)
    print(json.dumps(report, indent=1, default=str))
    sys.exit(0 if ok else 1)


if __name__ == '__main__':
    main()
