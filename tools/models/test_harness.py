"""Fast, unlicensed local tests: archive safety and owned-process isolation."""
import os
from pathlib import Path
import struct
import sys
import tempfile
import unittest
import uuid
import zlib

from archive import canonicalize


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
