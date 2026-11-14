"""Independent archive/scene checks, analytic section checks and metamorphic pairs.

Does not call HLD or any production M2D+ code. Run serially after generation.
"""
import argparse
from collections import Counter
import hashlib
import json
import math
from pathlib import Path

from common import ROOT, SerialRhino, digest, expand, initialize, write_json


def check_units(folder, R):
    """Compare archived geometry after physical-unit/translation normalization."""
    base_path = folder / 'units-coords-m.3dm'
    if not base_path.exists():
        return []
    base = R.FileIO.File3dm.Read(str(base_path)); results = []
    try:
        reference = {name: geo for geo, _, name, _ in expand(base, R)}
        for suffix in ('mm', 'ft', 'offset'):
            path = folder / f'units-coords-{suffix}.3dm'
            if not path.exists():
                continue
            meta = json.loads(path.with_suffix('.json').read_text())
            scene = R.FileIO.File3dm.Read(str(path))
            try:
                factor = R.RhinoMath.UnitScale(scene.Settings.ModelUnitSystem, R.UnitSystem.Meters)
                offset = meta['expected']['translationModelUnits']
                transform = (R.Geometry.Transform.Scale(R.Geometry.Point3d.Origin, factor)
                             * R.Geometry.Transform.Translation(*[-v for v in offset]))
                mismatches = []; checked = []
                for geo, placement, name, _ in expand(scene, R):
                    normalized = geo.Duplicate()
                    try:
                        normalized.Transform(transform * placement)
                        if name not in reference or not normalized.IsDuplicate(reference[name], 1e-7):
                            mismatches.append(name)
                        checked.append(name)
                    finally:
                        normalized.Dispose()
                results.append({'model': meta['id'], 'checkedObjects': len(checked), 'mismatches': mismatches,
                                'unitScaleToMetres': factor, 'translationModelUnits': offset,
                                'toleranceMetres': 1e-7, 'fixtureSha256': digest(path),
                                'pass': not mismatches and set(checked) == set(reference)})
            finally:
                scene.Dispose()
    finally:
        base.Dispose()
    return results


def semantic(model, R):
    """Content SHA256, not DataCRC. Ignores archive GUID allocation/revision metadata.

Hashes expanded world-space geometry serialization, names, layer names/colours,
object colours and material/style source indices. Byte comparison separately
covers the complete archive, including views and style content.
"""
    options = R.FileIO.SerializationOptions()
    options.WriteUserData = False
    layers = {l.Index: l for l in model.AllLayers}
    h = hashlib.sha256()
    records = []
    for geo, transform, name, attrs in expand(model, R):
        world = geo.Duplicate()
        try:
            world.Transform(transform)
            # A byte-exact geometry serialization; content includes knots, trims and topology.
            encoded_shape = world.ToBrep() if isinstance(world, R.Geometry.SubD) else world
            encoded = encoded_shape.ToJSON(options)
            if encoded_shape is not world:
                encoded_shape.Dispose()
            record = {'name': name.split('/')[0],
                      'geometry': hashlib.sha256(encoded.encode()).hexdigest(),
                      'layer': layers[attrs.LayerIndex].Name,
                      'layerColor': layers[attrs.LayerIndex].Color.ToArgb(),
                      'colorSource': str(attrs.ColorSource), 'color': attrs.ObjectColor.ToArgb(),
                      'materialIndex': attrs.MaterialIndex, 'sectionSource': str(attrs.SectionAttributesSource)}
            records.append(record)
        finally:
            world.Dispose()
    for record in sorted(records, key=lambda r: r['name']):
        h.update(json.dumps(record, sort_keys=True, separators=(',', ':')).encode())
    return h.hexdigest()


def check_sections(model, meta, R, S):
    G = R.Geometry
    results = []
    targets = meta.get('expected', {}).get('analyticSections', {})
    for geo, transform, name, _ in expand(model, R):
        if name not in targets:
            continue
        world = geo.Duplicate(); world.Transform(transform)
        b = world.ToBrep() if isinstance(world, (G.Extrusion, G.Surface)) else world
        curves, joined, breps = [], [], []
        try:
            ok, cs, _ = G.Intersect.Intersection.BrepPlane(b, G.Plane.WorldXY, meta['tolerance'])
            curves = list(cs or [])
            joined = list(G.Curve.JoinCurves(S.Array[G.Curve](curves), meta['tolerance']))
            breps = list(G.Brep.CreatePlanarBreps(S.Array[G.Curve]([c for c in joined if c.IsClosed]), meta['tolerance']))
            area = 0
            for region in breps:
                props = G.AreaMassProperties.Compute(region); area += props.Area; props.Dispose()
            holes = sum(sum(str(loop.LoopType) == 'Inner' for loop in region.Loops) for region in breps)
            expected = targets[name]
            passed = len(breps) == expected['regions'] and abs(area - expected['area']) < max(1e-6, meta['tolerance'] ** 2 * 10)
            if 'holes' in expected:
                passed = passed and holes == expected['holes']
            results.append({'source': name, 'regions': len(breps), 'holes': holes, 'area': area, 'expected': expected, 'pass': passed})
        finally:
            for x in curves + joined + breps:
                x.Dispose()
            if b is not world:
                b.Dispose()
            world.Dispose()
    return results


