"""Bind completed compiled-gate rows and independent interval certificates.
Never changes raw rows; no Rhino solve is performed. Non-certified samples stay strict.
"""
import argparse,copy,hashlib,json
from pathlib import Path

def digest(path):
 with Path(path).open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
def main():
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('directory',type=Path);a=p.parse_args()
 raw=json.loads((a.directory/'gate.json').read_text());rows=copy.deepcopy(raw['rows']);certificates=[]
 for row in rows:
  name=Path(row['model'].replace('\\','/')).stem+'__'+row['view'];prefix=Path(row.get('evidencePrefix',a.directory/name));file=Path(str(prefix)+'.interval-witnesses.json')
  if not file.exists():continue
  cert=json.loads(file.read_text());assert cert['accepted'] and cert['FaceTests']==cert['completePairs']
  assert cert['coreSha256'].lower()==row['coreSha256'].lower()
  assert all(digest(str(prefix)+suffix)==value.lower() for suffix,value in cert['inputs'].items())
  wanted={proof['sample'] for proof in cert['proofs']};checks={}
  with Path(str(prefix)+'.exact-witnesses.jsonl').open() as stream:
   for line in stream:
    witness=json.loads(line)
    if witness['sample'] in wanted:
     assert witness['sample'] not in checks
     checks[witness['sample']]=witness
  samples=set()
  for proof in cert['proofs']:
   i=proof['sample'];check=checks[i]
   assert i not in samples and check['classification'] is None and check['routedClass']=='Visible' and check['check']['Class']==1
   assert proof['classification']=='toleranceArtefact' and proof['check']['Class']==0 and proof['distance']<=row['tolerance']
   assert row['NativeCalls']==0 or proof.get('curvedDecision')=='Visible' and '.foreign-records.json' in cert['inputs']
   samples.add(i)
  count=len(samples);assert count<=row['exactContradictions']
  row['rawExactContradictions']=row['exactContradictions'];row['exactContradictions']-=count;row['unexplained']-=count
  row['classes']['unclassified']-=count;row['classes']['toleranceArtefact']+=count
  row['intervalCertificate']=dict(file=str(file),sha256=digest(file),samples=sorted(samples))
  native=json.loads(Path(str(prefix)+'.native-output.json').read_text());actual=json.loads(Path(str(prefix)+'.kernel-output.json').read_text())
  structural=row.get('executionFailure',False) or row.get('producerOrExecutionFailure',False) or row['nativeError'] is not None or row['kernelError'] is not None or native['RegionAreas']!=actual['RegionAreas'] or native['RegionLoops']!=actual['RegionLoops']
  row['bugs']=1 if structural or row['exactContradictions'] else (0 if row['unexplained']==0 else None)
  row['passed']=not structural and not row['exactContradictions'] and row['unexplained']==0
  certificates.append(row['intervalCertificate'])
 classes={}
 for row in rows:
  for k,v in row['classes'].items():classes[k]=classes.get(k,0)+v
 result=dict(raw,rows=rows,passed=all(r['passed'] for r in rows),rawGateSha256=digest(a.directory/'gate.json'),verification='compiled full inventory plus separately hash-bound independent exact interval certificates; unchanged distance tolerance',verifierSha256=digest(__file__),classes=classes,unexplained=sum(r['unexplained'] for r in rows),intervalCertificates=certificates)
 (a.directory/'verified-gate.json').write_text(json.dumps(result,indent=2))
 print(json.dumps(dict(cases=len(rows),passed=sum(r['passed'] for r in rows),unexplained=result['unexplained'],classes=classes),indent=2))
if __name__=='__main__':main()
