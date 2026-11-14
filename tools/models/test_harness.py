"""Fast, unlicensed local tests: archive safety and owned-process isolation."""
import os
import math
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest
import uuid
import zlib
import time

from archive import canonicalize


class AreaOracleTests(unittest.TestCase):
    def test_torus_normal_cut_reduces_to_analytic_annulus(self):
        # A normal cut is an exact circular annulus, independently of bend radius.
        # Check the numerical oracle without Rhino or its intersection code.
        from curved import torus_cut_area
        expected = math.pi * (.6**2 - .45**2)
        for radius in (1, 4, 100):
            with self.subTest(bend_radius=radius):
                actual = torus_cut_area(radius, .6, 0, 32768) - torus_cut_area(radius, .45, 0, 32768)
                self.assertAlmostEqual(actual, expected, delta=5e-8)


def chunk(code, body, crc_body=None):
    crc = zlib.crc32(body if crc_body is None else crc_body)
    return struct.pack('<IQ', code, len(body) + 4) + body + struct.pack('<I', crc)


class ArchiveTests(unittest.TestCase):
    def test_uuid_references_and_nested_parent_crc(self):
        old, new = uuid.uuid4(), uuid.uuid4()
        child = chunk(0x40008002, old.bytes_le)
        raw = b'ABCD' + old.bytes_le
        archive = b'3D Geometry File Format       80' + chunk(0x40008000, child + raw, raw)
        with tempfile.TemporaryDirectory() as temp:
            p = Path(temp) / 'test.3dm'; p.write_bytes(archive)
            canonicalize(p, {str(old): str(new)})
            result = p.read_bytes()
            expected_child = chunk(0x40008002, new.bytes_le)
            expected_raw = b'ABCD' + new.bytes_le
            self.assertEqual(result, archive[:32] + chunk(0x40008000, expected_child + expected_raw, expected_raw))

    def test_unprotected_uuid_fails_without_mutating_file(self):
        old = uuid.uuid4()
        archive = b'3D Geometry File Format       80' + old.bytes_le
        with tempfile.TemporaryDirectory() as temp:
            p = Path(temp) / 'test.3dm'; p.write_bytes(archive)
            with self.assertRaisesRegex(ValueError, 'outside a verified CRC'):
                canonicalize(p, {str(old): str(uuid.uuid4())})
            self.assertEqual(p.read_bytes(), archive)

    def test_bad_crc_is_rejected(self):
        old = uuid.uuid4()
        valid = chunk(0x40008000, old.bytes_le)
        archive = b'3D Geometry File Format       80' + valid[:-1] + bytes([valid[-1] ^ 1])
        with tempfile.TemporaryDirectory() as temp:
            p = Path(temp) / 'test.3dm'; p.write_bytes(archive)
            with self.assertRaises(ValueError):
                canonicalize(p, {str(old): str(uuid.uuid4())})

    def test_nil_uuid_is_rejected_before_opening_file(self):
        with self.assertRaisesRegex(ValueError, 'Nil UUID'):
            canonicalize('must-not-be-opened.3dm', {str(uuid.UUID(int=0)): str(uuid.uuid4())})


@unittest.skipUnless(os.name == 'nt', 'Windows Job Objects')
class SupervisorTests(unittest.TestCase):
    def test_queued_admission_is_outside_execution_budget(self):
        from owned_job import run
        with tempfile.TemporaryDirectory() as temp:
            marker = Path(temp) / 'admission.json'
            marker.write_text('{"status":"admitted","admittedPerfCounter":0}')
            code = ("import sys,time,json,pathlib; assert sys.stdin.readline().strip()=='go'; "
                    "time.sleep(1.2); p=pathlib.Path(sys.argv[1]); q=p.with_suffix('.tmp'); q.write_text(json.dumps(dict(status='admitted',admittedPerfCounter=time.perf_counter()))); q.replace(p); time.sleep(.1)")
            result = run([sys.executable,'-B','-c',code,str(marker)],Path(temp)/'worker.log',.8,.5,
                         admission_path=marker,admission_timeout=5)
        self.assertEqual(result['status'],'exited')
        self.assertGreater(result['admissionSeconds'],1)
        self.assertLess(result['wallSeconds'],.8)
        self.assertGreater(result['elapsedSeconds'],1.2)

    def test_admission_timeout_does_not_report_compute_timeout(self):
        from owned_job import run
        with tempfile.TemporaryDirectory() as temp:
            result = run([sys.executable,'-B','-c',"import sys,time; assert sys.stdin.readline().strip()=='go'; time.sleep(30)"],
                         Path(temp)/'worker.log',.2,.5,admission_path=Path(temp)/'admission.json',admission_timeout=.5)
        self.assertEqual(result['status'],'admission_timeout')
        self.assertEqual(result['wallSeconds'],0)
        self.assertGreater(result['admissionSeconds'],.5)
        self.assertLess(result['elapsedSeconds'],3)

    def test_waiting_mutex_contender_acquires_after_owner_releases(self):
        from common import SerialRhino
        mutex_name = 'Local\\M2DPlusHarnessTest-' + str(uuid.uuid4())
        with tempfile.TemporaryDirectory() as temp:
            marker = Path(temp)/'acquired'
            code = "import sys,pathlib; from common import SerialRhino;\nwith SerialRhino(wait_seconds=5,name=sys.argv[1]): pathlib.Path(sys.argv[2]).write_text('acquired')"
            with SerialRhino(name=mutex_name):
                child = subprocess.Popen([sys.executable,'-B','-c',code,mutex_name,str(marker)],
                                         cwd=Path(__file__).parent,creationflags=subprocess.CREATE_NO_WINDOW)
                time.sleep(.2)
                self.assertFalse(marker.exists())
            try:
                self.assertEqual(child.wait(timeout=5),0)
                self.assertEqual(marker.read_text(),'acquired')
            finally:
                if child.poll() is None:
                    child.kill(); child.wait()

    def test_timeout_terminates_only_launched_job_including_descendant(self):
        from owned_job import run
        code = "import sys,subprocess,time; assert sys.stdin.readline().strip()=='go'; subprocess.Popen([sys.executable,'-c','import time;time.sleep(30)']); time.sleep(30)"
        with tempfile.TemporaryDirectory() as temp:
            result = run([sys.executable, '-B', '-c', code], Path(temp) / 'worker.log', 1, .5)
        self.assertEqual(result['status'], 'wall_timeout')
        self.assertLess(result['wallSeconds'], 5)
        self.assertGreater(result['peakPrivateBytes'], 0)

    def test_successful_handshake_and_exit(self):
        from owned_job import run
        with tempfile.TemporaryDirectory() as temp:
            result = run([sys.executable, '-B', '-c', "import sys; assert sys.stdin.readline().strip()=='go'"], Path(temp) / 'worker.log', 10, .5)
        self.assertEqual(result['status'], 'exited')
        self.assertEqual(result['exitCode'], 0)


if __name__ == '__main__':
    unittest.main()