def check_crop(model, meta, R, S):
    if meta['id'] != 'cut-topology':
        return None
    G = R.Geometry
    v = next(v for v in meta['views'] if v['name'] == 'L split rectangle')
    p = v['plane']; pl = G.Plane(G.Point3d(*p['origin']), G.Vector3d(*p['x']), G.Vector3d(*p['y']))
    geo = next(o.Geometry for o in model.Objects if o.Attributes.Name == 'crop-L')
    b = geo.ToBrep()
    curves = []; joined = []; intersections = []
    try:
        _, cs, _ = G.Intersect.Intersection.BrepPlane(b, pl, meta['tolerance'])
        curves = list(cs or [])
        joined = list(G.Curve.JoinCurves(S.Array[G.Curve](curves), meta['tolerance']))
        x0, x1, y0, y1 = v['crop']
        rectangle = G.Rectangle3d(pl, G.Interval(x0, x1), G.Interval(y0, y1)).ToNurbsCurve()
        try:
            intersections = list(G.Curve.CreateBooleanIntersection(joined[0], rectangle, meta['tolerance']))
            area = 0
            for c in intersections:
                mass = G.AreaMassProperties.Compute(c); area += mass.Area; mass.Dispose()
            return {'regions': len(intersections), 'area': area, 'expectedRegions': 2, 'expectedArea': 2,
                    'pass': len(intersections) == 2 and abs(area - 2) < 1e-6}
        finally:
            rectangle.Dispose()
    finally:
        for c in curves + joined + intersections:
            c.Dispose()
        b.Dispose()


