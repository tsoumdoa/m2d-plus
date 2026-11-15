"""Shared model schema utilities. Importing this module does not start Rhino."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import uuid

ROOT = Path(__file__).resolve().parents[2]
SCHEMA = 1
NAMESPACE = uuid.UUID('928846af-0905-49ad-bc90-2ecc903f0783')


def write_json(path, value, sort_keys=True):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(path.suffix + '.tmp')
    temp.write_text(json.dumps(value, indent=2, sort_keys=sort_keys, allow_nan=False) + '\n', encoding='utf-8')
    temp.replace(path)


def digest(path):
    h = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def stable_id(name):
    return str(uuid.uuid5(NAMESPACE, name))


def initialize():
    import rhinoinside
    rhinoinside.load(8, 'net8.0')
    import Rhino
    import System
    return Rhino, System


class SerialRhino:
    """Cross-worktree mutex. Construction tools fail fast; queued runners may wait."""
    def __init__(self, wait_seconds=0, name='Local\\M2DPlusLocalModelSuiteRhino'):
        self.wait_seconds = wait_seconds
        self.name = name

    def __enter__(self):
        import ctypes as C
        from ctypes import wintypes as W
        self.kernel = C.WinDLL('kernel32', use_last_error=True)
        self.kernel.CreateMutexW.argtypes = [C.c_void_p, W.BOOL, W.LPCWSTR]
        self.kernel.CreateMutexW.restype = W.HANDLE
        self.kernel.WaitForSingleObject.argtypes = [W.HANDLE, W.DWORD]
        self.kernel.ReleaseMutex.argtypes = [W.HANDLE]
        self.kernel.CloseHandle.argtypes = [W.HANDLE]
        self.handle = self.kernel.CreateMutexW(None, False, self.name)
        if not self.handle:
            raise C.WinError(C.get_last_error())
        timeout = 0xffffffff if self.wait_seconds is None else int(self.wait_seconds * 1000)
        state = self.kernel.WaitForSingleObject(self.handle, timeout)
        if state not in (0, 0x80):
            self.kernel.CloseHandle(self.handle)
            raise RuntimeError('Another local model-suite Rhino job is active. Run serially.')
        return self

    def __exit__(self, *_):
        self.kernel.ReleaseMutex(self.handle)
        self.kernel.CloseHandle(self.handle)


def plane_spec(origin=(0, 0, 0), x=(1, 0, 0), y=(0, 1, 0)):
    return {'origin': list(origin), 'x': list(x), 'y': list(y)}


def view(name, kind='direction keyword', value='Top', plane=None, **kwargs):
    result = {'name': name, 'source': {'kind': kind, 'value': value},
              'settings': {'hidden': 'on', 'tangents': 'off', 'seams': 'off'}}
    if plane is not None:
        result['plane'] = plane
    result.update(kwargs)
    return result


def expand(model, R):
    """Borrow definition geometry with cumulative transforms; never mutate the model."""
    G = R.Geometry
    objects = {str(o.Id): o for o in model.Objects}
    definitions = {str(d.Id): d for d in model.AllInstanceDefinitions}

    def walk(obj, transform, path, seen):
        geo = obj.Geometry
        name = obj.Attributes.Name or str(obj.Id)
        if isinstance(geo, G.InstanceReferenceGeometry):
            key = str(geo.ParentIdefId)
            if key in seen or key not in definitions:
                raise ValueError('Cyclic/missing definition: ' + key)
            for oid in definitions[key].GetObjectIds():
                yield from walk(objects[str(oid)], transform * geo.Xform, path + [name], seen | {key})
        elif not isinstance(geo, G.ClippingPlaneSurface) and obj.Attributes.GetUserString('m2d.view') != '1':
            yield geo, transform, '/'.join(path + [name]), obj.Attributes

    for obj in model.Objects:
        if not obj.Attributes.IsInstanceDefinitionObject:
            yield from walk(obj, G.Transform.Identity, [], set())


def stats(model, R):
    from collections import Counter
    G = R.Geometry
    top = [o for o in model.Objects if not o.Attributes.IsInstanceDefinitionObject
           and not isinstance(o.Geometry, G.ClippingPlaneSurface) and o.Attributes.GetUserString('m2d.view') != '1']
    types = Counter(str(o.Geometry.GetType().Name) for o in top)
    expanded = Counter()
    faces = triangles = 0
    for geo, _, _, _ in expand(model, R):
        expanded[str(geo.GetType().Name)] += 1
        if isinstance(geo, G.Brep):
            faces += geo.Faces.Count
        elif isinstance(geo, G.Mesh):
            triangles += geo.Faces.TriangleCount + 2 * geo.Faces.QuadCount
    return {'objects': len(top), 'objectTypes': dict(types), 'expandedObjects': sum(expanded.values()),
            'expandedTypes': dict(expanded), 'brepFaces': faces, 'meshTriangles': triangles,
            'blockDefinitions': model.AllInstanceDefinitions.Count,
            'blockInstances': types.get('InstanceReferenceGeometry', 0),
            'definitionMembers': sum(o.Attributes.IsInstanceDefinitionObject for o in model.Objects)}
