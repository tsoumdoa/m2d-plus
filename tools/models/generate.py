"""Local-only, seeded Rhino 8 model suite. No active document or UI access."""
from __future__ import annotations

import argparse
import math
from pathlib import Path
import random
import subprocess
import sys

from archive import canonicalize
from common import ROOT, SCHEMA, SerialRhino, digest, initialize, plane_spec, stable_id, stats, view, write_json

R = S = G = None
OPEN_MODELS = []
TOL = 1e-4
TIERS = {'XS': (50, 1, 1), 'S': (500, 1, 2), 'M': (5000, 1, 8),
         'L': (20000, 1, 30), 'XL': (50000, 5, 12), 'XXL': (150000, 15, 12)}
FEATURES = ['view-sources', 'section-styles', 'cut-topology', 'view-range', 'glass', 'blocks',
            'edges', 'curves-points', 'units-coords', 'annotations']


def arr(items, typ=None):
    return S.Array[typ or G.GeometryBase](list(items))


def box(x, y, z, dx, dy, dz):
    return G.BoundingBox(G.Point3d(x, y, z), G.Point3d(x + dx, y + dy, z + dz)).ToBrep()


def moved(geo, x=0, y=0, z=0, scale=1):
    dup = geo.Duplicate()
    dup.Transform(G.Transform.Translation(x, y, z) * G.Transform.Scale(G.Point3d.Origin, scale))
    return dup


def profile(points, z=-1, height=2):
    curve = G.PolylineCurve(arr([G.Point3d(x, y, z) for x, y in points], G.Point3d))
    ext = G.Extrusion.Create(curve, height, True)
    curve.Dispose()
    if ext is None:
        raise ValueError('Invalid profile')
    return ext


def cylinder(radius=.2, height=3):
    return G.Cylinder(G.Circle(G.Plane.WorldXY, radius), height).ToBrep(True, True)


def canopy():
    mesh = G.Mesh()
    for j in range(4):
        for i in range(5):
            mesh.Vertices.Add(i, j, .35 * math.sin(i * math.pi / 4) + .15 * j)
    for j in range(3):
        for i in range(4):
            a = j * 5 + i
            mesh.Faces.AddFace(a, a + 1, a + 6, a + 5)
    mesh.Normals.ComputeNormals()
    subd = G.SubD.CreateFromMesh(mesh)
    mesh.Dispose()
    assert subd and subd.IsValid
    return subd


