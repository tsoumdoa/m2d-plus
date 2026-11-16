"""Summarise hash-bound census receipts without changing engine/gate verdicts."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import re


def read(p):
    return json.loads(p.read_text())


def digest(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()


def load(folder):
    result = {}
    for receipt in folder.glob('*/receipt.json'):
        row = read(receipt)
        assert row['watch']['status'] == 'exited' and row['watch']['exitCode'] == 0, receipt
        assert row['timings']['rows'][0]['failure'] is None, receipt
        dumpfile = next(receipt.parent.glob('*-1-census.json'))
        dump = read(dumpfile)
        causesfile = next(receipt.parent.glob('*-causes.json'), None)
        result[row['case']] = dict(row=row, dump=dump, causes=read(causesfile) if causesfile else None,
                                  receiptSha256=digest(receipt), dumpSha256=digest(dumpfile))
    return result


def identities(dump):
    unset, local, refused, unsupported = set(), set(), set(), set()
    for s in dump['diagnostics']:
        if s.startswith(('Unset segment ', 'Composition Unset ')):
            m = re.search(r'source=([^;]+)', s)
            if m and m[1]: unset.add(m[1])
        if s.startswith(('Local Unset retained:', 'Local composition Unset retained:')):
            m = re.search(r'source=([^;]+)', s)
            if m: local.add(m[1])
        if ': Uncertain: ' in s: refused.add(s.split(': Uncertain: ')[0])
        if ': Unsupported: ' in s: unsupported.add(s.split(': Unsupported: ')[0])
    return unset, local, refused, unsupported


def classified(entry):
    dump, evidence = entry['dump'], entry['causes']
    assert evidence is not None
    unset, local, refused, unsupported = identities(dump)
    result = Counter()
    for piece in evidence['pieces']:
        source = piece['SourceIdentity']
        for cause, length in piece['causes'].items():
            if cause == 'source/footprint quarantine':
                if source in unset or source in local:
                    cause = 'native Unset propagation / continuous-proof refusal'
                elif source in refused:
                    cause = 'section-certificate source quarantine'
                elif source in unsupported:
                    cause = 'unsupported-source quarantine'
                elif refused:
                    cause = 'section-certificate footprint quarantine'
                elif unsupported:
                    cause = 'unsupported-source footprint quarantine'
                elif any(s.startswith('Section recovery visibility proofs:') for s in dump['diagnostics']):
                    cause = 'section recovery continuous-proof refusal'
                else:
                    cause = 'other footprint quarantine'
            result[cause] += length
    actual = sum(p['length'] for p in dump['pieces'] if p['c'] == 'Unresolved')
    assert abs(sum(result.values())-actual) < max(1e-5, actual*1e-7), (sum(result.values()), actual)
    return dict(result)


def canonical(dump):
    return dict(error=dump['error'], quarantined=dump['quarantined'], pieces=[{k:p[k] for k in ('c','s','profile','exact','p')} for p in dump['pieces']], regions=dump['regions'])


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('root', type=Path)
    p.add_argument('--final-folder', default='final')
    p.add_argument('--before-folder', default='before')
    p.add_argument('--direct-baseline', action='store_true', help='Baseline already includes the cause ledger; no legacy cause replay is needed')
    a = p.parse_args()
    before, replay, final = (load(a.root / name) for name in (a.before_folder,a.before_folder if a.direct_baseline else 'before-causes',a.final_folder))
    assert len(before) == len(replay) == len(final) == 50
    assert before.keys() == replay.keys() == final.keys()
    rows, bc, fc = [], Counter(), Counter()
    for case, b in before.items():
        r, f = replay[case], final[case]
        assert b['row']['modelSha256'] == r['row']['modelSha256'] == f['row']['modelSha256']
        assert b['row']['engineSha256'] == r['row']['engineSha256']
        assert canonical(b['dump']) == canonical(r['dump']), 'Baseline replay changed classed loci: '+case
        old, new = classified(r), classified(f)
        bc.update(old); fc.update(new)
        def metrics(e):
            d=e['dump']; u,l,ref,unsupported=identities(d)
            piece_length=sum(p['length'] for p in d['pieces'])
            region_length=sum(loop['length'] for region in d['regions'] for loop in region['loops'])
            length=piece_length+region_length; unresolved=sum(p['length'] for p in d['pieces'] if p['c']=='Unresolved')
            # A static final-class view of the census, not a second Hidden-off
            # solve. In particular, a Hidden veto remains Unresolved drawing ink.
            displayed=[p for p in d['pieces'] if p['c']!='Hidden']
            displayed_length=region_length+sum(p['length'] for p in displayed)
            displayed_unresolved=sum(p['length'] for p in displayed if p['c']=='Unresolved')
            return dict(length=length,unresolved=unresolved,share=unresolved/length if length else 0,
                        visibilityCurveShare=unresolved/piece_length if piece_length else 0,regionBoundaryLength=region_length,
                        excludingFinalHiddenPieces=dict(length=displayed_length,unresolved=displayed_unresolved,
                            share=displayed_unresolved/displayed_length if displayed_length else 0),
                        quarantinedView=d['quarantined'],wholeQuarantinedSources=len((u-l)|ref|unsupported),
                        wholeQuarantineByCause={'native Unset':len(u-l),'section refusal':len(ref),
                            'unsupported section/source':len(unsupported)},
                        unresolvedSources=len({p['s'] for p in d['pieces'] if p['c']=='Unresolved'}),
                        regionCount=len(d['regions']),tinyUnresolved=sum(p['length'] for p in d['pieces'] if p['c']=='Unresolved' and p['length']<=2*d['tolerance']),
                        verifierBudgetSkips=sum(int(m[1]) for s in d['diagnostics'] if (m:=re.search(r'; skipped=(\d+);',s))),
                        receiptSha256=e['receiptSha256'],dumpSha256=e['dumpSha256'])
        auditfile=next((a.root/a.final_folder/case).glob('*-restored-audit.json')); audit=read(auditfile)
        assert audit['bodyRayPairs']==audit['expectedPairs'] and audit['wrongVisible']==0, auditfile
        previous=list((a.root/'continuous-proof-audit-initial'/case).glob('*-1-census.json'))
        repeat=None
        if previous:
            current=next((a.root/a.final_folder/case).glob('*-1-census.json'))
            repeat=dict(byteIdentical=previous[0].read_bytes()==current.read_bytes(),previousSha256=digest(previous[0]),currentSha256=digest(current))
            assert repeat['byteIdentical'], 'Changed drawing repeat: '+case
        rows.append(dict(case=case,before=metrics(b),after=metrics(f),beforeCauses=old,afterCauses=new,
                         unchangedClassedLoci=canonical(b['dump'])==canonical(f['dump']),repeat=repeat,audit=audit,auditSha256=digest(auditfile)))
    summary=dict(cases=50,beforeCauses=bc,afterCauses=fc,rows=rows,
                 beforeUnresolved=sum(r['before']['unresolved'] for r in rows),afterUnresolved=sum(r['after']['unresolved'] for r in rows),
                 newVisibleContradictions=sum(r['audit']['wrongVisible'] for r in rows),
                 independentlyAuditedSamples=sum(r['audit']['samples'] for r in rows),
                 allBodyRayPairs=sum(r['audit']['bodyRayPairs'] for r in rows))
    summary['quarantineCauseCounts']={phase:{cause:{
        'views':sum(r[phase]['wholeQuarantineByCause'][cause]>0 for r in rows),
        'viewSourcePairs':sum(r[phase]['wholeQuarantineByCause'][cause] for r in rows)}
        for cause in ('native Unset','section refusal','unsupported section/source')}
        for phase in ('before','after')}
    def hidden_audit(folder):
        files=sorted(folder.glob('*/*-hidden-audit.json'))
        if not files:return None
        assert len(files)==50, 'Incomplete Hidden-direction audit'
        audits=[read(f) for f in files]
        assert all(a['bodyRayPairs']==a['expectedPairs'] for a in audits)
        assert all('capContainmentTests' in a and a['capContainmentHits']<=a['capContainmentTests'] for a in audits)
        harnesses={read(f.parent/'receipt.json')['harnessSha256'] for f in files}
        assert len(harnesses)==1, 'Mixed Hidden-audit observers'
        return dict(cases=50,harnessSha256=next(iter(harnesses)),samples=sum(a['samples'] for a in audits),bodyRayPairs=sum(a['bodyRayPairs'] for a in audits),
                    blocked=sum(a['blockedSamples'] for a in audits),margin=sum(a['marginSamples'] for a in audits),
                    wrongHidden=sum(a['wrongHidden'] for a in audits),unchangedHiddenCurves=sum(a['unchangedHiddenCurves'] for a in audits),
                    uncastableRays=sum(a.get('uncastableRays',0) for a in audits),
                    capContainmentTests=sum(a['capContainmentTests'] for a in audits),
                    capContainmentHits=sum(a['capContainmentHits'] for a in audits),
                    capContactMargins=sum(a.get('capContactMargins',0) for a in audits),
                    roundoffUnchangedHiddenLines=sum(a.get('roundoffUnchangedHiddenLines',0) for a in audits),
                    files=[dict(path=str(f.resolve()),sha256=digest(f)) for f in files])
    summary['hiddenAuditBefore']=hidden_audit(a.root/a.before_folder)
    summary['hiddenAuditAfter']=hidden_audit(a.root/a.final_folder)
    if summary['hiddenAuditAfter']:assert summary['hiddenAuditAfter']['wrongHidden']==0
    if summary['hiddenAuditBefore'] and summary['hiddenAuditAfter']:
        assert summary['hiddenAuditBefore']['harnessSha256']==summary['hiddenAuditAfter']['harnessSha256']
    visits=Counter()
    for row in final.values():
        for diagnostic in row['dump']['diagnostics']:
            match=re.match(r'Restored Hidden ray audit: intervals=(\d+); before length=([^;]+); kept length=([^;]+); veto length=([^;]+)\.',diagnostic)
            if match:
                visits['intervals']+=int(match[1]);visits['beforeLength']+=float(match[2]);visits['keptLength']+=float(match[3]);visits['vetoLength']+=float(match[4])
    summary['restoredHiddenVerificationVisits']=dict(visits)
    (a.root/'summary.json').write_text(json.dumps(summary,indent=2))
    lines=['| Model / view | Unresolved length before → after | Share before → after | Whole-source quarantines before → after |',
           '|---|---:|---:|---:|']
    for r in rows:
        b,f=r['before'],r['after']
        lines.append(f"| {r['case']} | {b['unresolved']:.6f} → {f['unresolved']:.6f} | {100*b['share']:.4f}% → {100*f['share']:.4f}% | {b['wholeQuarantinedSources']} → {f['wholeQuarantinedSources']} |")
    (a.root/'census-table.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    stagefile=a.root/'after-fix-1-summary.json'
    stage=read(stagefile)['afterCauses'] if stagefile.exists() else {}
    cause_lines=(['| Cause (ranked by baseline affected length) | Before | Final |', '|---|---:|---:|'] if a.direct_baseline else
                 ['| Cause (ranked by baseline affected length) | Before | After fix 1 | Final |', '|---|---:|---:|---:|'])
    for cause in sorted(bc.keys()|fc.keys(),key=lambda c:(-bc[c],c)):
        middle='' if a.direct_baseline else f' {stage.get(cause,0):.6f} |'
        cause_lines.append(f'| {cause} | {bc[cause]:.6f} |{middle} {fc[cause]:.6f} |')
    zero=' 0 | 0 |' if a.direct_baseline else ' 0 | 0 | 0 |'
    cause_lines.extend([f'| {label} |{zero}' for label in ('Unsupported-source whole-view quarantine',
                        'Verifier budget skips (Unresolved length)','Tie rule (Unresolved length)')])
    (a.root/'cause-table.md').write_text('\n'.join(cause_lines)+'\n',encoding='utf-8')
    print(json.dumps({k:summary[k] for k in ('cases','beforeUnresolved','afterUnresolved','newVisibleContradictions','independentlyAuditedSamples','allBodyRayPairs')},indent=2))


if __name__ == '__main__':
    main()