def verify(args):
    R, S = initialize(); G = R.Geometry
    rows = []
    for side in sorted(args.models.glob('*.json')):
        if side.name == 'validation.json':
            continue
        meta = json.loads(side.read_text(encoding='utf-8'))
        if 'views' not in meta or (args.features and meta['tier']):
            continue
        path = side.with_suffix('.3dm')
        row = {'model': meta['id'], 'hashMatches': digest(path) == meta['sha256'],
               'smallFeature': meta['tier'] is None, 'sizeBytes': path.stat().st_size}
        if meta['tier'] is None:
            row['within2MB'] = path.stat().st_size <= 2_000_000
        model, read_log = R.FileIO.File3dm.ReadWithLog(str(path))
        read_log = read_log or ''
        try:
            row['readLog'] = read_log
            row['archiveReadClean'] = not any(term in read_log.lower() for term in ('crc error', 'crc failure', 'error reading', 'corrupt'))
            row['validGeometry'] = all(o.Geometry.IsValid for o in model.Objects)
            row['sections'] = check_sections(model, meta, R, S)
            row['crop'] = check_crop(model, meta, R, S)
            row['semanticSha256'] = semantic(model, R)
            if args.compare:
                other = R.FileIO.File3dm.Read(str(args.compare / path.name))
                try:
                    row['repeatSemanticEqual'] = row['semanticSha256'] == semantic(other, R)
                    row['repeatArchiveBytesEqual'] = digest(path) == digest(args.compare / path.name)
                finally:
                    other.Dispose()
            sources = []
            for v in meta['views']:
                if v['source']['kind'] == 'clipping plane name':
                    obj = next(o for o in model.Objects if o.Attributes.Name == v['source']['value'])
                    cp = obj.Geometry
                    stored = {'name': v['name'], 'depthEnabled': cp.PlaneDepthEnabled, 'depth': cp.PlaneDepth,
                                    'participationEnabled': cp.ParticipationListsEnabled,
                                    'normal': [cp.Plane.ZAxis.X, cp.Plane.ZAxis.Y, cp.Plane.ZAxis.Z]}
                    stored['pass'] = (('nativeDepth' not in v or (cp.PlaneDepthEnabled and cp.PlaneDepth == v['nativeDepth']))
                                      and ('scopeLayers' not in v or cp.ParticipationListsEnabled))
                    x, y = v['plane']['x'], v['plane']['y']
                    normal = [x[1]*y[2]-x[2]*y[1], x[2]*y[0]-x[0]*y[2], x[0]*y[1]-x[1]*y[0]]
                    stored['pass'] &= all(abs(a-b) < 1e-12 for a,b in zip(normal, stored['normal']))
                    if 'scopeLayers' in v:
                        ids, layers, exclusion = cp.GetClipParticipation()
                        names = {layer.Index: layer.Name for layer in model.AllLayers}
                        stored.update(scopeLayers=[names[int(i)] for i in layers], scopeObjects=[str(i) for i in ids], exclusion=exclusion)
                        stored['pass'] &= not exclusion and not stored['scopeObjects'] and sorted(stored['scopeLayers']) == sorted(v['scopeLayers'])
                    sources.append(stored)
            row['clippingSources'] = sources
            row['namedViews'] = []
            for v in meta['views']:
                if v['source']['kind'] == 'named view':
                    saved = next(n for n in model.AllNamedViews if n.Name == v['source']['value'])
                    perspective = saved.Viewport.IsPerspectiveProjection
                    x, y = v['plane']['x'], v['plane']['y']
                    normal = R.Geometry.Vector3d(x[1]*y[2]-x[2]*y[1], x[2]*y[0]-x[0]*y[2], x[0]*y[1]-x[1]*y[0])
                    direction = saved.Viewport.CameraDirection
                    target = saved.Viewport.TargetPoint
                    row['namedViews'].append({'name': saved.Name, 'perspective': perspective,
                                              'direction': [direction.X, direction.Y, direction.Z],
                                              'target': [target.X, target.Y, target.Z],
                                              'pass': perspective == v.get('perspective', False) and (direction + normal).Length < 1e-10
                                                      and target.IsValid and target.DistanceTo(R.Geometry.Point3d(*v['plane']['origin'])) < 1e-10})
            if meta['id'] == 'section-styles':
                styles = []
                for obj in model.Objects:
                    a = obj.Attributes; st = a.GetCustomSectionStyle()
                    styles.append({'name': a.Name, 'source': str(a.SectionAttributesSource), 'hasCustomStyle': st is not None,
                                   'background': str(st.BackgroundFillMode) if st else None,
                                   'hatchIndex': st.HatchIndex if st else None, 'colorSource': str(a.ColorSource),
                                   'hatchScale': st.HatchScale if st else None,
                                   'boundaryVisible': st.BoundaryVisible if st else None,
                                   'boundaryWidthScale': st.BoundaryWidthScale if st else None,
                                   'backgroundColor': st.BackgroundFillColor.ToArgb() if st else None})
                row['styles'] = styles
                row['layerStyles'] = {l.Name: l.GetCustomSectionStyle() is not None for l in model.AllLayers}
                by_name = {s['name']: s for s in styles}
                expected_sources = {'by-layer': 'FromLayer', 'custom': 'FromObject', 'by-clipping-plane': 'FromSectioner',
                                    'parent-member': 'FromParent', 'parent-nested': 'FromParent', 'nested-by-parent': 'FromObject'}
                concrete = next(l for l in model.AllLayers if l.Name == 'Concrete style').GetCustomSectionStyle()
                row['styleChecksPass'] = (all(by_name[n]['source'] == source for n, source in expected_sources.items())
                    and concrete is not None and concrete.HatchIndex >= 0
                    and by_name['custom']['background'] == 'SolidColor'
                    and by_name['explicit-no-fill']['background'] == 'None' and by_name['explicit-no-fill']['hatchIndex'] == -1
                    and by_name['viewport-fill']['background'] == 'Viewport'
                    and by_name['Styled clipping plane']['hasCustomStyle']
                    and not by_name['no-style-layer-colour']['hasCustomStyle']
                    and by_name['no-style-material-colour']['colorSource'] == 'ColorFromMaterial'
                    and all(s['boundaryVisible'] and s['boundaryWidthScale'] == 1.5 and s['hatchScale'] == .15
                            for s in styles if s['hasCustomStyle']))
        finally:
            model.Dispose()
        rows.append(row)
        print(meta['id'], 'valid', row['validGeometry'], 'section checks', len(row['sections']), 'repeat', row.get('repeatSemanticEqual'), flush=True)
    units = check_units(args.models, R)
    print('Normalized unit/translation geometry:', all(u['pass'] for u in units), flush=True)
    result = {'rows': rows, 'unitEquivalence': units, 'allChecksPass': all(r['hashMatches'] and r['validGeometry'] and r['archiveReadClean'] and r.get('within2MB', True)
              and all(s['pass'] for s in r['sections']) and (not r['crop'] or r['crop']['pass']) and all(c['pass'] for c in r['clippingSources'])
              and all(v['pass'] for v in r['namedViews']) and r.get('styleChecksPass', True)
              and r.get('repeatSemanticEqual', True) and r.get('repeatArchiveBytesEqual', True) for r in rows) and all(u['pass'] for u in units)}
    write_json(args.out, result)
    if not result['allChecksPass']:
        raise SystemExit(1)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--models', type=Path, default=ROOT / 'artifacts/models')
    p.add_argument('--features', action='store_true')
    p.add_argument('--compare', type=Path)
    p.add_argument('--out', type=Path, default=ROOT / 'artifacts/models/validation.json')
    args = p.parse_args()
    with SerialRhino():
        verify(args)


if __name__ == '__main__':
    main()
