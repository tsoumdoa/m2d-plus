"""Verify expanded geometry equivalence before interpreting blocks/exploded HLD differences."""
import argparse
import json
from pathlib import Path

from common import ROOT, SerialRhino, expand, initialize, write_json


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--models', type=Path, default=ROOT / 'artifacts/models')
    p.add_argument('--tiers', default='XS,S,M,L')
    p.add_argument('--curved', action='store_true')
    args = p.parse_args()
    R, _ = initialize(); G = R.Geometry
    rows = []
    prefix = 'curved-perf' if args.curved else 'perf'
    for tier in args.tiers.split(','):
        blocks = R.FileIO.File3dm.Read(str(args.models / f'{prefix}-{tier}-blocks.3dm'))
        exploded = R.FileIO.File3dm.Read(str(args.models / f'{prefix}-{tier}-exploded.3dm'))
        try:
            camera_checks = []
            section_checks = []
            for form, scene in [('blocks', blocks), ('exploded', exploded)]:
                meta = json.loads((args.models / f'{prefix}-{tier}-{form}.json').read_text())
                if args.curved:
                    from curved_checks import verify_tier_sections
                    section_checks.extend(dict(c, form=form) for c in verify_tier_sections(scene, meta, R))
                named = {v.Name: v for v in scene.AllNamedViews}
                for descriptor in meta['views']:
                    p = descriptor['plane']; vp = named[descriptor['name']].Viewport
                    plane = G.Plane(G.Point3d(*p['origin']), G.Vector3d(*p['x']), G.Vector3d(*p['y']))
                    camera_checks.append({'form': form, 'view': descriptor['name'],
                                          'pass': vp.IsParallelProjection and (vp.CameraDirection + plane.ZAxis).Length < 1e-10
                                                  and vp.TargetPoint.IsValid and vp.TargetPoint.DistanceTo(plane.Origin) < 1e-10})
            actual = {name.split('/')[0]: (g, t) for g, t, name, _ in expand(exploded, R)}
            tolerance = blocks.Settings.ModelAbsoluteTolerance * .001
            mismatches = []; checked = 0
            for geo, transform, name, _ in expand(blocks, R):
                name = name.split('/')[0]
                other, ot = actual.pop(name)
                a, b = geo.Duplicate(), other.Duplicate()
                a.Transform(transform); b.Transform(ot)
                ab, bb = a, b
                try:
                    if isinstance(a, (G.Extrusion, G.Surface, G.SubD)):
                        ab = a.ToBrep()
                    if isinstance(b, (G.Extrusion, G.Surface, G.SubD)):
                        bb = b.ToBrep()
                    equal = ab.IsDuplicate(bb, tolerance) if isinstance(ab, G.Brep) and isinstance(bb, G.Brep) else G.GeometryBase.GeometryEquals(ab, bb)
                    if not equal:
                        mismatches.append(name)
                    checked += 1
                finally:
                    if ab is not a: ab.Dispose()
                    if bb is not b: bb.Dispose()
                    a.Dispose(); b.Dispose()
            row = {'tier': tier, 'checkedObjects': checked, 'comparisonToleranceMetres': tolerance,
                   'mismatches': mismatches, 'unmatchedExplodedObjects': list(actual),
                   'cameraChecks': camera_checks, 'sectionChecks': section_checks,
                   'pass': not mismatches and not actual and all(c['pass'] for c in camera_checks + section_checks),
                   'method': 'Expanded world geometry; Brep.IsDuplicate at 0.001 × model tolerance, exact GeometryEquals for meshes.'}
            rows.append(row); print(tier, checked, 'equivalent', row['pass'], flush=True)
        finally:
            blocks.Dispose(); exploded.Dispose()
    write_json(args.models / ('curved-pair-validation.json' if args.curved else 'pair-validation.json'), {'rows': rows, 'allChecksPass': all(r['pass'] for r in rows)})
    if not all(r['pass'] for r in rows):
        raise SystemExit(1)


if __name__ == '__main__':
    with SerialRhino():
        main()
