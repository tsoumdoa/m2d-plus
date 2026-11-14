"""Independent feature checks, without HiddenLineDrawing or product code."""
import math

from common import expand


def verify_tier_sections(model, meta, R):
    """Require both plan cuts and the roof section to intersect real surfaces."""
    if meta.get('family') != 'curved' or not meta.get('tier'):
        return []
    G = R.Geometry
    leaves = list(expand(model, R))
    checks = []
    for name in ('Plan level 1', 'Plan upper', 'Section long overlap'):
        descriptor = next(v for v in meta['views'] if v['name'] == name)
        p = descriptor['plane']
        plane = G.Plane(G.Point3d(*p['origin']), G.Vector3d(*p['x']), G.Vector3d(*p['y']))
        required = 'roof-diagonal' if name == 'Section long overlap' else 'glass'
        hit = None
        for geo, transform, source, _ in leaves:
            if required not in source:
                continue
            bounds = geo.GetBoundingBox(True); bounds.Transform(transform)
            distances = [plane.DistanceTo(c) for c in bounds.GetCorners()]
            if min(distances) >= 0 or max(distances) <= 0:
                continue
            shape = geo.Duplicate(); shape.Transform(transform)
            curves = []
            try:
                ok, cs, _ = G.Intersect.Intersection.BrepPlane(shape, plane, meta['tolerance'])
                curves = list(cs or [])
                if ok and any(c.GetLength() > meta['tolerance'] for c in curves):
                    hit = source
                    break
            finally:
                for c in curves: c.Dispose()
                shape.Dispose()
        checks.append({'view': name, 'intersectedSource': hit, 'pass': hit is not None})
    return checks


def verify(model, meta, R):
    if meta.get('family') != 'curved':
        return []
    G = R.Geometry
    objects = {o.Attributes.Name: o.Geometry for o in model.Objects}
    checks = []

    def record(name, passed, **evidence):
        checks.append(dict(check=name, **evidence, **{'pass': bool(passed)}))

    for name, expected in meta['expected'].get('edgeInventory', {}).items():
        b = objects[name]
        seams = {t.Edge.EdgeIndex for f in b.Faces for loop in f.Loops for t in loop.Trims
                 if str(t.TrimType) == 'Seam' and t.Edge is not None}
        tangent = [e.EdgeIndex for e in b.Edges if e.IsSmoothManifoldEdge(.001) and e.EdgeIndex not in seams]
        crease = [e.EdgeIndex for e in b.Edges if str(e.Valence) == 'Interior' and not e.IsSmoothManifoldEdge(.001) and e.EdgeIndex not in seams]
        naked = [e.EdgeIndex for e in b.Edges if str(e.Valence) == 'Naked']
        record('archived edge classification: ' + name,
               expected == dict(seam=sorted(seams), tangent=tangent, crease=crease, naked=naked))

    if meta['id'] == 'curved-steel':
        record('All eight steel members are closed solids', len(objects) == 8 and all(g.IsSolid for g in objects.values()))
        inventory = meta['expected']['edgeInventory']['I-beam-filleted-web-flanges']
        record('I-beam contains tangent fillets and cap creases', bool(inventory['tangent']) and bool(inventory['crease']),
               tangentEdges=len(inventory['tangent']), creaseEdges=len(inventory['crease']))
    if meta['id'] == 'diagrid-shell':
        roof = objects['open-roof-shell']
        record('Roof is open and has no fill region when cut', not roof.IsSolid and roof.Faces.Count == 1)
    if meta['id'] == 'curved-facade':
        cassette = objects['perforated-cassette-9-circular-holes']
        mass = G.AreaMassProperties.Compute(cassette)
        try:
            holes = sum(str(loop.LoopType) == 'Inner' for loop in cassette.Loops)
            record('Perforated cassette analytic area and holes', holes == 9 and abs(mass.Area-(9-9*math.pi*.25**2)) < 1e-6,
                   holes=holes, area=mass.Area)
        finally:
            mass.Dispose()
        for pair in meta['expected']['meshBrepPairs']:
            b, mesh = objects[pair['brep']], objects[pair['mesh']]
            # Sample vertices AND face centroids; vertices alone cannot reveal faceting.
            samples = [G.Point3d(v) for v in mesh.Vertices]
            samples += [G.Point3d(mesh.Faces.GetFaceCenter(i)) for i in range(mesh.Faces.Count)]
            delta = G.Vector3d(*pair['translation'])
            deviations = [(p-delta).DistanceTo(b.ClosestPoint(p-delta)) for p in samples]
            worst = max(deviations)
            record('Mesh/Brep sampled deviation: ' + pair['mesh'], worst < .004,
                   samples=len(samples), maximumDeviationMetres=worst, acceptanceMetres=.004,
                   method='Vertices and face centroids, not a Hausdorff bound')
    if meta['id'] == 'subd-vs-nurbs':
        converted = objects['canopy-SubD'].ToBrep()
        reference = objects['canopy-NURBS'].DuplicateBrep()
        try:
            reference.Transform(G.Transform.Translation(-7,0,0))
            # Native ToBrep may reorder its faces across archive round trips.
            # Compare the actual surfaces in both directions rather than IDs.
            distances = []
            for a,b in ((converted,reference),(reference,converted)):
                for face in a.Faces:
                    for i in range(7):
                        for j in range(7):
                            p = face.PointAt(face.Domain(0).ParameterAt(i/6),face.Domain(1).ParameterAt(j/6))
                            distances.append(p.DistanceTo(b.ClosestPoint(p)))
            record('SubD/NURBS bidirectional sampled surface equality', max(distances)<1e-7,
                   samples=len(distances),maximumDeviationMetres=max(distances),acceptanceMetres=1e-7)
            mesh = objects['canopy-mesh']
            samples = [G.Point3d(mesh.Faces.GetFaceCenter(i))-G.Vector3d(14,0,0) for i in range(mesh.Faces.Count)]
            maximum = max(p.DistanceTo(converted.ClosestPoint(p)) for p in samples)
            record('SubD mesh sampled limit-surface deviation', maximum < .02,
                   samples=len(samples), maximumDeviationMetres=maximum, acceptanceMetres=.02)
        finally:
            converted.Dispose(); reference.Dispose()
    if meta['id'] == 'tangent-continuity':
        for label in ('G1','G2'):
            inv = meta['expected']['edgeInventory'][label+'-plane-polynomial-blend']
            record(label+' join is tangent and not a crease', len(inv['tangent']) == 1 and not inv['crease'])
    return checks
