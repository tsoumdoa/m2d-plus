# -*- coding: utf-8 -*-
"""Runs ONLY inside the dedicated Rhino.exe launched by screenshots.py.

IronPython 2.7-compatible. It modifies only the new process's in-memory document,
never saves the source model, and exits that process after its captures.
"""
import json
import os
import traceback

import Rhino
import System
from System.Drawing import Bitmap, Color, Font, FontStyle, Graphics, PointF, SolidBrush
from System.Drawing.Imaging import ImageFormat, PixelFormat


def capture(request):
    path = request['model'].replace('\\', '/')
    if not Rhino.RhinoApp.RunScript('_-Open "' + path + '" _Enter', False):
        raise RuntimeError('Opening the model failed')
    doc = Rhino.RhinoDoc.ActiveDoc
    view = doc.Views.ActiveView
    if view is None:
        raise RuntimeError('No UI-capable Rhino view')
    view.Maximized = True
    vp = view.ActiveViewport
    G = Rhino.Geometry
    bounds = G.BoundingBox.Empty
    for obj in doc.Objects.GetObjectList(Rhino.DocObjects.ObjectType.AnyObject):
        if obj.IsInstanceDefinitionGeometry or isinstance(obj.Geometry, G.ClippingPlaneSurface):
            continue
        bounds.Union(obj.GetBoundingBox(None))
    if not bounds.IsValid:
        raise RuntimeError('Model has no usable bounds')
    # View-source frames are explanatory objects in this disposable document only.
    overlay = Rhino.DocObjects.ObjectAttributes()
    overlay.ColorSource = Rhino.DocObjects.ObjectColorSource.ColorFromObject
    overlay.ObjectColor = Color.FromArgb(30, 145, 125)
    overlay.Name = 'M2D reference view frame (not saved)'
    for spec in request['views']:
        if spec['source']['kind'] not in ('named view', 'clipping plane name', 'rectangle', 'box'):
            continue
        p = spec.get('plane')
        if p is None:
            continue
        pl = G.Plane(G.Point3d(*p['origin']), G.Vector3d(*p['x']), G.Vector3d(*p['y']))
        if spec['source']['kind'] == 'clipping plane name':
            extent = max(1, min(bounds.Diagonal.Length * .08, 5))
            frame = [-extent, extent, -extent, extent]
        else:
            frame = spec.get('crop', spec.get('frame'))
        if frame:
            rectangle = G.Rectangle3d(pl, G.Interval(frame[0], frame[1]), G.Interval(frame[2], frame[3]))
            doc.Objects.AddCurve(rectangle.ToNurbsCurve(), overlay)
            arrow = G.Line(pl.Origin, pl.Origin + pl.ZAxis * max(bounds.Diagonal.Length * .035, .2))
            doc.Objects.AddLine(arrow, overlay)
            if request['id'] == 'view-sources':
                doc.Objects.AddTextDot(spec['name'], pl.Origin, overlay)
    mode = Rhino.Display.DisplayModeDescription.GetDisplayMode(Rhino.Display.DisplayModeDescription.ShadedId)
    if mode is None:
        raise RuntimeError('Shaded display mode unavailable')
    vp.DisplayMode = mode
    vp.ChangeToParallelProjection(True)
    directions = {'SW': (1, 1, -1), 'Front': (0, 1, 0), 'Top': (0, 0, -1)}
    outputs = []
    for name in request['captures']:
        direction = G.Vector3d(*directions[name]); direction.Unitize()
        up = G.Vector3d.YAxis if name == 'Top' else G.Vector3d.ZAxis
        vp.SetCameraLocation(bounds.Center - direction * max(bounds.Diagonal.Length * 2, 10), False)
        vp.SetCameraDirection(direction, False)
        vp.CameraUp = up
        vp.SetCameraTarget(bounds.Center, False)
        vp.ZoomBoundingBox(bounds)
        doc.Views.Redraw()
        cap = Rhino.Display.ViewCapture()
        cap.Width, cap.Height = 1200, 800
        cap.ScaleScreenItems = False
        cap.DrawGrid, cap.DrawAxes, cap.DrawGridAxes = False, False, False
        cap.TransparentBackground = True
        bitmap = cap.CaptureToBitmap(view)
        if bitmap is None:
            raise RuntimeError('ViewCapture returned no bitmap')
        output = Bitmap(1200, 800, PixelFormat.Format24bppRgb)
        graphics = Graphics.FromImage(output)
        brush = SolidBrush(Color.FromArgb(36, 48, 65))
        font = Font('Segoe UI', 12, FontStyle.Regular)
        try:
            graphics.Clear(Color.FromArgb(247, 249, 252))
            graphics.DrawImage(bitmap, 0, 0, 1200, 800)
            graphics.DrawString(request['id'] + '  |  ' + name + '  |  Rhino Shaded', font, brush, PointF(22, 18))
            graphics.DrawString('{0:,} objects | green: view-source frames'.format(request['objects']), font, brush, PointF(22, 760))
            destination = os.path.join(request['out'], request['id'] + '-' + name + '.png')
            output.Save(destination, ImageFormat.Png)
            outputs.append(destination)
        finally:
            font.Dispose(); brush.Dispose(); graphics.Dispose(); output.Dispose(); bitmap.Dispose()
    return outputs


request_path = os.environ.get('M2DPLUS_CAPTURE_REQUEST')
if not request_path:
    raise RuntimeError('Capture must be launched by screenshots.py')
request = json.load(open(request_path))
result = {'model': request['id'], 'method': 'Rhino.exe Shaded ViewCapture', 'status': 'running'}
with open(request['result'], 'w') as stream:
    json.dump(result, stream)
try:
    result['paths'] = capture(request)
    result['status'] = 'ok'
except Exception as error:
    result['status'] = 'error'
    result['error'] = str(error)
    result['traceback'] = traceback.format_exc()
finally:
    with open(request['result'], 'w') as stream:
        json.dump(result, stream, indent=2)
    # This script is executing in our dedicated owned process. No UI automation,
    # process enumeration, shared preferences or source-file saving is involved.
    Rhino.RhinoDoc.ActiveDoc.Modified = False
    Rhino.RhinoApp.Exit()
