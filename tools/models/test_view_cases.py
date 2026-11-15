"""Manual unlicensed checks of frozen view contracts and independent profile oracles."""
import json
import math
import unittest
import tempfile
from collections import defaultdict
from common import ROOT,digest


class ViewCaseFixtures(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.folder=ROOT/'tests/fixtures/view-cases'
        cls.frozen=[json.loads(p.read_text(encoding='utf-8')) for p in cls.folder.glob('*.json')]
        if not cls.frozen: raise RuntimeError('Freeze the view cases first')
        models={m['id']:m for m in cls.frozen}
        # Include large generated sidecars when present; a fresh clone still checks all frozen cases.
        for p in (ROOT/'artifacts/view-models').glob('*.json'):
            m=json.loads(p.read_text(encoding='utf-8')); models.setdefault(m['id'],m)
        cls.models=list(models.values())

    def test_frozen_size_hash_and_opt_in_policy(self):
        for m in self.frozen:
            with self.subTest(model=m['id']):
                path=self.folder/(m['id']+'.json')
                self.assertLessEqual(path.stat().st_size,2_000_000)
                self.assertFalse((self.folder/(m['id']+'.3dm')).exists())
                manifest=json.loads((ROOT/'tests/fixtures/manifest.json').read_text())
                entry=next(r for r in manifest['fixtures'] if r['id']=='models/'+m['id']+'.3dm')
                self.assertEqual(entry['definitionSha256'],digest(path)); self.assertFalse(m['hook'])
                self.assertTrue(entry['external']); self.assertTrue(entry['manualOnly'])

    def test_every_view_has_right_handed_orthonormal_frame_and_back_face(self):
        for m in self.models:
            for v in m['views']:
                with self.subTest(model=m['id'],view=v['name']):
                    p=v['plane']; x,y=p['x'],p['y']; cross=[x[1]*y[2]-x[2]*y[1],x[2]*y[0]-x[0]*y[2],x[0]*y[1]-x[1]*y[0]]
                    self.assertAlmostEqual(sum(t*t for t in x),1,places=12)
                    self.assertAlmostEqual(sum(t*t for t in y),1,places=12)
                    self.assertAlmostEqual(sum(a*b for a,b in zip(x,y)),0,places=12)
                    self.assertGreater(v['depth'],0)
                    for i in range(3): self.assertAlmostEqual(v['viewBox']['pathEnd'][i],p['origin'][i]-cross[i]*v['depth'],places=10)
                    self.assertEqual(v['ghViews']['box']['z'],[-v['depth'],0])
                    self.assertEqual(v['ghViews']['plane'],p)
                    for kind in ('rectangle','box'):
                        self.assertEqual(v['ghViews'][kind]['plane'],p)
                        self.assertEqual(v['ghViews'][kind]['x'],v['crop'][:2])
                        self.assertEqual(v['ghViews'][kind]['y'],v['crop'][2:])
                    if 'pathTangent' in v['expected']:
                        tangent=v['expected']['pathTangent']; length=math.sqrt(sum(t*t for t in tangent))
                        self.assertAlmostEqual(sum(a*b for a,b in zip(cross,tangent))/length,1,places=11)

    def test_five_interior_stations_per_selected_rail(self):
        for m in self.models:
            groups=defaultdict(list)
            for v in m['views']:
                if v['case']==6: groups[v['expected']['source']].append(v['expected']['station'])
            for source,stations in groups.items():
                with self.subTest(model=m['id'],source=source):
                    self.assertEqual(len(stations),5); self.assertEqual(len(set(stations)),5)
                    self.assertTrue(all(0<s<1 for s in stations))

    def test_radial_views_cover_24_distinct_directions_about_axis(self):
        m=next(m for m in self.models if m['id']=='view-round-tower')
        radial=[v for v in m['views'] if 'azimuthDegrees' in v['expected']]
        self.assertEqual(len(radial),24)
        directions={(v['expected']['azimuthDegrees']+(180 if v['expected']['direction']==-1 else 0))%360 for v in radial}
        self.assertEqual(directions,set(range(0,360,15)))
        self.assertTrue(all(v['plane']['origin']==[0,0,0] for v in radial))
        self.assertEqual(len(m['views']),26)

    def test_independent_profile_areas(self):
        m=next(m for m in self.models if m['id']=='view-curved-steel')
        for v in m['views']:
            e=v['expected']; name=e.get('source','')
            if name=='CHS-arc': self.assertAlmostEqual(e['area'],math.pi*(.12+.095)*(.12-.095),places=12)
            if name in ('RHS-arc','twisted-RHS'): self.assertAlmostEqual(e['area'],.09,places=12); self.assertEqual(e['loops'],2)
            if name=='I-beam-filleted-web-flanges':
                self.assertAlmostEqual(e['area'],.3+.14-(4-math.pi)*.04**2,places=12)

    def test_rotated_references_have_matching_named_views(self):
        models={m['id']:m for m in self.models}
        for m in self.models:
            for v in m['views']:
                exp=v['expected'].get('metamorphic')
                if not exp: continue
                ref=models[exp['referenceModel']]
                self.assertEqual({x['name'] for x in ref['views']},{x['name'] for x in m['views']})
                self.assertEqual(len(m['views']),12)
                base=next(x for x in ref['views'] if x['name']==v['name'])['plane']
                a=math.radians(exp['rotationDegrees']); c,s=math.cos(a),math.sin(a)
                for axis in ('x','y'):
                    x,y,z=base[axis]; rotated=(c*x-s*y,s*x+c*y,z)
                    for expected,actual in zip(rotated,v['plane'][axis]): self.assertAlmostEqual(expected,actual,places=11)


class IndependentOutputAudit(unittest.TestCase):
    def test_polygon_area_subtracts_hole_and_rejects_equal_area_wrong_profile(self):
        from view_audit import profile_check
        rows=[{'class':'Cut','source':'test','points':points} for points in
              [[[-.2,-.3],[.2,-.3],[.2,.3],[-.2,.3],[-.2,-.3]],
               [[-.15,-.25],[.15,-.25],[.15,.25],[-.15,.25],[-.15,-.25]]]]
        expectation={'source':'test','area':.09,'loops':2,'areaTolerance':1e-8,'profile':'RHS'}
        with tempfile.TemporaryDirectory() as folder:
            path=__import__('pathlib').Path(folder)/'output.json'; path.write_text(json.dumps(rows))
            self.assertEqual(profile_check(path,expectation,1e-4)['status'],'pass')
            # Area-preserving anisotropic deformation must fail the independent shape check.
            for row in rows: row['points']=[[x*2,y/2] for x,y in row['points']]
            path.write_text(json.dumps(rows))
            result=profile_check(path,expectation,1e-4)
            self.assertAlmostEqual(result['area'],.09,places=12)
            self.assertEqual(result['status'],'fail')
            self.assertFalse(result['rotatedProfileShapePassed'])

    def test_open_native_cut_cannot_pass_by_area_alone(self):
        from view_audit import profile_check
        with tempfile.TemporaryDirectory() as folder:
            path=__import__('pathlib').Path(folder)/'output.json'
            path.write_text(json.dumps([{'class':'Cut','source':'test','points':[[0,0],[1,0],[1,1],[0,1]]}]))
            result=profile_check(path,{'source':'test','area':1,'loops':1,'areaTolerance':1e-4},1e-4)
            self.assertEqual(result['status'],'fail'); self.assertEqual(result['openChains'],1)

    def test_class_swap_is_distinct_from_missing_geometry(self):
        from view_audit import initialize_core,compare
        initialize_core()
        with tempfile.TemporaryDirectory() as folder:
            a=__import__('pathlib').Path(folder)/'a.json'; b=a.with_name('b.json')
            a.write_text(json.dumps([{'class':'Visible','source':'a','points':[[0,0],[1,0]]}]))
            b.write_text(json.dumps([{'class':'Hidden','source':'b','points':[[0,0],[1,0]]}]))
            result=compare(a,b,1e-4)
            self.assertFalse(result['passed'])
            self.assertEqual(result['expectedToActual']['missingInkSamples'],0)
            self.assertGreater(result['expectedToActual']['classOnlySamples'],0)
            self.assertGreater(result['expectedToActual']['confidentVisibleHiddenSwapSamples'],0)
            b.write_text(json.dumps([{'class':'Unresolved','source':'b','points':[[0,0],[1,0]]}]))
            result=compare(a,b,1e-4)
            self.assertGreater(result['expectedToActual']['uncertaintyClassSamples'],0)
            self.assertEqual(result['expectedToActual']['confidentVisibleHiddenSwapSamples'],0)
            b.write_text(json.dumps([{'class':'Visible','source':'b','points':[[0,1],[1,1]]}]))
            result=compare(a,b,1e-4)
            self.assertGreater(result['expectedToActual']['missingInkSamples'],0)


class BoundedCurveCollector(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        from pathlib import Path
        binary=ROOT/'artifacts/view-engine-v4-bin/M2DPlus.Render.dll'
        reference=Path('C:/Program Files/Rhino 8/System/netcore/RhinoCommon.dll')
        if not binary.exists() or not reference.exists(): raise unittest.SkipTest('Build the local render bridge first')
        from view_audit import initialize_core
        initialize_core(); import clr
        # Load managed types only. No RhinoCore, document or native geometry operation.
        clr.AddReference(str(reference)); clr.AddReference(str(binary))
        from M2DPlus.Render import ViewCurveTessellation
        cls.collect=staticmethod(lambda control,tolerance:json.loads(ViewCurveTessellation.ControlJson(json.dumps(control),tolerance)))

    def test_rational_circle_and_independent_cubic_samples(self):
        tolerance=1e-4
        result=self.collect([[1,0,0,1],[1,1,0,math.sqrt(.5)],[0,1,0,1]],tolerance)
        self.assertTrue(result['Success']); self.assertLessEqual(result['ErrorBound'],tolerance)
        for x,y,z in result['Points']: self.assertAlmostEqual(math.hypot(x,y),1,places=12)
        for a,b in zip(result['Points'],result['Points'][1:]):
            self.assertLessEqual(1-math.hypot((a[0]+b[0])/2,(a[1]+b[1])/2),tolerance)
        control=[[0,0,0,1],[1,2,0,2],[2,-1,1,.5],[3,0,2,1]]
        result=self.collect(control,tolerance); self.assertTrue(result['Success'])
        # Independent Bernstein evaluation, rather than the collector's subdivision recurrence.
        for index in range(101):
            t=index/100; weights=[(1-t)**3,3*t*(1-t)**2,3*t*t*(1-t),t**3]
            total=sum(w*p[3] for w,p in zip(weights,control))
            q=[sum(w*p[3]*p[j] for w,p in zip(weights,control))/total for j in range(3)]
            distances=[]
            for a,b in zip(result['Points'],result['Points'][1:]):
                d=[y-x for x,y in zip(a,b)]; length=sum(x*x for x in d)
                s=max(0,min(1,sum((q[j]-a[j])*d[j] for j in range(3))/length)) if length else 0
                distances.append(math.sqrt(sum((q[j]-a[j]-s*d[j])**2 for j in range(3))))
            self.assertLessEqual(min(distances),tolerance+1e-12)

    def test_small_closed_loop_survives_and_nonpositive_weights_fail(self):
        result=self.collect([[0,0,0,1],[1e-7,1e-7,0,1],[0,0,0,1]],1e-4)
        self.assertTrue(result['Success']); self.assertGreaterEqual(len(result['Points']),3)
        self.assertTrue(any(p!=result['Points'][0] for p in result['Points']))
        self.assertFalse(result['PointOnly'])
        point=self.collect([[2,3,4,1],[2,3,4,1]],1e-4)
        self.assertTrue(point['Success']); self.assertTrue(point['PointOnly'])
        self.assertEqual(point['Points'],[[2,3,4],[2,3,4]])
        for weight in (0,-1):
            result=self.collect([[0,0,0,1],[1,0,0,weight]],1e-4)
            self.assertFalse(result['Success']); self.assertFalse(result['Points'])


if __name__=='__main__': unittest.main()
