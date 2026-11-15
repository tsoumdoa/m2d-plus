"""Raw RhinoCommon HLD adapter; no M2D+ engine or borrowed MVP implementation.

Adapters expose run(model, sidecar, view, multiple_threads, emit_phase), returning
(JSON-compatible measurements, SVG text). They own/dispose native drawing data.
The worker initializes Rhino and owns the File3dm for the duration of this call.
"""
from collections import Counter, defaultdict
import html
import math
import time

from common import expand

DIRECTIONS = {'Top': (0, 0, -1), 'Bottom': (0, 0, 1), 'Front': (0, 1, 0), 'Back': (0, -1, 0),
              'Right': (-1, 0, 0), 'Left': (1, 0, 0), 'SW': (1, 1, -1), 'SE': (-1, 1, -1),
              'NE': (-1, -1, -1), 'NW': (1, -1, -1)}


def resolve(model, descriptor, leaves, R):
    G = R.Geometry
    bounds = G.BoundingBox.Empty
    for geo, t, _, _ in leaves:
        b = geo.GetBoundingBox(True); b.Transform(t); bounds.Union(b)
    if not bounds.IsValid:
        bounds = G.BoundingBox(G.Point3d(-1, -1, -1), G.Point3d(1, 1, 1))
    kind, value = descriptor['source']['kind'], descriptor['source']['value']
    native_view, clip_obj = None, None
    if kind == 'named view':
        native_view = next(v for v in model.AllNamedViews if v.Name == value)
        if native_view.Viewport.IsPerspectiveProjection:
            raise ValueError('perspective_not_supported')
        vp = native_view.Viewport
        if not vp.IsValidCamera or not vp.TargetPoint.IsValid:
            raise ValueError('invalid_named_camera')
        pl = G.Plane(vp.TargetPoint, vp.CameraX, vp.CameraY)
    elif kind == 'clipping plane name':
        clip_obj = next(o for o in model.Objects if o.Attributes.Name == value and isinstance(o.Geometry, G.ClippingPlaneSurface))
        pl = clip_obj.Geometry.Plane
    elif kind in ('vector', 'direction keyword'):
        d = G.Vector3d(*(value if kind == 'vector' else DIRECTIONS[value]))
        if not d.Unitize():
            raise ValueError('zero_vector')
        up = G.Vector3d.YAxis if abs(d.Z) > .9999 else G.Vector3d.ZAxis
        right = G.Vector3d.CrossProduct(up, -d); right.Unitize()
        up = G.Vector3d.CrossProduct(-d, right)
        pl = G.Plane(bounds.Center, right, up)
    else:
        p = descriptor['plane']
        pl = G.Plane(G.Point3d(*p['origin']), G.Vector3d(*p['x']), G.Vector3d(*p['y']))
    if not pl.IsValid:
        raise ValueError('invalid_view_plane')
    flat = G.Transform.PlaneToPlane(pl, G.Plane.WorldXY)
    corners = []
    for p in bounds.GetCorners():
        p.Transform(flat); corners.append(p)
    frame = [min(p.X for p in corners), max(p.X for p in corners), min(p.Y for p in corners), max(p.Y for p in corners)]
    margin = max(frame[1] - frame[0], frame[3] - frame[2], model.Settings.ModelAbsoluteTolerance * 100) * .04
    frame = [frame[0] - margin, frame[1] + margin, frame[2] - margin, frame[3] + margin]
    crop = descriptor.get('crop')
    if kind == 'named view' and descriptor.get('settings', {}).get('crop') != 'off':
        vp = native_view.Viewport
        crop = [vp.FrustumLeft, vp.FrustumRight, vp.FrustumBottom, vp.FrustumTop]
    if crop:
        frame = crop
    cut = kind in ('clipping plane name', 'GH-style plane', 'rectangle', 'box')
    clips = [pl] if cut else []
    settings = descriptor.get('settings', {})
    depth = descriptor.get('depth')
    if clip_obj and clip_obj.Geometry.PlaneDepthEnabled:
        depth = clip_obj.Geometry.PlaneDepth
    if settings.get('depth', 'auto') != 'auto':
        text = str(settings['depth']).split()
        depth = float(text[0]) * (R.RhinoMath.UnitScale(R.UnitSystem.Meters, model.Settings.ModelUnitSystem) if len(text) > 1 and text[1] == 'm' else 1)
    if depth is not None and settings.get('farclip', 'noline') != 'none':
        clips.append(G.Plane(pl.Origin - pl.ZAxis * depth, -pl.ZAxis))
    if crop:
        x0, x1, y0, y1 = crop
        clips += [G.Plane(pl.PointAt(x0, 0), -pl.XAxis), G.Plane(pl.PointAt(x1, 0), pl.XAxis),
                  G.Plane(pl.PointAt(0, y0), -pl.YAxis), G.Plane(pl.PointAt(0, y1), pl.YAxis)]
    scope = None
    if clip_obj and clip_obj.Geometry.ParticipationListsEnabled:
        object_ids, layer_ids, exclusion = clip_obj.Geometry.GetClipParticipation()
        scope = (set(str(i) for i in object_ids), set(int(i) for i in layer_ids), exclusion)
    return pl, bounds, frame, clips, scope, depth, cut


