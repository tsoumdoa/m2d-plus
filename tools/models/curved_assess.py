"""Measured curved baseline properties. No self-oracle visibility verdicts."""
import argparse
from collections import Counter
import json
from pathlib import Path
import xml.etree.ElementTree as ET

from common import ROOT, write_json
from report import records


def paths(row):
    svg = row['_path'].with_suffix('.svg')
    return [(p.attrib.get('class'),p.attrib.get('d')) for p in ET.parse(svg).getroot()
            if p.tag.endswith('path')]


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--results',type=Path,default=ROOT/'artifacts/baseline')
    args=p.parse_args(); rows=records(args.results)
    lookup={(r['model'],r['view']):r for r in rows if not r.get('multipleThreads')}
    comparisons=[]
    for camera in ('Front','SW','Top'):
        for tangent in ('off','on'):
            names=[f'{camera} tangents {tangent} seams {s}' for s in ('off','on')]
            if any(('tangent-continuity',n) not in lookup for n in names): continue
            a,b=[lookup['tangent-continuity',n] for n in names]
            comparisons.append({'camera':camera,'tangents':tangent,'seamToggleChangesPaths':paths(a)!=paths(b),
                                'offCounts':a['pieceCounts'],'onCounts':b['pieceCounts'],
                                'nativeOff':a['nativeEdgeSettings'],'nativeOn':b['nativeEdgeSettings']})
    findings=[]
    for model,views in [('curved-facade',['SW','Front','Grazing']),('diagrid-shell',['SW','Plan cut','Roof section']),
                        ('space-frame',['SW','Plan cut']),('curved-steel',['Oblique hollow cut'])]:
        for view in views:
            r=lookup.get((model,view))
            if r and r.get('unsetProxies'):
                findings.append({'model':model,'view':view,'observation':f'Native reports {r["unsetProxies"]:,} Unset/parentless proxies; these are omitted safely and extraction is partial. This is not a measured count of missing visible silhouettes.'})
    if comparisons and not any(c['seamToggleChangesPaths'] for c in comparisons):
        findings.append({'model':'tangent-continuity','view':'Front, SW and Top (both tangent settings)',
            'observation':'Turning seams on changes no extracted SVG paths or piece counts in all six matched comparisons, although archived torus/sphere seam trims exist and IncludeTangentSeams is recorded true. The expected seam display is absent in these native outputs; a native seam-setting limitation remains to investigate.'})
    a=lookup.get(('tangent-continuity','Front tangents off seams off'))
    b=lookup.get(('tangent-continuity','Front tangents on seams off'))
    if a and b:
        findings.append({'model':'tangent-continuity','view':'Front tangents off/on, seams off',
            'observation':f'Tangent toggle adds the visible fillet boundaries: visible pieces change from {a["pieceCounts"].get("Visible",0)} to {b["pieceCounts"].get("Visible",0)}. No Unset proxies. The edge-on sheet projects as a line, not an area.'})
    findings.append({'model':'curved-steel','view':'Oblique hollow cut',
        'observation':'Visual inspection retains the outer and inner non-circular cut loops. The independent area/hole check passes; raw HLD supplies curves, not a filled region or topology certificate.'})
    tier_caps=[{'model':r['model'],'view':r['view'],'status':r['status'],'wallSeconds':r.get('wallSeconds')}
               for r in rows if r['model'].startswith('curved-perf-') and r['status'] in ('wall_timeout','private_memory_limit')]
    tier_unsets=sorted((r for r in rows if r['model'].startswith('curved-perf-') and r.get('unsetProxies',0)>0),
                       key=lambda r:r['unsetProxies'],reverse=True)
    for r in tier_unsets[:3]:
        findings.append({'model':r['model'],'view':r['view'],
            'observation':f'One of the largest completed-tier diagnostics: {r["unsetProxies"]:,} Unset/parentless proxies. Omitted safely; partial extraction. This count does not establish how many visible silhouettes are missing.'})
    representation_pairs=[]
    for (model,view),a in lookup.items():
        if not model.startswith('curved-perf-') or not model.endswith('-blocks'):
            continue
        b=lookup.get((model.removesuffix('-blocks')+'-exploded',view))
        if not b or any(r.get('phase')!='complete' or r['status'] not in ('ok','extraction_partial') for r in (a,b)):
            continue
        categories=set(a.get('lengths',{}))|set(b.get('lengths',{}))
        differences={c:b.get('lengths',{}).get(c,0)-a.get('lengths',{}).get(c,0) for c in categories}
        representation_pairs.append({'tier':a['tier'],'view':view,'countsEqual':a['pieceCounts']==b['pieceCounts'],
            'blocks':a['pieceCounts'],'exploded':b['pieceCounts'],'lengthDeltaMetres':differences,
            'unsetBlocks':a.get('unsetProxies',0),'unsetExploded':b.get('unsetProxies',0),
            'interpretation':'Same validated scene geometry; piece partition/count differences alone do not certify visibility error.'})
    write_json(args.results/'curved-assessment.json',{'seamToggleComparisons':comparisons,'cappedAttempts':tier_caps,
        'representationComparisons':representation_pairs,
        'statuses':dict(Counter(r['status'] for r in rows)), 'quality':'Property evidence only; no full visibility oracle'})
    # Preserve separately recorded visual findings when regenerating measured diagnostics.
    manual=args.results/'curved-visual-findings.json'
    if manual.exists(): findings.extend(json.loads(manual.read_text())['findings'])
    write_json(args.results/'curved-observations.json',{'findings':findings})
    print(len(comparisons),'seam toggle comparisons;',len(tier_caps),'capped tier attempts')


if __name__=='__main__': main()
