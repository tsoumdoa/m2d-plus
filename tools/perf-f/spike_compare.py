"""Curved spike: compact tables on top of compare.py's bidirectional locus cover (no product code).

Usage: python spike_compare.py reference.json candidate.json [--budgets=0.000125,0.001,0.003] [--step=0.02] [--classes=Visible,Hidden]

For every budget (absolute model units) prints, per direction, the length of linework with no same-class partner within the
budget, and the same with classes ignored ("locus"). The class rows include errors in where visibility changes along a
curve; the locus rows only ask whether the line is drawn in the right place.
"""
import json, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from compare import cover, segments, length


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    opts = {a.split('=')[0]: a.split('=')[1] for a in sys.argv[1:] if a.startswith('--') and '=' in a}
    a = json.load(open(args[0])); b = json.load(open(args[1]))
    tol = a['tolerance']; dim = 2
    budgets = [float(x) for x in opts.get('--budgets', f'{1.25 * tol},{10 * tol},{30 * tol},{100 * tol}').split(',')]
    step = float(opts.get('--step', 0.02))
    classes = opts.get('--classes')
    def keep(d):
        pieces = [p for p in d['pieces'] if len(p['p']) >= 2 * dim and (classes is None or p['c'] in classes.split(','))]
        return pieces
    pa, pb = keep(a), keep(b)
    def merged(pieces): return [dict(p, c='Any') for p in pieces]
    out = {'reference': args[0], 'candidate': args[1], 'step': step, 'lengths': {}, 'rows': []}
    for name, pieces in (('reference', pa), ('candidate', pb)):
        totals = {}
        for p in pieces: totals[p['c']] = totals.get(p['c'], 0.0) + length(segments(p, dim))
        out['lengths'][name] = {k: round(v, 3) for k, v in sorted(totals.items())}
    for budget in budgets:
        row = {'budget': budget}
        for label, x, y in (('refNotInCand', pa, pb), ('candNotInRef', pb, pa)):
            missed, worst, examples = cover(x, y, dim, budget, step)
            row[label] = {k: round(v, 3) for k, v in sorted(missed.items())}
            row[label + 'Example'] = examples[:2]
            lm, lw, _ = cover(merged(x), merged(y), dim, budget, step)
            row[label + 'Locus'] = round(sum(lm.values()), 3)
            row[label + 'LocusWorst'] = None if not lm else (lw if lw < 1e90 else 'none nearby')
        out['rows'].append(row)
    print(json.dumps(out, indent=1, default=str))


if __name__ == '__main__':
    main()