class Model:
    def __init__(self, name, seed, out, units='Meters', scale=1, offset=(0, 0, 0)):
        self.name, self.seed, self.out = name, seed, Path(out).resolve()
        self.rng = random.Random(seed)
        self.file = R.FileIO.File3dm()
        OPEN_MODELS.append(self.file)
        self.file.ApplicationName = 'M2D+ local test models'
        self.file.ApplicationDetails = 'MIT deterministic geometry generator schema 1'
        self.file.StartSectionComments = 'M2D+ model suite; local only'
        # Prevent File3dm.Write from filling revision history with the current clock.
        self.file.Revision = 1
        self.file.Settings.ModelUnitSystem = getattr(R.UnitSystem, units)
        self.file.Settings.ModelAbsoluteTolerance = TOL * scale
        self.file.Settings.ModelAngleToleranceRadians = math.pi / 180
        self.scale, self.offset = scale, offset
        self.layers, self.defs, self.counter = {}, {}, 0
        self.views, self.expected, self.notes = [], {}, []
        self.source_fixtures = {}
        self.archive_ids = {}
        self.layer('Structure', (150, 160, 170))
        self.layer('Envelope', (190, 150, 110))
        self.layer('Glass', (100, 180, 210))
        self.layer('Fixtures', (140, 110, 175))
        self.layer('View sources', (90, 180, 120))

    def uid(self, key):
        return S.Guid(stable_id(f'{self.name}/{self.seed}/{key}'))

    def layer(self, name, rgb, style=None):
        if name in self.layers:
            return self.layers[name]
        layer = R.DocObjects.Layer()
        layer.Name, layer.Id = name, self.uid('layer/' + name)
        layer.Color = S.Drawing.Color.FromArgb(*rgb)
        if style:
            layer.SetCustomSectionStyle(style)
        self.file.AllLayers.Add(layer)
        index = self.file.AllLayers.FindName(name, S.Guid.Empty).Index
        self.layers[name] = index
        return index

    def attrs(self, name, layer='Structure', source=None, style=None, color=None, material=None):
        a = R.DocObjects.ObjectAttributes()
        a.Name, a.ObjectId, a.LayerIndex = name, self.uid('object/' + name), self.layers[layer]
        if source:
            a.SectionAttributesSource = getattr(R.DocObjects.ObjectSectionAttributesSource, source)
        if style:
            a.SetCustomSectionStyle(style)
        if color:
            a.ColorSource = R.DocObjects.ObjectColorSource.ColorFromObject
            a.ObjectColor = S.Drawing.Color.FromArgb(*color)
        if material is not None:
            a.MaterialIndex = material
            a.MaterialSource = R.DocObjects.ObjectMaterialSource.MaterialFromObject
        return a

    def add(self, geo, name=None, layer='Structure', **kw):
        self.counter += 1
        name = name or f'object-{self.counter:06}'
        a = self.attrs(name, layer, **kw)
        if self.scale != 1 or any(self.offset):
            geo = moved(geo, *self.offset, self.scale)
        if isinstance(geo, G.ClippingPlaneSurface):
            geo.AddClipViewportId(self.uid('viewport/' + name))
            # Generic Add rejects this subtype; AddSurface preserves the complete
            # ClippingPlaneSurface, including depth and participation metadata.
            oid = self.file.Objects.AddSurface(geo, a)
        elif isinstance(geo, G.TextEntity):
            oid = self.file.Objects.AddText(geo.PlainText, geo.Plane, geo.TextHeight, 'Arial', False, False, a)
        elif isinstance(geo, G.Leader):
            oid = self.file.Objects.AddLeader(geo.PlainText, geo.Plane, arr(geo.Points2D, G.Point2d), a)
        elif isinstance(geo, G.LinearDimension):
            oid = self.file.Objects.AddLinearDimension(geo, a)
        elif isinstance(geo, G.Hatch):
            oid = self.file.Objects.AddHatch(geo, a)
        else:
            oid = self.file.Objects.Add(geo, a)
        if oid == S.Guid.Empty:
            raise RuntimeError('Could not add ' + name)
        return oid

    def fixture(self, filename):
        path = ROOT / 'tests/fixtures' / (filename + '.3dm')
        self.source_fixtures[filename] = digest(path)
        f = R.FileIO.File3dm.Read(str(path))
        try:
            return next(o.Geometry.Duplicate() for o in f.Objects if not o.Attributes.IsInstanceDefinitionObject)
        finally:
            f.Dispose()

    def definition(self, name, geometries, attributes=None):
        geometries = list(geometries)
        attrs = attributes or [self.attrs(f'{name}/member-{i}') for i in range(len(geometries))]
        index = self.file.AllInstanceDefinitions.Add(name, 'M2D+ seeded fixture', G.Point3d.Origin,
                                                     arr(geometries), arr(attrs, R.DocObjects.ObjectAttributes))
        d = list(self.file.AllInstanceDefinitions)[index]
        self.archive_ids[str(d.Id)] = str(self.uid('definition/' + name))
        for i, oid in enumerate(d.GetObjectIds()):
            self.archive_ids[str(oid)] = str(self.uid(f'definition/{name}/member/{i}'))
        self.defs[name] = d.Id
        return d.Id

    def instance(self, definition, transform, name, **kw):
        return self.add(G.InstanceReferenceGeometry(self.defs[definition], transform), name, **kw)

    def material(self, name, rgb, transparency=0):
        m = R.DocObjects.Material()
        m.Name, m.Id = name, self.uid('material/' + name)
        m.DiffuseColor = S.Drawing.Color.FromArgb(*rgb)
        m.Transparency = transparency
        self.file.AllMaterials.Add(m)
        return list(self.file.AllMaterials)[-1].Index

    def add_view(self, v):
        self.views.append(v)

    def save_views(self):
        for v in self.views:
            p = v.get('plane', plane_spec())
            pl = G.Plane(G.Point3d(*p['origin']), G.Vector3d(*p['x']), G.Vector3d(*p['y']))
            kind = v['source']['kind']
            if kind == 'clipping plane name':
                cp = G.ClippingPlaneSurface(pl)
                cp.SetExtents(0, G.Interval(-5, 5), True)
                cp.SetExtents(1, G.Interval(-5, 5), True)
                if 'nativeDepth' in v:
                    cp.PlaneDepth, cp.PlaneDepthEnabled = v['nativeDepth'], True
                if 'scopeLayers' in v:
                    cp.SetClipParticipation(arr([], S.Guid), arr([self.layers[n] for n in v['scopeLayers']], S.Int32), False)
                    cp.ParticipationListsEnabled = True
                self.add(cp, v['source']['value'], 'View sources', source='FromObject',
                         style=style(self, 'clip-style', hatch=0) if v.get('clipStyle') else None)
                cp.Dispose()
            if kind in ('named view', 'clipping plane name'):
                # ViewInfo.Viewport mutations are detached when File3dm.Add copies
                # the ViewInfo. Snapshot a standalone viewport instead; no live doc.
                viewport = R.DocObjects.ViewportInfo()
                if v.get('perspective', False):
                    viewport.IsPerspectiveProjection = True
                else:
                    viewport.IsParallelProjection = True
                viewport.SetCameraLocation(pl.Origin + pl.ZAxis * v.get('cameraDistance', 200))
                viewport.SetCameraDirection(-pl.ZAxis)
                viewport.SetCameraUp(pl.YAxis)
                frame = v.get('frame', [-150, 150, -150, 150])
                viewport.SetFrustum(*frame, .01, 10000)
                viewport.TargetPoint = pl.Origin
                display = R.Display.RhinoViewport()
                if not display.SetViewProjection(viewport, False):
                    raise RuntimeError('Cannot construct named camera: ' + v['name'])
                display.SetCameraTarget(pl.Origin, False)
                info = R.DocObjects.ViewInfo(display)
                info.Name, info.NamedViewId = v['name'], self.uid('view/' + v['name'])
                self.archive_ids[str(info.Viewport.Id)] = str(self.uid('viewport/' + v['name']))
                self.file.AllNamedViews.Add(info)
                saved = list(self.file.AllNamedViews)[-1].Viewport
                if (saved.IsPerspectiveProjection != v.get('perspective', False)
                        or (saved.CameraDirection + pl.ZAxis).Length > 1e-10
                        or not saved.TargetPoint.IsValid or saved.TargetPoint.DistanceTo(pl.Origin) > 1e-10):
                    raise RuntimeError('Named camera did not persist: ' + v['name'])
                info.Dispose(); display.Dispose(); viewport.Dispose()

    def save(self, hook=False, tier=None, flavor=None):
        self.save_views()
        path = self.out / (self.name + '.3dm')
        path.parent.mkdir(parents=True, exist_ok=True)
        options = R.FileIO.File3dmWriteOptions()
        options.Version, options.SaveRenderMeshes, options.SaveAnalysisMeshes = 8, False, False
        try:
            # Rhino stores the supplied path in archive metadata. A basename keeps
            # archives independent of the checkout/output directory.
            if tier is None:
                path.write_bytes(bytes(self.file.ToByteArray(options)))
            else:
                stage = ROOT / 'artifacts/models/.archive-stage' / path.name
                stage.parent.mkdir(parents=True, exist_ok=True)
                if not self.file.Write(str(stage), options):
                    raise RuntimeError('Failed to write ' + str(path))
                stage.replace(path)
            for i, ds in enumerate(self.file.AllDimStyles):
                self.archive_ids[str(ds.Id)] = str(self.uid('dimstyle/' + str(i)))
            canonicalize(path, self.archive_ids)
            count = stats(self.file, R)
            data = {'schemaVersion': SCHEMA, 'id': self.name, 'seed': self.seed, 'units': str(self.file.Settings.ModelUnitSystem),
                    'tolerance': self.file.Settings.ModelAbsoluteTolerance, 'hook': hook,
                    'tier': tier, 'flavor': flavor, 'views': self.views, 'expected': self.expected,
                    'notes': self.notes, 'sourceFixtures': self.source_fixtures, 'stats': count,
                    'sizeBytes': path.stat().st_size, 'sha256': digest(path),
                    'generator': {'script': 'tools/models/generate.py', 'sha256': digest(__file__),
                                  'rhino': str(R.RhinoApp.Version), 'runtime': str(S.Environment.Version)}}
            write_json(path.with_suffix('.json'), data)
            print(self.name, count['objects'], data['sizeBytes'], flush=True)
            return data
        finally:
            self.file.Dispose()
            OPEN_MODELS.remove(self.file)


