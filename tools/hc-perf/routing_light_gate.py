"""Owner's branch merge gate; preserves the stricter whole-output verdict.

Unknown comparisons are Unresolved/Info, never certified Visible. Large-model
coverage is non-blocking for this branch; the full gate remains a beta requirement.
"""
import argparse,hashlib,json,subprocess,sys
from pathlib import Path

def digest(path):
 with Path(path).open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
def stem(path):return Path(str(path).replace('\\','/')).stem
def write(path,value):path.write_text(json.dumps(value,indent=2))
def main():
 p=argparse.ArgumentParser(description=__doc__)
 p.add_argument('--inventory',type=Path,required=True);p.add_argument('--output',type=Path,required=True)
 p.add_argument('--hidden-audit',type=Path,help='Final 50-view, all-body wrong-Hidden audit; included in the gate verdict')
 p.add_argument('--hidden-audit-before',type=Path,help='Baseline audit for before/after counts')
 p.add_argument('directories',nargs='+',type=Path);a=p.parse_args();a.output.mkdir(parents=True,exist_ok=True)
 inventory=json.loads(a.inventory.read_text());rows={};files={}
 for directory in a.directories:
  for file in directory.glob('*.gate.json'):
   row=json.loads(file.read_text());name=stem(row['model'])+'__'+row['view']
   if row.get('contactRuleVersion')!=1 or row['classes'].get('tieConvention',0):raise RuntimeError('Fresh contact-rule evidence required: '+str(file))
   row['evidencePrefix']=str(file.resolve())[:-10];rows[name]=row;files[name]=file
 producers={tuple(r[k].lower() for k in ('engineSha256','coreSha256','harnessSha256')) for r in rows.values()}
 if len(producers)!=1:raise RuntimeError('Mixed compiled producers')
 write(a.output/'gate.json',dict(completeInventory=False,passed=False,cases=len(rows),rows=list(rows.values()),
  inventorySha256=digest(a.inventory),note='Required contact-rule inventory; raw whole-output verdict preserved separately'))
 subprocess.run([sys.executable,'-B',str(Path(__file__).with_name('routing_verify.py')),str(a.output)],check=True)
 verified=json.loads((a.output/'verified-gate.json').read_text());required=[];supplemental=[]
 requiredNames={stem(c['path'])+'__'+c['view'] for c in inventory if 'perf-L-' not in stem(c['path']) and 'curved-perf-' not in stem(c['path'])}
 for row in verified['rows']:
  name=stem(row['model'])+'__'+row['view'];prefix=Path(row['evidencePrefix']);entry=dict(case=name,
   rawGateSha256=digest(files[name]),unexplained=row['unexplained'],classes=row['classes'],
   knownBugs=row.get('bugs') or 0,wrongVisible=row.get('exactContradictions',0),
   status='Verified' if row['passed'] else 'Unresolved/Info',visiblePromotionAllowed=row['passed'])
  # A fresh complete planar oracle comparison can clear line discrepancies.
  # Points are excluded by that oracle and keep the case Unresolved/Info.
  reference=Path(str(prefix)+'.reference.json');inputfile=Path(str(prefix)+'.reference-input.json')
  if reference.exists() and inputfile.exists():
   proof=json.loads(reference.read_text())
   if proof['passed'] and proof['inputSha256'].lower()==digest(inputfile) and proof['coreSha256'].lower()==row['coreSha256'].lower():
    entry['planarReference']=dict(file=str(reference),sha256=digest(reference),inputSha256=digest(inputfile),
     actualOutputSha256=digest(str(prefix)+'.kernel-output.json'),completePairs=proof['completePairs'])
    failures=json.loads(Path(str(prefix)+'.failures.json').read_text())
    if not any(f['Point'] for f in failures):entry.update(status='Independent planar agreement',visiblePromotionAllowed=True)
  watch=Path(str(prefix)+'.watch.json')
  completed=False
  if watch.exists():
   w=json.loads(watch.read_text());entry['watchSha256']=digest(watch)
   completed=w['status']=='exited' and w['exitCode']==0 or w['status']=='case-completed-before-sibling-timeout' and w.get('gateSha256')==entry['rawGateSha256']
  entry['executionComplete']=completed
  (required if name in requiredNames else supplemental).append(entry)
 missing=sorted(requiredNames-{r['case'] for r in required});known=sum(r['knownBugs'] for r in required);wrong=sum(r['wrongVisible'] for r in required)
 result=dict(rule='Owner branch merge gate: zero known bugs / proven wrong Visible; uncertified comparisons remain Unresolved/Info',
  passed=not missing and known==0 and wrong==0 and all(r['executionComplete'] for r in required),
  required=len(requiredNames),completed=len(required),missing=missing,knownBugs=known,wrongVisible=wrong,
  infoCases=[r['case'] for r in required if r['status']=='Unresolved/Info'],classes=verified['classes'],
  requiredRows=required,supplementalRows=supplemental,full233GatePassed=False,
  betaRequirement='Rerun the full 233-view inventory before public beta',verifierSha256=digest(__file__))
 if a.hidden_audit:
  def hidden_evidence(folder,bind_producer):
   evidence=[]
   for file in sorted(folder.glob('*/*-hidden-audit.json')):
    audit=json.loads(file.read_text());receipt=file.parent/'receipt.json';r=json.loads(receipt.read_text())
    complete=r['watch']['status']=='exited' and r['watch']['exitCode']==0 and r['timings']['rows'][0]['failure'] is None
    if bind_producer:complete &= r['engineSha256'].lower()==next(iter(producers))[0]
    complete &= audit['bodyRayPairs']==audit['expectedPairs'] and audit['samples']==audit['blockedSamples']+audit['marginSamples']+audit['wrongHidden']
    complete &= audit['clippedAtCut']==r['case'].endswith(('__plan','__long'))
    complete &= 'capContainmentTests' in audit and audit.get('capContainmentHits',0)<=audit.get('capContainmentTests',0)
    evidence.append(dict(case=r['case'],file=str(file.resolve()),sha256=digest(file),receiptSha256=digest(receipt),complete=complete,
     harnessSha256=r['harnessSha256'],samples=audit['samples'],bodyRayPairs=audit['bodyRayPairs'],wrongHidden=audit['wrongHidden'],blocked=audit['blockedSamples'],margin=audit['marginSamples'],uncastableRays=audit.get('uncastableRays',0),capContainmentTests=audit.get('capContainmentTests',0),capContainmentHits=audit.get('capContainmentHits',0),capContactMargins=audit.get('capContactMargins',0),roundoffUnchangedHiddenLines=audit.get('roundoffUnchangedHiddenLines',0)))
   harnesses={r['harnessSha256'] for r in evidence}
   return dict(cases=len(evidence),harnessSha256=next(iter(harnesses)) if len(harnesses)==1 else None,complete=len(evidence)==50 and len(harnesses)==1 and all(r['complete'] for r in evidence),
    samples=sum(r['samples'] for r in evidence),bodyRayPairs=sum(r['bodyRayPairs'] for r in evidence),
    wrongHidden=sum(r['wrongHidden'] for r in evidence),blocked=sum(r['blocked'] for r in evidence),margin=sum(r['margin'] for r in evidence),uncastableRays=sum(r['uncastableRays'] for r in evidence),capContainmentTests=sum(r['capContainmentTests'] for r in evidence),capContainmentHits=sum(r['capContainmentHits'] for r in evidence),capContactMargins=sum(r['capContactMargins'] for r in evidence),roundoffUnchangedHiddenLines=sum(r['roundoffUnchangedHiddenLines'] for r in evidence),rows=evidence)
  result['hiddenAudit']=hidden_evidence(a.hidden_audit,True)
  if a.hidden_audit_before:result['hiddenAuditBefore']=hidden_evidence(a.hidden_audit_before,False)
  result['passed'] &= result['hiddenAudit']['complete'] and result['hiddenAudit']['wrongHidden']==0
  if a.hidden_audit_before:result['passed'] &= result['hiddenAuditBefore']['complete'] and result['hiddenAuditBefore']['harnessSha256']==result['hiddenAudit']['harnessSha256']
 write(a.output/'merge-gate.json',result)
 print(json.dumps({k:result[k] for k in ('passed','required','completed','missing','knownBugs','wrongVisible','infoCases')},indent=2))
if __name__=='__main__':main()
