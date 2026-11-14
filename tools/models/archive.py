"""Canonicalize native-assigned UUIDs, preserving archive CRCs and byte lengths.

Only generator-owned v8 archives are accepted. Chunk layout/CRC flags are from
McNeel's openNURBS opennurbs_3dm.h (8.x). UUID substitutions must be inside a
CRC32-verified chunk; otherwise this fails closed. No geometry values change.
"""
from bisect import bisect_left
import mmap
import re
import struct
import uuid
import zlib


def canonicalize(path, ids):
    if any(uuid.UUID(str(a)).int == 0 or uuid.UUID(str(b)).int == 0 for a, b in ids.items()):
        raise ValueError('Nil UUID is never a valid canonicalization target')
    pairs = {uuid.UUID(str(a)).bytes_le: uuid.UUID(str(b)).bytes_le for a, b in ids.items() if str(a) != str(b)}
    if not pairs:
        return
    with open(path, 'r+b') as file, mmap.mmap(file.fileno(), 0) as data:
        if not data[:32].startswith(b'3D Geometry File Format') or data[:32].rstrip(b'\x00 ').split()[-1] != b'80':
            raise ValueError('Canonical UUID rewrite requires a v8 3dm archive')
        locations = [(m.start(), pairs[m.group()]) for m in re.finditer(b'|'.join(re.escape(k) for k in pairs), data)]
        starts = [p for p, _ in locations]
        candidates = []
        spans = []
        covered = set()
        # CRC-bearing table records, anonymous/user chunks and openNURBS class data.
        pattern = rb'[\x00-\xff][\x80-\x8f]\x00[\x02\x20\x40]|\xfc\xff\x02\x00'
        for hit in re.finditer(pattern, data):
            header = hit.start()
            if header + 12 > len(data):
                continue
            size = struct.unpack_from('<Q', data, header + 4)[0]
            lo, hi = header + 12, header + 12 + size - 4
            if size < 4 or hi + 4 > len(data):
                continue
            candidates.append((header, lo, hi))
        candidate_starts = sorted(h for h, _, _ in candidates)
        verified = {}
        for header, lo, hi in sorted(candidates, key=lambda s: s[2] - s[0]):
            stored = struct.unpack_from('<I', data, hi)[0]
            ranges = [(lo, hi)]
            if zlib.crc32(data[lo:hi]) != stored:
                # openNURBS parent CRCs exclude complete nested chunks (headers too).
                child_headers = candidate_starts[bisect_left(candidate_starts, lo):bisect_left(candidate_starts, hi)]
                children = [(a, verified[a][0] + 4) for a in child_headers if a in verified and verified[a][0] + 4 <= hi]
                ranges = []; pos = lo
                for a, b in children:
                    if a < pos:
                        continue
                    if a > pos:
                        ranges.append((pos, a))
                    pos = b
                if pos < hi:
                    ranges.append((pos, hi))
                crc = 0
                for a, b in ranges:
                    crc = zlib.crc32(data[a:b], crc)
                if crc != stored:
                    continue
            verified[header] = (hi, ranges)
            touched = []
            for a, b in ranges:
                first, last = bisect_left(starts, a), bisect_left(starts, b)
                touched += [i for i in range(first, last) if starts[i] + 16 <= b]
            if not touched:
                continue
            spans.append((hi, ranges))
            covered.update(touched)
        if len(covered) != len(locations):
            missing = [p for i, (p, _) in enumerate(locations) if i not in covered]
            raise ValueError('UUID outside a verified CRC chunk: ' + str(missing[:10]))
        for pos, replacement in locations:
            data[pos:pos + 16] = replacement
        # Nested CRC chunks are repaired before any encompassing CRC payload.
        for hi, ranges in spans:
            crc = 0
            for lo, end in ranges:
                crc = zlib.crc32(data[lo:end], crc)
            struct.pack_into('<I', data, hi, crc)
        data.flush()
