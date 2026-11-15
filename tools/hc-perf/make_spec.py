"""Builds the PERF hill-climb benchmark spec (shared set; theory E scores curved too).

Usage: python make_spec.py <out.json> [--smoke] --configs '<json list>' [--metamorphic name,...] [--ref DIR]
"""
import glob, json, os, sys, argparse

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ORTHO = 'C:/Users/tomosandego/.t3/worktrees/m2d-plus/t3-3a78edd1-1cd89a1b1b9b38a1/artifacts/models'
CURVED = 'C:/Users/tomosandego/.t3/worktrees/m2d-plus/t3-d6bdd8e8-f1e6b3c68a0e104e/artifacts/models'
PERF_VIEWS = ['North elevation', 'SW axo', 'Top']
SMALL_VIEWS = ['Top', 'Front', 'SW']
# Four 20 m x 20 m crop tiles of the M named-view frame (+-27.3 m about the camera target).
TILES = [[-20, 0, -20, 0], [0, 20, -20, 0], [-20, 0, 0, 20], [0, 20, 0, 20]]

def items(smoke=False):
    out = []
    fixtures = sorted(glob.glob(os.path.join(REPO, 'tests', 'fixtures', '*.3dm')))
    for f in fixtures:
        out.append({'model': f.replace(os.sep, '/'), 'group': 'fixture', 'metamorphic': True,
                    'views': [{'name': v, 'gate': True} for v in SMALL_VIEWS]})
    features = ['annotations', 'blocks', 'curves-points', 'cut-topology', 'edges', 'glass', 'section-styles',
                'units-coords-ft', 'units-coords-m', 'units-coords-mm', 'units-coords-offset', 'view-range', 'view-sources']
    for m in features:
        out.append({'model': f'{ORTHO}/{m}.3dm', 'group': 'feature', 'metamorphic': True,
                    'views': [{'name': v, 'gate': True} for v in SMALL_VIEWS]})
    for m in ['curved-steel', 'diagrid-shell', 'space-frame', 'curved-facade']:
        p = f'{CURVED}/{m}.3dm'
        if os.path.exists(p):
            out.append({'model': p, 'group': 'curved', 'metamorphic': False, 'views': [{'name': v, 'gate': True} for v in SMALL_VIEWS]})
    tiers = ['XS', 'S'] if smoke else ['XS', 'S', 'M', 'L', 'XL']
    for tier in tiers:
        for flavor in ['blocks', 'exploded']:
            gate = tier in ('XS', 'S')
            timed = tier != 'XS'  # XS is a gate-only tier
            views = [{'name': v, 'gate': gate, 'timed': timed} for v in PERF_VIEWS]
            if tier == 'M':
                for v in ['North elevation', 'SW axo']:
                    for k, t in enumerate(TILES):
                        views.append({'name': v, 'label': f'{v} tile {k + 1}', 'tile': t, 'gate': True, 'timed': False})
            out.append({'model': f'{ORTHO}/perf-{tier}-{flavor}.3dm', 'group': f'perf-{tier}', 'metamorphic': tier == 'XS', 'views': views})
    # Curved L/XL are excluded: native HLD cannot produce a reference (L never completed in the curved suite's own baseline).
    ctiers = ['S'] if smoke else ['S', 'M']
    for tier in ctiers:
        for flavor in ['blocks', 'exploded']:
            p = f'{CURVED}/curved-perf-{tier}-{flavor}.3dm'
            if os.path.exists(p):
                out.append({'model': p, 'group': f'curved-perf-{tier}', 'metamorphic': False,
                            'views': [{'name': v, 'gate': tier == 'S'} for v in PERF_VIEWS]})
    return out

if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('out'); ap.add_argument('--smoke', action='store_true')
    ap.add_argument('--configs', required=True); ap.add_argument('--metamorphic', default='')
    ap.add_argument('--ref', default=os.path.join(REPO, 'artifacts', 'hc-perf', 'native-ref'))
    ap.add_argument('--filter', default='')
    a = ap.parse_args()
    its = items(a.smoke)
    if a.filter: its = [i for i in its if any(f in i['model'] for f in a.filter.split(','))]
    spec = {'refDir': a.ref, 'reps': 3, 'repThreshold': 10, 'configs': json.loads(a.configs),
            'metamorphic': [m for m in a.metamorphic.split(',') if m], 'items': its}
    json.dump(spec, open(a.out, 'w'), indent=1)
    print(f'{len(its)} models, {sum(len(i["views"]) for i in its)} views')
