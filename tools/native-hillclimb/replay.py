"""Replay the standalone native hidden-flag negative regression, with owned containment."""
from pathlib import Path
import json
from run import ROOT, RESOLVER
from supervise import run
output=ROOT/'artifacts/native-hillclimb/reproducer-replay'
output.mkdir(parents=True,exist_ok=True)
exe=ROOT/'tools/native-hillclimb/bin/Release/net8.0-windows/NativeHillclimb.exe'
result=run([str(exe),str(RESOLVER),str(ROOT/'hillclimb/repros/hidden-flag-three-objects.3dm'),str(output),'replay'],str(output/'worker.log'),60,2)
(output/'watch.json').write_text(json.dumps(result,indent=2)+'\n')
if result['exitCode']:
    raise SystemExit((output/'worker.log').read_text())
print((output/'replay.json').read_text())
