"""Prepare only small feature archives for review; never stage, commit or push."""
import argparse
import json
from pathlib import Path
import shutil

from common import ROOT, digest


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--models', type=Path, default=ROOT / 'artifacts/models')
    p.add_argument('--all-features', action='store_true', help='Otherwise freeze only hook-eligible feature models')
    args = p.parse_args()
    target = ROOT / 'tests/fixtures/features'; target.mkdir(parents=True, exist_ok=True)
    manifest_path = ROOT / 'tests/fixtures/manifest.json'
    manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
    # A fresh worktree may generate only one extension of the suite. Preserve
    # existing entries whose models are not part of this invocation.
    generated_ids = {'models/' + p.with_suffix('.3dm').name for p in args.models.glob('*.json')
                     if p.with_suffix('.3dm').exists()}
    manifest['fixtures'] = [f for f in manifest['fixtures'] if f['id'] not in generated_ids]
    for side in sorted(args.models.glob('*.json')):
        meta = json.loads(side.read_text(encoding='utf-8'))
        if 'views' not in meta:
            continue
        model = side.with_suffix('.3dm')
        if digest(model) != meta['sha256']:
            raise ValueError('Hash mismatch: ' + str(model))
        bundled = meta['tier'] is None and (args.all_features or meta['hook'])
        entry = {'id': 'models/' + model.name, 'localModelSuite': True, 'external': not bundled,
                 'sha256': meta['sha256'], 'sizeBytes': model.stat().st_size, 'units': meta['units'],
                 'hook': meta['hook'], 'manualOnly': True, 'seed': meta['seed'], 'tier': meta['tier'], 'flavor': meta['flavor'],
                 'generator': meta['generator']['script'], 'stats': meta['stats'],
                 'description': 'Local deterministic model suite: ' + meta['id'], 'sidecarSha256': digest(side)}
        if bundled:
            if model.stat().st_size > manifest['maximumBundledSizeBytes']:
                raise ValueError('Feature exceeds the 2 MB policy: ' + str(model))
            shutil.copyfile(model, target / model.name)
            shutil.copyfile(side, target / side.name)
            entry.update(path='features/' + model.name, sidecar='features/' + side.name)
        else:
            entry.update(generatedPath='../../artifacts/models/' + model.name,
                         generatedSidecar='../../artifacts/models/' + side.name)
        manifest['fixtures'].append(entry)
    manifest['localModelSuite'] = {'schemaVersion': 1, 'policy': 'Local only; no CI. Hook flags are candidates, not enabled jobs.',
                                 'generator': '../../tools/models/generate.py', 'seed': 20261114,
                                 'generatedDirectory': '../../artifacts/models', 'report': '../../artifacts/baseline/index.html'}
    temp = manifest_path.with_suffix('.json.tmp')
    temp.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    temp.replace(manifest_path)
    print('Prepared', sum(f.get('localModelSuite', False) and not f['external'] for f in manifest['fixtures']), 'feature archives; nothing staged or committed')


if __name__ == '__main__':
    main()
