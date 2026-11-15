"""Assess limited independent/metamorphic properties from recorded results.

Execution success is never promoted to complete drawing correctness.
"""
import argparse
import json
from pathlib import Path

from common import ROOT, write_json
from report import records


def assess(folder):
    rows = [r for r in records(folder) if r.get('engine') == 'native']
    index = {(r['model'], r['view'], r.get('multipleThreads', False)): r for r in rows if r.get('phase') == 'complete'}
    findings = []
    perspective = index.get(('view-sources', 'Perspective must error', False))
    if perspective:
        findings.append({'property': 'perspective view rejection', 'model': 'view-sources', 'view': 'Perspective must error',
                         'pass': perspective.get('status') == 'view_error' and perspective.get('error') == 'perspective_not_supported',
                         'status': perspective.get('status'), 'error': perspective.get('error')})
    beam = index.get(('curves-points', 'Top', False))
    if beam:
        for source in ('rebar--0.6', 'rebar-0.6'):
            c = beam.get('sourceCounts', {}).get(source, {})
            findings.append({'property': 'analytic beam rebar occlusion', 'model': 'curves-points', 'view': 'Top', 'source': source,
                             'pass': c.get('Hidden', 0) > 0 and c.get('Visible', 0) == 0, 'counts': c})
        for source, expected in [('front-point', 'PointVisible'), ('outside-point', 'PointVisible'), ('behind-point', 'PointHidden')]:
            c = beam.get('sourceCounts', {}).get(source, {})
            findings.append({'property': 'analytic point occlusion', 'model': 'curves-points', 'view': 'Top', 'source': source,
                             'pass': c.get(expected) == 1, 'counts': c})
    annotation = index.get(('annotations', 'Top', False))
    if annotation:
        findings.append({'property': 'unsupported annotation census', 'model': 'annotations', 'view': 'Top',
                         'pass': len(annotation.get('unsupported', [])) == 5 and annotation.get('acceptedObjects') == 0,
                         'types': [x['type'] for x in annotation.get('unsupported', [])]})
    glass = index.get(('glass', 'Front clear', False))
    if glass:
        c = glass.get('sourceCounts', {}).get('column-behind-glass', {})
        findings.append({'property': 'product clear-glass expectation (not implemented by raw HLD)', 'model': 'glass', 'view': 'Front clear',
                         'pass': c.get('Hidden', 0) == 0 and c.get('Visible', 0) > 0, 'counts': c,
                         'interpretation': 'Material policy gap; furniture/column remain dashed behind glazing in the SVG.'})
    for view in ('Top', 'SW', 'Plan cut'):
        base = index.get(('units-coords-m', view, False))
        if not base:
            continue
        for suffix, scale in [('mm', 1000), ('ft', 1 / .3048), ('offset', 1)]:
            other = index.get(('units-coords-' + suffix, view, False))
            if not other:
                continue
            errors = {key: abs(base.get('lengths', {}).get(key, 0) - other.get('lengths', {}).get(key, 0) / scale)
                      for key in set(base.get('lengths', {})) | set(other.get('lengths', {}))}
            findings.append({'property': 'units/translation class-length metamorphism', 'model': other['model'], 'view': view,
                             'pass': max(errors.values(), default=0) < .001, 'classLengthErrorsMetres': errors,
                             'samePieceCounts': base.get('pieceCounts') == other.get('pieceCounts'),
                             'limit': 'Aggregated lengths/counts, not full-locus equivalence.'})
    for r in rows:
        if not r.get('multipleThreads') or r.get('phase') != 'complete':
            continue
        other = index.get((r['model'], r['view'], False))
        if not other:
            continue
        differences = {key: r.get('lengths', {}).get(key, 0) - other.get('lengths', {}).get(key, 0)
                       for key in set(r.get('lengths', {})) | set(other.get('lengths', {}))}
        same = r.get('pieceCounts') == other.get('pieceCounts') and max(map(abs, differences.values()), default=0) < .001
        findings.append({'property': 'native threading class-length/count comparison', 'model': r['model'], 'view': r['view'],
                         'pass': same, 'lengthDifferenceOnMinusOff': differences,
                         'offCounts': other.get('pieceCounts'), 'onCounts': r.get('pieceCounts'),
                         'limit': 'Single trials; equal aggregates do not prove equal curves.'})
    for tier in ('XS', 'S', 'M', 'L', 'XL', 'XXL'):
        for r in rows:
            if r.get('tier') != tier or r.get('flavor') != 'blocks' or r.get('phase') != 'complete':
                continue
            other = index.get((f'perf-{tier}-exploded', r['view'], r.get('multipleThreads', False)))
            if not other:
                continue
            differences = {key: r.get('lengths', {}).get(key, 0) - other.get('lengths', {}).get(key, 0)
                           for key in set(r.get('lengths', {})) | set(other.get('lengths', {}))}
            findings.append({'property': 'blocks/exploded native aggregate comparison', 'model': r['model'], 'view': r['view'],
                             'multipleThreads': r.get('multipleThreads'), 'pass': max(map(abs, differences.values()), default=0) < .001,
                             'lengthDifferenceBlocksMinusExploded': differences,
                             'samePieceCounts': r.get('pieceCounts') == other.get('pieceCounts'),
                             'limit': 'Inputs use identical prototypes/placements; this measures native outputs, not full-locus correctness.'})
    write_json(folder / 'assessment.json', {'findings': findings})
    print('Limited checks', len(findings), 'passed', sum(f['pass'] for f in findings), 'differences/gaps', sum(not f['pass'] for f in findings))
    return findings


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--results', type=Path, default=ROOT / 'artifacts/baseline')
    assess(parser.parse_args().results)