def run(model, sidecar, descriptor, multiple_threads, emit_phase, polyline_fallback=None):
    import Rhino as R
    import System as S
    G = R.Geometry
    started = time.perf_counter()
    leaves = list(expand(model, R))
    if descriptor.get('reverseSourceOrder'):
        leaves.reverse()
    result = {'engine': 'native', 'status': 'running', 'expandedObjects': len(leaves),
              'multipleThreads': multiple_threads, 'unsupported': [], 'normalizations': [], 'extractionErrors': [],
              'notImplementedByRawHld': ['section styles/hatches', 'beyond/fade classification', 'glass=clear',
                                         'cut-region topology/merge', 'farclip=noline suppression'],
              'quality': 'unknown: execution baseline, not a full-output correctness certificate'}
    try:
        pl, bounds, frame, clips, scope, depth, cut = resolve(model, descriptor, leaves, R)
    except ValueError as e:
        return dict(result, status='view_error', error=str(e), solveWallSeconds=time.perf_counter() - started), empty_svg(str(e))
    vp = R.DocObjects.ViewportInfo(); vp.IsParallelProjection = True
    distance = max(bounds.Diagonal.Length * 2, 10)
    vp.SetCameraLocation(bounds.Center + pl.ZAxis * distance)
    vp.SetCameraDirection(-pl.ZAxis); vp.SetCameraUp(pl.YAxis)
    vp.SetFrustum(-distance, distance, -distance, distance, .001, distance * 4)
    parameters = G.HiddenLineDrawingParameters()
    parameters.AbsoluteTolerance = model.Settings.ModelAbsoluteTolerance
    parameters.Flatten = False
    parameters.IncludeHiddenCurves = True
    settings = descriptor.get('settings', {})
    parameters.IncludeTangentEdges = settings.get('tangents', 'off') == 'on'
    parameters.IncludeTangentSeams = settings.get('seams', 'off') == 'on'
    result['nativeEdgeSettings'] = {'tangents': parameters.IncludeTangentEdges,
                                  'seams': parameters.IncludeTangentSeams}
    parameters.SetViewport(vp)
    if scope is None:
        for clip in clips:
            parameters.AddClippingPlane(clip)
    owned = []; accepted = 0
    layer_names = {l.Index: l.Name for l in model.AllLayers}
    source_counts = defaultdict(Counter)
    preparation_start = time.perf_counter()
    emit_phase('prepare')
    try:
        for geo, transform, name, attrs in leaves:
            shape = geo
            if isinstance(shape, G.SubD):
                shape = geo.ToBrep(); owned.append(shape)
                result['normalizations'].append({'source': name, 'from': 'SubD', 'to': 'Brep', 'reason': 'raw AddGeometry does not accept SubD'})
            # Definition-local geometry and cumulative transforms preserve repetition at the API boundary.
            if scope is not None:
                ids, layers, exclusion = scope
                member = str(attrs.ObjectId) in ids or attrs.LayerIndex in layers
                active = (not member) if exclusion else member
                selected = clips if active else []
                planes = S.Collections.Generic.List[G.Plane]()
                for clip in selected:
                    planes.Add(clip)
                ok = parameters.AddGeometryAndPlanes(shape, transform, name, planes)
            else:
                ok = parameters.AddGeometry(shape, transform, name)
            if ok:
                accepted += 1
            else:
                result['unsupported'].append({'source': name, 'type': str(geo.GetType().Name), 'layer': layer_names.get(attrs.LayerIndex)})
        result['acceptedObjects'] = accepted
        result['prepareSeconds'] = time.perf_counter() - preparation_start
        if not accepted:
            return dict(result, status='unsupported', computeSeconds=0, solveWallSeconds=time.perf_counter() - started), empty_svg('No accepted geometry')
        emit_phase('compute', {'acceptedObjects': accepted})
        t = time.perf_counter()
        drawing = G.HiddenLineDrawing.Compute(parameters, multiple_threads)
        result['computeSeconds'] = time.perf_counter() - t
        if drawing is None:
            return dict(result, status='native_null', solveWallSeconds=time.perf_counter() - started), empty_svg('HLD returned null')
        try:
            emit_phase('extract', {'computeSeconds': result['computeSeconds']})
            ok, inverse = drawing.WorldToHiddenLine.TryGetInverse()
            if not ok:
                raise RuntimeError('Singular HLD transform')
            flat = G.Transform.PlaneToPlane(pl, G.Plane.WorldXY) * inverse
            paths = defaultdict(list); points = []; raw = Counter(); kinds = Counter(); lengths = defaultdict(float)
            tolerance = model.Settings.ModelAbsoluteTolerance
            counts = Counter(); unset = 0; sampled = 0; hidden_cuts = 0
            loci = []
            for segment in drawing.Segments:
                visibility = str(segment.SegmentVisibility); raw[visibility] += 1
                if visibility == 'Unset':
                    unset += 1
                    continue
                parent = segment.ParentCurve
                if parent is None:
                    unset += 1
                    continue  # Invalid native proxies may throw; never dereference them.
                kind = str(parent.SilhouetteType); kinds[kind] += 1
                tag = str(parent.SourceObject.Tag) if parent.SourceObject else '<section>'
                source_counts[tag][visibility] += 1
                if visibility not in ('Visible', 'Hidden'):
                    continue
                curve = polyline = None
                try:
                    curve = segment.CurveGeometry.ToNurbsCurve()
                    if curve is None or not curve.IsValid:
                        raise ValueError('null/invalid curve')
                    curve.Transform(flat)
                    category = 'Cut' if ('Section' in kind and visibility == 'Visible') else visibility
                    counts[category] += 1; lengths[category] += curve.GetLength()
                    ok, poly = curve.TryGetPolyline()
                    if not ok:
                        polyline = curve.ToPolyline(tolerance, math.pi / 36, 0, 0)
                        if polyline is not None: ok, poly = polyline.TryGetPolyline()
                    if ok and poly is not None and poly.Count >= 2:
                        coords = [(float(p.X), -float(p.Y)) for p in poly]
                    else:
                        fallback = polyline_fallback(curve,tolerance) if polyline_fallback else None
                        if not fallback or not fallback['Success']:
                            raise ValueError('SVG tessellation failed: '+str(fallback.get('Error') if fallback else 'no bounded fallback'))
                        coords = [(float(p[0]),-float(p[1])) for p in fallback['Points']]
                        result.setdefault('tessellationFallbacks',[]).append({'source':tag,'visibility':visibility,
                            'errorBound':fallback['ErrorBound'],'pointOnly':fallback['PointOnly'],
                            'method':'positive-weight rational Bezier control-hull subdivision'})
                    if any(not math.isfinite(x) or not math.isfinite(y) for x, y in coords):
                        raise ValueError('Nonfinite SVG coordinates')
                    if descriptor.get('recordLoci'):
                        loci.append({'class':category, 'source':tag, 'points':[[x,-y] for x,y in coords]})
                    sampled += len(coords)
                    render_class = category
                    if 'Section' in kind and visibility == 'Hidden':
                        render_class = 'HiddenCut'; hidden_cuts += 1
                    paths[render_class].append('M' + 'L'.join(f'{x:.8g},{y:.8g}' for x, y in coords))
                except Exception as e:
                    result['extractionErrors'].append({'visibility': visibility, 'source': tag, 'error': str(e)[:1000]})
                finally:
                    if polyline is not None: polyline.Dispose()
                    if curve:
                        curve.Dispose()
            point_counts = Counter()
            for pt in drawing.Points:
                vis = str(pt.PointVisibility); point_counts[vis] += 1
                tag = str(pt.SourceObject.Tag) if pt.SourceObject else '<point>'
                source_counts[tag]['Point' + vis] += 1
                if vis == 'Visible':
                    p = G.Point3d(pt.Location); p.Transform(flat); points.append((p.X, -p.Y))
            result.update(rawSegmentCounts=dict(raw), pieceCounts=dict(counts), silhouetteCounts=dict(kinds),
                          pointCounts=dict(point_counts), unsetProxies=unset, lengths=dict(lengths),
                          hiddenCutPieces=hidden_cuts, svgVertices=sampled, sourceCounts={k: dict(v) for k, v in source_counts.items()},
                          frame=frame, status='extraction_partial' if result['extractionErrors'] or unset else 'ok',
                          solveWallSeconds=time.perf_counter() - started)
            if descriptor.get('recordLoci'):
                result['loci'] = loci
            return result, svg(paths, points, frame, descriptor['name'])
        finally:
            drawing.Dispose()
    finally:
        for shape in owned:
            shape.Dispose()
        vp.Dispose()


