"""Summarise a routing candidate without accepting unexplained samples."""
import argparse,json,math,hashlib
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
PRIMARY=('perf-M-blocks__North','perf-M-blocks__SW','perf-L-blocks__North','curved-perf-S-blocks__SW')
def gm(xs):return math.exp(sum(math.log(x) for x in xs)/len(xs)) if xs and all(x>0 for x in xs) else None
def main():
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('directory',type=Path);p.add_argument('--baseline',type=Path,default=Path('C:/Users/tomosandego/.t3/worktrees/m2d-plus/t3-cb3ea1b1-387e150576fde9fd/artifacts/hc-perf/composition'));a=p.parse_args()
 manifest=json.loads((a.directory/'manifest.json').read_text());rows={};regress=[]
 final_verified=a.directory/'verified-gate.json'
 verified_result=json.loads(final_verified.read_text()) if final_verified.exists() else None
 verified_rows={Path(r['model'].replace('\\','/')).stem+'__'+r['view']:r for r in verified_result['rows']} if verified_result else {}
 verified=a.baseline/'verified-gate.json'
 snapshot=json.loads(verified.read_text()) if verified.exists() else None
 before_rows={Path(r['model'].replace('\\','/')).stem+'__'+r['view']:r for r in snapshot['rows']} if snapshot else {}
 for case in manifest:
  f=Path(case['output']+'.gate.json')
  if not f.exists():continue
  row=verified_rows.get(f.name[:-10]) or json.loads(f.read_text());rows[f.name[:-10]]=row
  old=a.baseline/f.name
  if f.name[:-10] in before_rows or old.exists():
   before=before_rows.get(f.name[:-10]) or json.loads(old.read_text())
   if row['modelSha256'].lower()!=before['modelSha256'].lower():raise RuntimeError('model mismatch: '+str(f))
   if row['unexplained']>before['unexplained']:regress.append(dict(case=f.name,before=before['unexplained'],after=row['unexplained']))
 gate=verified_result or (json.loads((a.directory/'gate.json').read_text()) if (a.directory/'gate.json').exists() else {})
 primary={name:rows[name] for name in PRIMARY if name in rows}
 perf={name:dict(routed=r['endToEndSeconds'],native=r['nativeEndToEndSeconds'],speedup=r['nativeEndToEndSeconds']/r['endToEndSeconds']) for name,r in primary.items() if r.get('nativeSolveSeconds',0)>0 and r.get('includesGhPublication')}
 tiers={}
 for tier,prefix in [('M','perf-M-'),('L','perf-L-'),('curved-S','curved-perf-S-')]:
  rs=[v for k,v in perf.items() if k.startswith(prefix)]
  if rs:tiers[tier]=dict(views=len(rs),routed=gm([r['routed'] for r in rs]),native=gm([r['native'] for r in rs]),speedup=gm([r['speedup'] for r in rs]))
 classes={}
 for row in rows.values():
  for k,v in row['classes'].items():classes[k]=classes.get(k,0)+v
 report=dict(candidate=a.directory.name,complete=gate.get('completeInventory',False) and len(rows)==233,gatePassed=gate.get('passed',False),completed=len(rows),passed=sum(r['passed'] for r in rows.values()),unexplained=sum(r['unexplained'] for r in rows.values()),classes=classes,knownBugs=sum(r.get('bugs') or 0 for r in rows.values()),bugClearanceUnknown=sum(r.get('bugs') is None for r in rows.values()),regressions=regress,performance=perf,tiers=tiers,primarySpeedup=gm([r['speedup'] for r in perf.values()]) if len(perf)==4 else None,note='Correctness cache durations are excluded. Before/after counters are not an acceptance waiver.')
 if snapshot:report['baseline']=dict(file=str(verified),sha256=hashlib.sha256(verified.read_bytes()).hexdigest(),cases=len(before_rows),passed=sum(r['passed'] for r in before_rows.values()),unexplained=sum(r['unexplained'] for r in before_rows.values()))
 (a.directory/'routing-summary.json').write_text(json.dumps(report,indent=2))
 scores=ROOT/'hillclimb/routing/scores.jsonl';previous=[json.loads(line) for line in scores.read_text().splitlines()] if scores.exists() else []
 previous=[r for r in previous if r.get('candidate')!=a.directory.name];previous.append(report);scores.write_text(''.join(json.dumps(r)+'\n' for r in previous))
 print(json.dumps({k:report[k] for k in ['candidate','complete','completed','passed','unexplained','regressions','tiers']},indent=2))
if __name__=='__main__':main()
