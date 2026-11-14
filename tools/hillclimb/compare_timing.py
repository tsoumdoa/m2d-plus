"""Paired bootstrap audit of equal-coverage timing differences on a shared host."""
import json
import random
import sys

a = {r['Id']: r['ms'] for r in json.load(open(sys.argv[1], encoding='utf-8-sig')) if r['crossing']}
b = {r['Id']: r['ms'] for r in json.load(open(sys.argv[2], encoding='utf-8-sig')) if r['crossing']}
assert a.keys() == b.keys(), 'Timing inventories differ'
ids = sorted(a)
x, y = [a[i] for i in ids], [b[i] for i in ids]
def median(values):
    ordered = sorted(values)
    return ordered[len(ordered) // 2]  # same upper-middle convention as the scorer
rng = random.Random(20261114)
differences = []
for _ in range(3000):
    sample = [rng.randrange(len(ids)) for _ in ids]
    differences.append(median(y[i] for i in sample) - median(x[i] for i in sample))
differences.sort()
result = dict(referenceMedianMs=median(x), candidateMedianMs=median(y),
              pairedMedianDifference95=[differences[75], differences[2924]],
              consistentReduction=differences[2924] < 0,
              note='Paired resampling of fixed member costs; shared global load is still uncontrolled.')
print(json.dumps(result))