def style(model, name, hatch=-1, background='None'):
    s = R.DocObjects.SectionStyle()
    s.Id, s.Name = model.uid('style/' + name), name
    s.HatchIndex, s.HatchScale = hatch, .15
    s.BackgroundFillMode = getattr(R.DocObjects.SectionBackgroundFillMode, background)
    s.BackgroundFillColor = S.Drawing.Color.FromArgb(175, 155, 135)
    s.BoundaryVisible, s.BoundaryWidthScale = True, 1.5
    return s


def building(m, levels=2):
    for k in range(levels + 1):
        m.add(box(0, 0, k * 3, 8, 6, .25), f'slab-{k}')
    for k in range(levels):
        for x in (0, 3.8, 7.6):
            for y in (0, 5.6):
                m.add(box(x, y, k * 3, .4, .4, 3), f'column-{k}-{x}-{y}')
        for x in (0, 7.8):
            m.add(box(x, 0, k * 3, .2, 6, 2.8), f'wall-{k}-{x}', 'Envelope')


def feature_views(m, cut=0):
    m.add_view(view('Top'))
    m.add_view(view('SW', value='SW'))
    m.add_view(view('Plan cut', 'GH-style plane', None, plane_spec((0, 0, cut))))


def features(seed, out, only=None):
    def want(name):
        return not only or name in only
    results = []
    if want('view-sources'):
        m = Model('view-sources', seed, out); building(m)
        for name, p in [('Top', plane_spec((4, 3, 10))),
                        ('Front', plane_spec((4, -2, 3), (1, 0, 0), (0, 0, 1))),
                        ('Rotated parallel', plane_spec((4, 3, 10), (.8, .6, 0), (-.6, .8, 0))),
                        ('Perspective must error', plane_spec((4, 3, 10)))]:
            m.add_view(view(name, 'named view', name, p, perspective=name.startswith('Perspective'), frame=[-8, 8, -6, 6],
                            expected={'error': 'perspective_not_supported'} if name.startswith('Perspective') else {}))
        for name, extra in [('Plain clip', {}), ('Depth clip', {'nativeDepth': 2.0}),
                            ('Structure only', {'scopeLayers': ['Structure']}), ('Flipped clip', {})]:
            p = plane_spec((0, 0, 4.2), (1, 0, 0), (0, -1 if name.startswith('Flipped') else 1, 0))
            m.add_view(view(name, 'clipping plane name', name, p, **extra))
        m.add_view(view('Direction Front', value='Front'))
        m.add_view(view('Vector oblique', 'vector', [1, 2, -1]))
        m.add_view(view('GH plane', 'GH-style plane', None, plane_spec((0, 0, 1.2))))
        m.add_view(view('GH rectangle', 'rectangle', None, plane_spec((4, 3, 1.2)), size=[5, 4], crop=[-2.5, 2.5, -2, 2]))
        m.add_view(view('GH box', 'box', None, plane_spec((4, 3, 4.2)), size=[6, 5, 3], crop=[-3, 3, -2.5, 2.5], depth=3))
        m.expected = {'perspectiveErrorViews': 1, 'scopeUncutLayers': ['Envelope'], 'boxNearFaceZ': 4.2, 'boxFarFaceZ': 1.2}
        results.append(m.save())
    if want('section-styles'):
        m = Model('section-styles', seed, out)
        pattern = R.DocObjects.HatchPattern(R.DocObjects.HatchPattern.Defaults.Hatch1)
        pattern.Name, pattern.Id = 'Concrete-like diagonal', m.uid('hatch/concrete')
        m.file.AllHatchPatterns.Add(pattern)
        hi = list(m.file.AllHatchPatterns)[-1].Index
        m.layer('Concrete style', (120, 145, 160), style(m, 'concrete', hi))
        m.add(box(0, 0, -1, 1.5, 1.5, 2), 'by-layer', 'Concrete style')
        m.add(box(2, 0, -1, 1.5, 1.5, 2), 'custom', source='FromObject', style=style(m, 'custom', hi, 'SolidColor'), color=(190, 80, 60))
        m.add(box(4, 0, -1, 1.5, 1.5, 2), 'by-clipping-plane', source='FromSectioner')
        member = box(0, 0, -1, 1.5, 1.5, 2)
        m.definition('leaf', [member], [m.attrs('parent-member', source='FromParent')])
        m.definition('nested', [G.InstanceReferenceGeometry(m.defs['leaf'], G.Transform.Identity)], [m.attrs('parent-nested', source='FromParent')])
        m.instance('nested', G.Transform.Translation(6, 0, 0), 'nested-by-parent', source='FromObject', style=style(m, 'parent', hi))
        m.add(box(0, 3, -1, 1.5, 1.5, 2), 'explicit-no-fill', source='FromObject', style=style(m, 'no-fill'))
        m.add(box(2, 3, -1, 1.5, 1.5, 2), 'viewport-fill', source='FromObject', style=style(m, 'viewport', background='Viewport'))
        m.add(box(4, 3, -1, 1.5, 1.5, 2), 'no-style-layer-colour', 'Envelope')
        mi = m.material('ochre', (190, 130, 40))
        oid = m.add(box(6, 3, -1, 1.5, 1.5, 2), 'no-style-material-colour', material=mi)
        m.file.Objects.FindId(oid).Attributes.ColorSource = R.DocObjects.ObjectColorSource.ColorFromMaterial
        m.add_view(view('Styled clipping plane', 'clipping plane name', 'Styled clipping plane', plane_spec(), clipStyle=True))
        m.add_view(view('GH plane fallback', 'GH-style plane', None, plane_spec()))
        m.expected = {'sectionRegions': 8, 'regionAreaEach': 2.25, 'holes': 0, 'explicitNoFill': ['explicit-no-fill'],
                      'viewportFallback': 'object display colour', 'unavailableProperties': []}
        m.notes.append('No document section-style table exists; custom styles are stored on layers/object attributes. HLD does not render fills.')
        results.append(m.save(hook=True))
    if want('cut-topology'):
        m = Model('cut-topology', seed, out)
        # Reuse frozen primitive truth; the new fixture exercises interactions and contacts.
        for filename, x in [('rhs_hollow', 0), ('chs_hollow', 4), ('i_beam', 8)]:
            m.add(moved(m.fixture(filename), x), filename)
        m.add(box(-.25, -.25, -1, .5, .5, 2), 'island-inside-rhs-void')
        for i, x in enumerate((0, 2)):
            m.add(box(x, 5, -1, 2, .3, 2), 'same-style-touch-' + str(i))
        m.add(box(5, 5, -1, 2, .3, 2), 'different-style-a', color=(180, 100, 100))
        m.add(box(7, 5, -1, 2, .3, 2), 'different-style-b', color=(100, 100, 180))
        m.add(box(0, 8, -.2, 4, 4, .4), 'overlap-slab')
        m.add(box(1.8, 9.8, -1, .4, .4, 3), 'overlap-column')
        m.add(box(5, 8, -.3, 2, 2, .3), 'coplanar-top')
        for i, factor in enumerate((-.5, .5, -2, 2)):
            m.add(box(10 + i * 1.5, 5, -1, 1, 1, 1 + factor * TOL), f'contact-{factor:+g}tol')
        m.add(G.PlaneSurface(G.Plane(G.Point3d(9, 9, 0), G.Vector3d.XAxis, G.Vector3d.ZAxis), G.Interval(0, 2), G.Interval(-1, 1)), 'open-crossing')
        # L = two arms in QI. Rotated rectangular strip x+y in [3,4] disconnects the arms.
        m.add(moved(profile([(0, 0), (5, 0), (5, 1), (1, 1), (1, 5), (0, 5), (0, 0)]), 16, 10), 'crop-L')
        # Crop axes diagonal to split a connected L into two regions.
        feature_views(m)
        a = math.sqrt(.5)
        m.add_view(view('L split rectangle', 'rectangle', None, plane_spec((17.75, 11.75, 0), (a, -a, 0), (a, a, 0)),
                        crop=[-4, 4, -.5 * a, .5 * a], size=[8, a], expected={'source': 'crop-L', 'cutRegions': 2, 'holes': 0, 'totalArea': 2.0}))
        m.expected = {'analyticSections': {'rhs_hollow': {'regions': 1, 'holes': 1, 'area': 2.04},
                     'chs_hollow': {'regions': 1, 'holes': 1, 'area': .51 * math.pi}, 'i_beam': {'regions': 1, 'holes': 0, 'area': 1.76},
                     'island-inside-rhs-void': {'regions': 1, 'holes': 0, 'area': .25},
                     'overlap-slab': {'regions': 1, 'area': 16}, 'overlap-column': {'regions': 1, 'area': .16}},
                     'coplanar-top': {'cutRegions': 0}, 'open-crossing': {'hatch': None},
                     'mergeSameStyleRegions': 1, 'mergeDifferentStyleRegions': 2}
        results.append(m.save(hook=True))
    if want('view-range'):
        m = Model('view-range', seed, out)
        for i in range(4):
            m.add(box(i * 4, 0, -4 + i, 4, 8 - i, .3), f'terrace-{i}')
        for depth in (2, 3, 4):
            m.add(box(depth * 2, 10, -depth - .2, 1, 1, .2), f'depth-{depth}m')
        m.add(box(0, 10, -7, 1, 2, 5), 'crosses-far-plane')
        m.add(box(18, 0, -10, .2, 12, 12), 'long-receding-wall')
        for far in ('none', 'line', 'noline'):
            v = view('Range ' + far, 'GH-style plane', None, plane_spec())
            v['settings'].update(depth='5 m', farclip=far, beyond='3 m', fade='on', **{'fade.start': '10 %', 'fade.end': '90 %', 'fade.limit': '25 %'})
            m.add_view(v)
        m.expected = {'visibleNearThreshold': ['depth-2m', 'depth-3m'], 'beyondThreshold': ['depth-4m'],
                      'farClipSource': 'crosses-far-plane', 'farDepth': 5, 'thresholdStrictlyGreater': True}
        results.append(m.save(hook=True))
    if want('glass'):
        m = Model('glass', seed, out); mi = m.material('Transparent glazing', (125, 190, 205), .85)
        for i in range(5):
            m.add(box(i * 1.5, 0, 0, 1.45, .06, 3), f'glass-{i}', 'Glass', material=mi)
            m.add(box(i * 1.5, -.03, 0, .05, .12, 3), f'mullion-{i}', 'Envelope')
            m.add(box(i * 1.5 + .5, 2, .5, .6, .6, .5), f'chair-{i}', 'Fixtures')
        m.add(moved(cylinder(.25, 3), 3, 3), 'column-behind-glass')
        for clear in ('opaque', 'clear'):
            v = view('Front ' + clear, value='Front'); v['settings']['glass'] = clear; m.add_view(v)
        m.add_view(view('SW', value='SW'))
        m.expected = {'opaqueHidden': ['column-behind-glass'], 'clearVisible': ['column-behind-glass'], 'rawHldIgnoresMaterials': True}
        results.append(m.save(hook=True))
    if want('blocks'):
        m = Model('blocks', seed, out)
        asym = profile([(0, 0), (2, 0), (2, .3), (.5, .3), (.5, 1), (0, 1), (0, 0)], 0, 1).ToBrep()
        m.definition('asymmetric', [asym])
        previous = 'asymmetric'
        for i in range(3):
            name = f'nest-{i + 2}'
            m.definition(name, [G.InstanceReferenceGeometry(m.defs[previous], G.Transform.Translation(.2, .1, .3))]); previous = name
        m.instance(previous, G.Transform.Identity, 'four-levels')
        m.instance('asymmetric', G.Transform.Translation(5, 0, 0) * G.Transform.Mirror(G.Plane.WorldYZ), 'mirrored-asymmetric')
        m.instance('asymmetric', G.Transform.Translation(8, 0, 0) * G.Transform.Scale(G.Plane.WorldXY, 1.7, .6, 1.3), 'nonuniform-asymmetric')
        window = profile([(0, 0), (1, 0), (1, .08), (0, .08), (0, 0)], 0, 1.2)
        m.definition('window', [window])
        for i in range(200):
            t = G.Transform.Translation((i % 20) * 2, (i // 20) * 2 + 5, 0) * G.Transform.Rotation((i % 4) * math.pi / 2, G.Vector3d.ZAxis, G.Point3d.Origin)
            m.instance('window', t, f'window-instance-{i}')
            g = window.Duplicate(); g.Transform(G.Transform.Translation(0, 25, 0) * t)
            m.add(g, f'window-exploded-{i}')
        feature_views(m, .6)
        m.expected = {'nestedLevels': 4, 'windowInstances': 200, 'windowCopies': 200, 'rotationsDegrees': [0, 90, 180, 270],
                      'metamorphicTranslationCopies': [0, 25, 0]}
        results.append(m.save())
    if want('edges'):
        m = Model('edges', seed, out)
        b = box(0, 0, 0, 2, 2, 2)
        fillets = G.Brep.CreateFilletEdges(b, arr(range(b.Edges.Count), S.Int32), arr([.2] * b.Edges.Count, S.Double),
                                         arr([.2] * b.Edges.Count, S.Double), G.BlendType.Fillet, G.RailType.RollingBall, TOL)
        if not fillets:
            raise RuntimeError('Filleted box construction failed')
        m.add(fillets[0], 'filleted-box')
        m.add(moved(cylinder(.8, 2), 4, 0), 'cylinder-seam')
        crv = G.NurbsCurve.Create(False, 3, arr([G.Point3d(7 + r, 0, z) for r, z in [(1, 0), (.6, .7), (1.3, 1.3), (.7, 2)]], G.Point3d))
        rev = G.RevSurface.Create(crv, G.Line(G.Point3d(7, 0, 0), G.Point3d(7, 0, 2)))
        m.add(rev, 'revolved-seam')
        m.add(moved(canopy(), 0, 4, 1), 'SubD-canopy')
        e = profile([(0, 0), (1, 0), (1, 1), (0, 1), (0, 0)], 0, 2)
        m.add(moved(e, 6, 4), 'extrusion-twin'); m.add(moved(e.ToBrep(), 8, 4), 'brep-twin')
        feature_views(m, 1)
        m.expected = {'rawUnsupported': {'SubD': 1}, 'twinsTranslation': [2, 0, 0], 'tangents': 'off', 'seams': 'off'}
        results.append(m.save())
    if want('curves-points'):
        m = Model('curves-points', seed, out)
        m.add(box(-3, -1, -1, 6, 2, 2), 'concrete-beam')
        for y in (-.6, .6):
            m.add(G.LineCurve(G.Point3d(-2.8, y, 0), G.Point3d(2.8, y, 0)), f'rebar-{y}', 'Fixtures')
        for x in (-4, 0, 4):
            m.add(G.LineCurve(G.Point3d(x, -3, 0), G.Point3d(x, 3, 0)), f'grid-{x}', 'Fixtures')
        for name, xyz in [('front-point', (1, 0, 2)), ('behind-point', (1, 0, -2)), ('outside-point', (4, 2, 0))]:
            m.add(G.Point(G.Point3d(*xyz)), name, 'Fixtures')
        m.add(G.LineCurve(G.Point3d(-2, .3, 1), G.Point3d(2, .3, 1)), 'curve-on-top', 'Fixtures')
        feature_views(m)
        m.expected = {'Top': {'hidden': ['rebar--0.6', 'rebar-0.6', 'behind-point'],
                              'visible': ['front-point', 'outside-point', 'curve-on-top']}, 'pointPath': 'HLD.Points'}
        results.append(m.save(hook=True))
    if want('units-coords'):
        for unit, factor, offset, suffix in [('Meters', 1, (0, 0, 0), 'm'), ('Millimeters', 1000, (0, 0, 0), 'mm'),
                                             ('Feet', 1 / .3048, (0, 0, 0), 'ft'), ('Meters', 1, (1e6, 1e6, 1e6), 'offset')]:
            m = Model('units-coords-' + suffix, seed, out, unit, factor, offset); building(m)
            m.add_view(view('Top')); m.add_view(view('SW', value='SW'))
            m.add_view(view('Plan cut', 'GH-style plane', None, plane_spec((offset[0], offset[1], offset[2] + 1.2 * factor))))
            m.expected = {'metamorphicGroup': 'units-coords', 'modelUnitsPerMetre': factor, 'translationModelUnits': list(offset),
                          'sameTopologyAfterInverseTransform': True}
            results.append(m.save(hook=True))
    if want('annotations'):
        m = Model('annotations', seed, out)
        for name, x in [('text_entity', 0), ('text_dot', 3)]:
            m.add(moved(m.fixture(name), x), name, 'Fixtures')
        dimstyle = R.DocObjects.DimensionStyle(); dimstyle.Id, dimstyle.Name = m.uid('dimstyle'), 'Metres'
        m.file.AllDimStyles.Add(dimstyle)
        d = G.LinearDimension.FromPoints(G.Point3d(0, 3, 0), G.Point3d(5, 3, 0), G.Point3d(2, 4, 0)); d.DimensionStyleId = dimstyle.Id
        m.add(d, 'dimension', 'Fixtures')
        leader = G.Leader.Create('Unsupported leader', G.Plane.WorldXY, dimstyle, arr([G.Point3d(0, 5, 0), G.Point3d(2, 6, 0), G.Point3d(4, 6, 0)], G.Point3d))
        m.add(leader, 'leader', 'Fixtures')
        c = G.Circle(G.Plane(G.Point3d(7, 4, 0), G.Vector3d.ZAxis), 1).ToNurbsCurve()
        h = G.Hatch.Create(arr([c], G.Curve), 0, 0, 1, TOL)[0]; m.add(h, 'hatch', 'Fixtures')
        m.add_view(view('Top'))
        m.expected = {'unsupportedOrIgnored': 5, 'types': ['TextEntity', 'TextDot', 'LinearDimension', 'Leader', 'Hatch']}
        results.append(m.save(hook=True))
    return results


def performance(tier, flavor, seed, out):
    target, buildings, floors = TIERS[tier]
    m = Model(f'perf-{tier}-{flavor}', seed, out)
    # One geometry per reusable definition makes top-level and expanded object counts comparable.
    # Every floor includes architectural parts, not generic random boxes.
    prototypes = {
        'slab': box(0, 0, -.25, 6, 6, .25), 'core': box(2, 2, 0, .25, 3, 3),
        'column': box(0, 0, 0, .35, .35, 3), 'CHS': moved(m.fixture('chs_hollow'), scale=.18),
        'RHS': moved(m.fixture('rhs_hollow'), scale=.18),
        'wall-pier': box(0, 0, 0, .2, 1, 3), 'wall-lintel': box(0, 1, 2.3, .2, 2, .7),
        'glass-panel': box(0, 0, 0, 1.4, .04, 2.7), 'mullion': box(0, 0, 0, .05, .1, 3),
        'stair-tread': box(0, 0, 0, 1.2, .3, .18), 'chair': box(0, 0, 0, .5, .5, .45),
        'desk': box(0, 0, .7, 1.2, .6, .06), 'roof-plant': box(0, 0, 0, 1.4, .8, .7),
        'curved-fin': G.Extrusion.Create(G.Arc(G.Point3d(0, 0, 0), G.Point3d(.4, .2, 0), G.Point3d(.6, .8, 0)).ToNurbsCurve(), 2.8, False),
        'SubD-canopy': canopy()}
    # Stretch hollow members to a storey while preserving their profile/void.
    for key in ('CHS', 'RHS'):
        prototypes[key].Transform(G.Transform.Scale(G.Plane.WorldXY, 1, 1, 3 / .36))
        prototypes[key].Transform(G.Transform.Translation(0, 0, 1.5))
    mi = m.material('Transparent glazing', (125, 190, 205), .85)
    def layer(key):
        return 'Glass' if key == 'glass-panel' else ('Fixtures' if key in ('chair', 'desk', 'roof-plant') else 'Structure')
    if flavor == 'blocks':
        for key, geo in prototypes.items():
            attrs = m.attrs(key + '-member', layer(key), material=mi if key == 'glass-panel' else None)
            m.definition(key, [geo], [attrs])
    # A room cell contains a slab, frame, opening, glazed front, stairs and furniture.
    parts = [('slab', 0, 0, 0), ('core', 0, 0, 0), ('column', .1, .1, 0), ('CHS', 5.7, .2, 0),
             ('RHS', .2, 5.7, 0), ('column', 5.7, 5.7, 0), ('wall-pier', 0, 0, 0),
             ('wall-pier', 0, 3, 0), ('wall-lintel', 0, 0, 0), ('glass-panel', 1, 0, 0),
             ('glass-panel', 2.5, 0, 0), ('glass-panel', 4, 0, 0), ('mullion', 1, 0, 0),
             ('mullion', 2.5, 0, 0), ('mullion', 4, 0, 0), ('mullion', 5.5, 0, 0),
             ('desk', 2, 3, 0), ('chair', 2.3, 4, 0), ('desk', 4, 3, 0), ('chair', 4.3, 4, 0)]
    parts += [('stair-tread', 4.5, .3 + .3 * i, .18 * i) for i in range(12)]
    parts += [('curved-fin', 5.5, 0, 0)]
    parts += [('mullion', 1 + i * .3, 5.95, 0) for i in range(10)]
    parts += [('chair', 1 + i * .8, 4.9, 0) for i in range(5)]
    terrain_count = 1 if tier in ('XL', 'XXL') else 0
    podium_count = 200 if tier == 'L' else 0
    regular_target = target - buildings * 2 - terrain_count - podium_count
    cells = math.ceil(regular_target / len(parts))
    per_building = math.ceil(cells / buildings)
    per_floor = math.ceil(per_building / floors)
    nx = math.ceil(math.sqrt(per_floor)); ny = math.ceil(per_floor / nx)
    width, length = nx * 6, ny * 6
    building_columns = math.ceil(math.sqrt(buildings))
    emitted = 0
    for cell in range(cells):
        b = min(cell // per_building, buildings - 1)
        local = cell - b * per_building
        level = min(local // per_floor, floors - 1); xy = local % per_floor
        bx, by = (b % building_columns) * (width + 15), (b // building_columns) * (length + 15)
        cx, cy, cz = bx + (xy % nx) * 6, by + (xy // nx) * 6, level * 3
        for key, x, y, z in parts:
            if emitted >= regular_target:
                break
            # Seed controls furniture position, identically in both representations.
            jitter = m.rng.uniform(-.1, .1) if key in ('chair', 'desk') else 0
            t = G.Transform.Translation(cx + x + jitter, cy + y, cz + z)
            name = f'b{b:02}-f{level:02}-c{xy:03}-{key}-{emitted:06}'
            if flavor == 'blocks':
                m.instance(key, t, name, layer=layer(key), material=mi if key == 'glass-panel' else None)
            else:
                geo = prototypes[key].Duplicate(); geo.Transform(t)
                m.add(geo, name, layer(key), material=mi if key == 'glass-panel' else None)
            emitted += 1
    for b in range(buildings):
        bx, by = (b % building_columns) * (width + 15), (b // building_columns) * (length + 15)
        for key, x, y, z in [('roof-plant', 2, 2, floors * 3), ('SubD-canopy', 0, -4, 2.8)]:
            t = G.Transform.Translation(bx + x, by + y, z)
            if flavor == 'blocks':
                m.instance(key, t, f'building-{b}-{key}', layer=layer(key))
            else:
                geo = prototypes[key].Duplicate(); geo.Transform(t); m.add(geo, f'building-{b}-{key}', layer(key))
    for i in range(podium_count):
        # Three low podium levels extend beyond the 30-storey tower footprint.
        key = ('slab', 'column', 'glass-panel', 'mullion')[i % 4]
        bay = i // 4; level = bay // 18; bay %= 18
        t = G.Transform.Translation(-12 + (bay % 9) * 6, -12 + (bay // 9) * (length + 18), level * 3)
        if flavor == 'blocks':
            m.instance(key, t, f'podium-{i}', layer=layer(key), material=mi if key == 'glass-panel' else None)
        else:
            geo = prototypes[key].Duplicate(); geo.Transform(t)
            m.add(geo, f'podium-{i}', layer(key), material=mi if key == 'glass-panel' else None)
    site_width = building_columns * (width + 15)
    site_length = math.ceil(buildings / building_columns) * (length + 15)
    if terrain_count:
        mesh = G.Mesh(); n = 40
        for j in range(n + 1):
            for i in range(n + 1):
                mesh.Vertices.Add(-5 + (site_width + 10) * i / n, -5 + (site_length + 10) * j / n,
                                  -.6 + .12 * math.sin(i * .3) * math.cos(j * .2))
        for j in range(n):
            for i in range(n):
                a = j * (n + 1) + i
                mesh.Faces.AddFace(a, a + 1, a + n + 2); mesh.Faces.AddFace(a, a + n + 2, a + n + 1)
        mesh.Normals.ComputeNormals(); m.add(mesh, 'terrain')
    center = (site_width / 2 - 7.5, site_length / 2 - 7.5, floors * 1.5)
    half = max(site_width + (24 if podium_count else 0), site_length + (24 if podium_count else 0), floors * 3) * .7
    for name, x, y in [('South elevation', (1, 0, 0), (0, 0, 1)), ('North elevation', (-1, 0, 0), (0, 0, 1)),
                       ('East elevation', (0, 1, 0), (0, 0, 1)), ('West elevation', (0, -1, 0), (0, 0, 1))]:
        m.add_view(view(name, 'named view', name, plane_spec(center, x, y), frame=[-half, half, -half, half], cameraDistance=1000))
    for name, height in [('Plan level 1', 1.2), ('Plan upper', max(1.8, (floors - 1) * 3 + 1.2))]:
        m.add_view(view(name, 'clipping plane name', name, plane_spec((center[0], center[1], height))))
    for name, p in [('Section transverse', plane_spec((width / 2, center[1], 0), (0, 1, 0), (0, 0, 1))),
                    ('Section long overlap', plane_spec((center[0], length / 2, 0), (1, 0, 0), (0, 0, 1)))]:
        m.add_view(view(name, 'clipping plane name', name, p))
    a = math.sqrt(.5); b = 1 / math.sqrt(6)
    m.add_view(view('SW axo', 'named view', 'SW axo', plane_spec(center, (a, -a, 0), (b, b, 2 * b)), frame=[-half, half, -half, half], cameraDistance=1000))
    m.expected = {'targetObjects': target, 'buildings': buildings, 'floors': floors, 'cellsPerFloor': per_floor,
                  'podiumObjects': podium_count,
                  'equivalentFlavor': f'perf-{tier}-' + ('exploded' if flavor == 'blocks' else 'blocks'),
                  'rawUnsupportedSubD': buildings, 'fullVisibilityTruth': 'unknown'}
    return m.save(tier=tier, flavor=flavor)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, default=ROOT / 'artifacts/models')
    parser.add_argument('--seed', type=int, default=20261114)
    parser.add_argument('--features', action='store_true')
    parser.add_argument('--only', help='Comma-separated feature groups')
    parser.add_argument('--tiers', default='', help='Comma-separated XS,S,M,L,XL,XXL')
    parser.add_argument('--flavors', default='blocks,exploded')
    parser.add_argument('--single', action='store_true', help=argparse.SUPPRESS)
    args = parser.parse_args()
    if not args.single:
        # A fresh process per model group fixes native SubD serial/cache metadata,
        # independently of selection order, and releases large generation allocations.
        jobs = []
        if args.features or args.only:
            for name in args.only.split(',') if args.only else FEATURES:
                if name not in FEATURES:
                    parser.error('Unknown feature group ' + name)
                jobs.append(['--only', name])
        for tier in filter(None, args.tiers.split(',')):
            if tier not in TIERS:
                parser.error('Unknown tier ' + tier)
            for flavor in args.flavors.split(','):
                jobs.append(['--tiers', tier, '--flavors', flavor])
        for job in jobs:
            print('generate', ' '.join(job), flush=True)
            subprocess.run([sys.executable, '-B', '-u', str(Path(__file__).resolve()), '--single',
                            '--seed', str(args.seed), '--out', str(args.out.resolve()), *job], check=True,
                           stdout=sys.stdout, stderr=sys.stderr, creationflags=subprocess.CREATE_NO_WINDOW)
        return
    global R, S, G
    R, S = initialize(); G = R.Geometry
    if args.features or args.only:
        features(args.seed, args.out, args.only.split(',') if args.only else None)
    for tier in filter(None, args.tiers.split(',')):
        for flavor in args.flavors.split(','):
            if flavor not in ('blocks', 'exploded'):
                parser.error('Unknown flavor ' + flavor)
            performance(tier, flavor, args.seed, args.out)


if __name__ == '__main__':
    if '--single' in sys.argv:
        with SerialRhino():
            try:
                main()
            finally:
                for model in OPEN_MODELS:
                    model.Dispose()
    else:
        try:
            main()
        finally:
            for model in OPEN_MODELS:
                model.Dispose()