def empty_svg(message):
    return '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 600 400"><text x="20" y="200" fill="#c66">' + html.escape(message) + '</text></svg>'


def svg(paths, points, frame, title):
    x0, x1, y0, y1 = frame
    width, height = max(x1 - x0, 1e-6), max(y1 - y0, 1e-6)
    # Pixel widths via vector-effect keep large campus drawings readable when opened at full size.
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{x0:.9g} {-y1:.9g} {width:.9g} {height:.9g}" role="img">',
           '<title>' + html.escape(title) + '</title>',
           '<style>svg{background:#fff;color:#222}path{fill:none;vector-effect:non-scaling-stroke;stroke:currentColor;stroke-width:.8;stroke-linejoin:round}.hidden{color:#9a9da3;stroke-dasharray:4 3;stroke-width:.6}.cut{stroke-width:1.8}@media(prefers-color-scheme:dark){svg{background:#161c25;color:#e2e6ed}.hidden{color:#717b8d}}</style>']
    for category in ('Hidden', 'HiddenCut', 'Visible', 'Cut'):
        if paths[category]:
            css_class = 'hidden cut' if category == 'HiddenCut' else category.lower()
            out.append(f'<path class="{css_class}" d="' + ''.join(paths[category]) + '"/>')
    for x, y in points:
        out.append(f'<circle cx="{x:.8g}" cy="{y:.8g}" r="{max(width,height)/350:.8g}" fill="currentColor"/>')
    out.append('</svg>')
    return ''.join(out)
