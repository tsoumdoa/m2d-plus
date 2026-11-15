"""Cull pairs through the independent comparator (tools/perf-f/compare.py).
visible: non-hidden output of the full solve (culled sources never have any, by construction of the sets) against the solve
with the set removed, both directions, budget 1.25 x tolerance.
hidden: every hidden line of the reduced solve must exist in the full solve (unfiltered, because coincident hidden lines are
owned by one source and that owner may be a removed one)."""
import json, subprocess, sys, glob, os
out = sys.argv[1]
def run(a, b):
    r = subprocess.run([sys.executable, 'tools/perf-f/compare.py', a, b], capture_output=True, text=True)
    return json.loads(r.stdout)
def short(d): return {k[:3]: round(v, 3) for k, v in d.items()} or 0
print('| Model, view | Set | Removed | Non-hidden length m | Full not in reduced (by class) | Reduced not in full (by class) | Worst offset mm | Reduced hidden not in full m |')
print('|---|---|---:|---:|---|---|---:|---:|')
for base in sorted(glob.glob(out + '/*-base-vs-*-visible.json')):
    stem, label = base[:-len('-visible.json')].rsplit('-base-vs-', 1)
    cand = f'{stem}-cull-{label}-visible.json'
    if not os.path.exists(cand): continue
    rep = run(base, cand); json.dump(rep, open(f'{stem}-compare-{label}-visible.json', 'w'), indent=1)
    hid = run(f'{stem}-cull-{label}-hidden.json', f'{stem}-base-hidden.json'); json.dump(hid, open(f'{stem}-compare-{label}-hidden.json', 'w'), indent=1)
    spike = json.load(open(stem + '-spike.json')); n = next((r['removedSources'] for r in spike['rows'] if r['name'] == 'cull ' + label), '?')
    total = round(sum(v['length'] for v in rep['baseline'].values()), 1)
    worst = [w for w in (rep['baselineNotInCandidate']['worstDistance'], rep['candidateNotInBaseline']['worstDistance']) if 0 < w < 1e90]
    model, view = os.path.basename(stem).rsplit('-', 1)
    print(f"| {model}, {view} | {label} | {n} | {total} | {short(rep['baselineNotInCandidate']['missedLengthByClass'])} | {short(rep['candidateNotInBaseline']['missedLengthByClass'])} | {('%.1f' % (max(worst) * 1000)) if worst else ('none nearby' if not rep['pass'] else '0')} | {round(sum(hid['baselineNotInCandidate']['missedLengthByClass'].values()), 2)} |")
