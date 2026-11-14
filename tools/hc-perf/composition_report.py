"""Contextual archived-native comparison; never time native HLD in this report."""

from pathlib import Path

import csv

import json

import math

import statistics

import hashlib



ROOT=Path(__file__).resolve().parents[2]

OUT=ROOT/'artifacts/hc-perf/composition'

ARCHIVES=[Path('C:/Users/tomosandego/.t3/worktrees/m2d-plus/t3-3a78edd1-1cd89a1b1b9b38a1/artifacts/baseline'),

          Path('C:/Users/tomosandego/.t3/worktrees/m2d-plus/t3-d6bdd8e8-f1e6b3c68a0e104e/artifacts/baseline')]

NAMES={'North':'North elevation','SW':'SW axo','Long-section':'Section long overlap'}

def gm(values):return math.exp(statistics.mean(math.log(v) for v in values)) if values else None



def main():

    manifest=json.loads((OUT/'manifest.json').read_text())

    rows=[]; verified=[]

    engine=(lambda path:hashlib.sha256(Path(path).read_bytes()).hexdigest())(ROOT/'tools/hc-perf/bin/Release/net8.0-windows/M2DPlus.Rhino.dll')

    core=hashlib.sha256((ROOT/'tools/hc-perf/bin/Release/net8.0-windows/M2DPlus.Core.dll').read_bytes()).hexdigest()

    from collections import Counter

    classes=Counter()



    for c in manifest:

        p=Path(c['output']+'.gate.json')

        if not p.exists():continue

        r=json.loads(p.read_text())

        valid=(r.get('engineSha256','').lower()==engine and r.get('coreSha256','').lower()==core and

               r.get('view')==c['view'] and r.get('modelSha256','').lower()==hashlib.sha256(Path(c['path']).read_bytes()).hexdigest())

        watch=Path(c['output']+'.watch.json')

        if watch.exists():
            supervision=json.loads(watch.read_text())
            completed=(supervision.get('status')=='case-completed-before-coordinator-cancel' and
                       supervision.get('completedGateSha256')==hashlib.sha256(p.read_bytes()).hexdigest())
            valid=valid and (supervision.get('status')=='exited' or completed)
        else:valid=False

        if not valid:

            verified.append({'model':c['path'],'view':c['view'],'passed':False,'executionFailure':'stale producer or failed watchdog'})

            continue

        verified.append(r);classes.update(r.get('classes',{}))

        stem=Path(r['model']).stem;native=None

        if r['view'] in NAMES:

            for archive in ARCHIVES:

                for f in archive.glob(stem+'__*__off__native.json'):

                    n=json.loads(f.read_text())

                    if n.get('view')!=NAMES[r['view']]:continue

                    if n.get('fixtureSha256','').lower()!=r['modelSha256'].lower():continue

                    native={'path':str(f),'completeSeconds':n.get('completeResultSeconds'),'computeSeconds':n.get('computeSeconds')};break

                if native:break

        k=r['KernelEdges'];n=r['NativeEdges']

        # These are non-exclusive counts: native fragments also include submitted planar curves.

        route=r['kernelInfo'].splitlines()[1]

        import re

        m=re.search(r'planar edges submitted=(\d+)',route);submitted=int(m.group(1)) if m else 0

        e2e=r['endToEndSeconds']

        row={'model':stem,'view':r['view'],'captureSeconds':r['captureSeconds'],'prepareSeconds':r.get('prepareSeconds'),

             'drawingAndPresentationSeconds':r['solveSeconds'],'endToEndSeconds':e2e,'includesPresentation':r.get('includesPresentation',False),

             'kernelCandidateEdges':k,'planarEdgesAlsoSubmittedToNative':submitted,'curvedCandidateEdges':r.get('CurvedCandidateEdges'), 'nativeOutputFragments':r.get('NativeFragments',n-submitted),'nativeCalls':r['NativeCalls'],

             'kernelEdgeSharePercent':100*k/(k+r['CurvedCandidateEdges']) if r.get('CurvedCandidateEdges') is not None and k+r['CurvedCandidateEdges'] else None,

             'planarCandidatesAvoidingNativePercent':100*(k-submitted)/k if k else None,

             'nativeParticipationPercent':100*(submitted+r['CurvedCandidateEdges'])/(k+r['CurvedCandidateEdges']) if r.get('CurvedCandidateEdges') is not None and k+r['CurvedCandidateEdges'] else None,

             'archivedNativeCompleteSeconds':native['completeSeconds'] if native else None,

             'archivedNativeComputeSeconds':native['computeSeconds'] if native else None,

             'contextualCompleteRatio':native['completeSeconds']/e2e if native and native['completeSeconds'] else None,

             'archivedSource':native['path'] if native else None,'nativeCensusIncomplete':'invalid extraction=0' not in r['kernelInfo'], 'gatePassed':r['passed'],'disagreements':r['disagreements']}

        rows.append(row)

    OUT.mkdir(exist_ok=True,parents=True)

    missing=len(manifest)-len(verified)

    unexplained=sum(r.get('unexplained',0) for r in verified)

    (OUT/'verified-gate.json').write_text(json.dumps({'passed':missing==0 and all(r['passed'] for r in verified),

        'cases':len(manifest),'verifiedCompiledGateProducer':len(rows),'missing':missing,'unexplained':unexplained,'classes':dict(classes),

        'bugs':None if unexplained or any(not r['passed'] for r in verified) else 0,

        'engineSha256':engine,'coreSha256':core,'rows':verified},indent=2)+'\n')

    with (OUT/'per-view.csv').open('w',newline='') as stream:

        writer=csv.DictWriter(stream,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)

    tiers=[]

    for prefix in ('perf-M-','perf-L-','curved-perf-S-'):

        selected=[r for r in rows if r['model'].startswith(prefix)]

        match=[r for r in selected if r['contextualCompleteRatio']]

        tiers.append({'tier':prefix.strip('-'),'views':len(selected),'endToEndGM':gm([r['endToEndSeconds'] for r in selected]),

                      'matchedArchiveViews':len(match),'currentMatchedGM':gm([r['endToEndSeconds'] for r in match]),'archivedNativeCompleteGM':gm([r['archivedNativeCompleteSeconds'] for r in match]),'contextualCompleteRatioGM':gm([r['contextualCompleteRatio'] for r in match]),

                      'kernelCandidateEdges':sum(r['kernelCandidateEdges'] for r in selected),'submittedPlanarEdges':sum(r['planarEdgesAlsoSubmittedToNative'] for r in selected),

                      'nativeFragments':sum(r['nativeOutputFragments'] for r in selected),'curvedCandidateEdges':sum(r['curvedCandidateEdges'] or 0 for r in selected)})

    (OUT/'report.json').write_text(json.dumps({'note':'Archived native is the model-suite native adapter; current rows include route C and presentation where flagged. Contextual ratio, not controlled same-adapter timing. Top has no uncut archive substitute. Shares use drawable kernel edges and observed drawable native parent curves; planar native queries overlap both families. Native extraction failures make the census incomplete.',

                                              'deviations':['The whole-output gate has unexplained differences; no accepted performance win or completed pivot is claimed.','Gate producer predates the final collapsed sub-tolerance range-interval guard in DrawingPresentation; final runtime tests and renders include that guard. The whole-output gate was not rerun after this fix.','Tie/tolerance and kernel-owned disagreement audits remain incomplete. Native-order exceptions require full-scene reproduction and never apply to kernel-owned candidates.','Hatch pattern loci are not a separate oracle; Cut loops, region areas/loop counts and existing style/export tests are checked.','Large validation scenes use an isolated 16 GiB scheduler/watchdog; product global admission stays at 512 MiB.'], 'tiers':tiers,'rows':rows},indent=2)+'\n')

    for t in tiers:

        total=t['kernelCandidateEdges']+t['curvedCandidateEdges']

        t['kernelSharePercent']=100*t['kernelCandidateEdges']/total if total else None

        t['nativeParticipationPercent']=100*(t['submittedPlanarEdges']+t['curvedCandidateEdges'])/total if total else None

    # Rewrite the report with the derived participation shares.

    report=json.loads((OUT/'report.json').read_text());report['tiers']=tiers

    (OUT/'report.json').write_text(json.dumps(report,indent=2)+'\n')

    chart=[]

    maximum=max((max(t['currentMatchedGM'] or 0,t['archivedNativeCompleteGM'] or 0) for t in tiers),default=1) or 1

    for t in tiers:

        if not t['matchedArchiveViews']:continue

        bars=[]

        for label,key,colour in [('Current','currentMatchedGM','--chart-1'),('Archived native','archivedNativeCompleteGM','--chart-2')]:

            value=t[key]

            bars.append(f"<div class='barrow'><span>{label}</span><div class='track'><div class='bar' style='width:{100*value/maximum:.2f}%;background:var({colour})'></div></div><b>{value:.2f} s</b></div>")

        chart.append(f"<div class='tier'><strong>{t['tier']} · {t['matchedArchiveViews']} matched views</strong>{''.join(bars)}</div>")

    html="""<!doctype html><html><meta charset='utf-8'><style>

    :root{font-family:var(--font-sans);color:var(--foreground)}body{margin:0}.tier{margin:0 0 16px}.tier>strong{font-size:13px}.barrow{display:grid;grid-template-columns:106px 1fr 65px;gap:8px;align-items:center;font-size:12px;margin-top:6px}.track{height:16px;background:var(--secondary);border-radius:3px;overflow:hidden}.bar{height:16px}b{font-weight:500;text-align:right}p{font-size:12px;color:var(--muted-foreground);line-height:1.4;margin:8px 0 0}

    </style>"""+''.join(chart)+"<p>Geometric mean end-to-end seconds; lower is faster. Hash-matched archived inputs. Contextual comparison: archived model-suite native adapter versus current routing, sections and presentation. Correctness gate has not passed. Uncut Top has no archive match.</p></html>"

    (OUT/'timings.html').write_text(html,encoding='utf-8')

    print(json.dumps(tiers,indent=2))

if __name__=='__main__':main()

