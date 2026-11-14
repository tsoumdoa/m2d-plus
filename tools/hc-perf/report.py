"""Verify the winning snapshot and write a compact, self-contained result chart."""
from pathlib import Path
import hashlib
import html
import json
import math

ROOT=Path(__file__).resolve().parents[2]


def main():
    records=[json.loads(line) for line in (ROOT/'hillclimb/scores.jsonl').read_text().splitlines()]
    baseline=next(r for r in records if r['iteration']==3)
    candidates=[r for r in records if r['iteration']>=4]
    streak=0
    for r in candidates:
        streak=0 if r.get('kept') else streak+1
    if len(candidates)<8 and streak<3:raise RuntimeError('Climb has not reached its stopping rule')
    winner=min([r for r in candidates if r.get('kept') and r['gate']['passed']],key=lambda r:r['primary'])
    for name,sha in winner['sourceSha256'].items():
        if hashlib.sha256((ROOT/'src/M2DPlus.Core/Visibility'/name).read_bytes()).hexdigest()!=sha:
            raise RuntimeError('Current source differs from winning snapshot: '+name)
    directory=ROOT/f'artifacts/hc-perf/iteration-{winner["iteration"]}'
    rows=[]
    for path in directory.glob('*__*.json'):
        if path.name.count('.')!=1:continue
        row=json.loads(path.read_text());old=json.loads((ROOT/'artifacts/hc-perf/validated-baseline/evidence'/path.name).read_text())
        if row['modelSha256']!=old['modelSha256']:raise RuntimeError('Benchmark model changed: '+path.name)
        rows.append(row)
    if len(rows)!=183 or len({r['engineSha256'] for r in rows})!=1:raise RuntimeError('Incomplete/mixed-engine winning run')
    if any(not r['gate']['passed'] for r in rows):raise RuntimeError('Failed winning row')
    native_cases=sum(r.get('nativeValidation',{}).get('passed',False) for r in rows)
    oracle_cases=sum(r['oracleRequested'] for r in rows)
    if native_cases!=165 or oracle_cases!=165:raise RuntimeError('Incomplete native/reference view gates')
    manifest={(c['path'],c['view']):c for c in json.loads((directory/'manifest.json').read_text())}
    primary_cases=sum(manifest[r['model'],r['view']]['primary'] and r['edges']>0 for r in rows)
    if primary_cases!=132:raise RuntimeError('Primary corpus changed')
    for pattern,count in [('pair-*.json',15),('*.rigid.json',4),('*.tiles.json',4)]:
        files=list(directory.glob(pattern))
        if len(files)!=count or any(not json.loads(p.read_text())['passed'] for p in files):
            raise RuntimeError('Incomplete/failed gate: '+pattern)
    if sum(len(json.loads(p.read_text())['tiles']) for p in directory.glob('*.tiles.json'))!=16:
        raise RuntimeError('Incomplete exact crop coverage')
    ratios=winner['secondary']['nativeRatios'];native={}
    for tier in ('S','M','L','XL'):
        values=[v for k,v in ratios.items() if k.startswith('perf-'+tier+'-')]
        native[tier]=math.exp(sum(map(math.log,values))/len(values)) if values else None
    result={'winner':winner['iteration'],'kept':[r['iteration'] for r in candidates if r.get('kept')],
            'attempts':len(candidates),'nonImprovingStreak':streak,'primary':winner['primary'],
            'primaryViews':primary_cases,'nativeCases':native_cases,'oracleCases':oracle_cases,
            'primarySpeedup':baseline['primary']/winner['primary'],
            'perfGeomean':winner['secondary']['perfTierGeomeanSeconds'],
            'perfSpeedup':baseline['secondary']['perfTierGeomeanSeconds']/winner['secondary']['perfTierGeomeanSeconds'],
            'tierGeomeanSeconds':winner['secondary']['tierGeomeanSeconds'],
            'archivedNativeNorthSwRatios':native,'archivedNativePerViewRatios':ratios,
            'nativeRatioScope':winner['secondary']['nativeRatioScope'],
            'nativeDisagreementSamples':winner['secondary']['nativeDisagreementCounts'],
            'fixedSupplementalKernelBugs':1,'unexplained':0,'winningScore':str(directory/'score.json')}
    dest=ROOT/'artifacts/hc-perf';(dest/'final-report.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    gains={k:baseline['secondary']['tierGeomeanSeconds'][k]/winner['secondary']['tierGeomeanSeconds'][k] for k in ('S','M','L','XL')}
    maximum=max(1,max(gains.values()))*1.12
    bars=[]
    for tier,value in gains.items():
        before=baseline['secondary']['tierGeomeanSeconds'][tier];after=winner['secondary']['tierGeomeanSeconds'][tier]
        tip=html.escape(f'{before:.4f} s -> {after:.4f} s; geometric mean of six views')
        bars.append(f'<div class="row" title="{tip}"><span>{tier}</span><div class="track"><i style="width:{value/maximum*100:.4f}%"></i><b style="left:{100/maximum:.4f}%"></b></div><strong>{value:.2f}×</strong></div>')
    page='''<!doctype html><html><head><meta charset="utf-8"><style>
    body{margin:0;font-family:var(--font-sans);color:var(--foreground)}
    main{width:100%}p{margin:0 0 14px}.label{font-weight:600;font-size:14px}
    .row{display:grid;grid-template-columns:28px minmax(0,1fr) 55px;gap:12px;align-items:center;margin:16px 0;font-size:13px}
    .track{height:22px;position:relative;background:var(--secondary);border-radius:4px;overflow:hidden}
    i{height:100%;display:block;background:var(--chart-1);border-radius:4px}
    b{position:absolute;top:0;bottom:0;border-left:2px dashed var(--foreground);opacity:.5}
    strong{text-align:right;font-variant-numeric:tabular-nums}small,details{color:var(--muted-foreground);font-size:12px;line-height:1.5}
    details{margin-top:14px}summary{cursor:pointer}details p{margin-top:10px}
    </style></head><body><main><p class="label">Planar kernel speedup over the validated baseline</p>'''
    page+=''.join(bars)
    page+='<small>Dashed line: baseline = 1×. Each tier includes North, SW and Top in blocks and exploded form. Hover for seconds.</small>'
    page+='<details><summary>Measurement scope</summary><p>Fresh extraction, index, solve and drawing-locus union times; all correctness gates pass. The chart compares the planar kernel with its validated baseline. Archived mixed-native timing ratios are separate; uncut Top and XL native references are unavailable.</p></details></main></body></html>'
    (dest/'final-report.html').write_text(page,encoding='utf-8')
    print(json.dumps(result,indent=2))


if __name__=='__main__':main()
