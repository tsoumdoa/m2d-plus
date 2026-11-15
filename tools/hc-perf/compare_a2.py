"""Retain canonical comparisons, independent disputed-support oracles and witnesses."""
import argparse
from pathlib import Path
import json
import shutil
import subprocess
import sys

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'tools/models'))
from common import write_json, digest
from owned_job import run
from audit_native import audit


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('directory',type=Path)
    p.add_argument('--kernel',type=Path)
    p.add_argument('--select',default='')
    p.add_argument('--case',default='',help='Exact model__view identifier')
    a=p.parse_args();exe=['dotnet',str(ROOT/'tools/hc-perf/reader/bin/Release/net8.0-windows/M2DPlus.HcPerf.Reader.dll')]
    for path in sorted(a.directory.glob('*.native.json')):
        stem=path.name.removesuffix('.native.json')
        if a.case and stem!=a.case:continue
        if a.select not in stem:continue
        prefix=a.directory/stem
        kernel=(a.kernel/stem) if a.kernel else prefix
        meta=Path(str(prefix)+'.native-meta.json')
        if meta.exists():
            native_meta=json.loads(meta.read_text())
            kernel_row=json.loads(Path(str(kernel)+'.json').read_text())
            if native_meta['modelSha256']!=kernel_row['modelSha256']:
                raise RuntimeError(f'{stem}: native and kernel model hashes differ')
        if not Path(str(prefix)+'.loci.json').exists():shutil.copyfile(str(kernel)+'.loci.json',str(prefix)+'.loci.json')
        if not Path(str(prefix)+'.scene.json').exists():
            row=json.loads(Path(str(kernel)+'.json').read_text())
            command=exe+['--scene',row['model'],row['view'],str(prefix)+'.scene.json']
            watch=run(command,str(prefix)+'.scene.log',1800,16)
            write_json(str(prefix)+'.scene.watch.json',watch)
            if watch['status']!='exited':raise RuntimeError(watch['status'])
        scene=json.loads(Path(str(prefix)+'.scene.json').read_text())
        tolerance=scene.get('tolerance',.0001)
        spacing=.05*scene.get('metersToModelUnits',1)
        comparison=subprocess.run(exe+['--native-compare',str(path),str(prefix)+'.loci.json',str(tolerance),str(spacing)],capture_output=True,text=True,check=True)
        write_json(str(prefix)+'.canonical-comparison.json',json.loads(comparison.stdout))
        hashes={'nativeSha256':digest(path),'lociSha256':digest(str(prefix)+'.loci.json'),'sceneSha256':digest(str(prefix)+'.scene.json')}
        provenance=Path(str(prefix)+'.comparison-provenance.json')
        previous=json.loads(provenance.read_text()) if provenance.exists() else {}
        oracle_path=Path(str(prefix)+'.native-oracle.json')
        reusable=oracle_path.exists() and all(previous.get(k)==v for k,v in hashes.items()) and previous.get('oracleSha256')==digest(oracle_path)
        if not reusable:
            watch=run(exe+['--native-oracle',str(prefix),str(oracle_path)],str(prefix)+'.native-oracle.log',1800,16)
            write_json(str(prefix)+'.native-oracle.watch.json',watch)
            if watch['status']!='exited':raise RuntimeError(watch['status'])
        audit(prefix,tolerance,spacing)
        write_json(provenance,{**hashes,'oracleSha256':digest(oracle_path),'kernelSource':str(kernel)})


if __name__=='__main__':main()
