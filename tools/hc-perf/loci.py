"""Reads hc-perf .loci files (BinaryWriter format)."""
import struct, sys

def _str(b, i):
    n = 0; shift = 0
    while True:
        c = b[i]; i += 1; n |= (c & 0x7f) << shift; shift += 7
        if c < 0x80: break
    return b[i:i + n].decode('utf-8'), i + n

def load(path):
    b = open(path, 'rb').read(); i = 0
    (n,) = struct.unpack_from('<i', b, i); i += 4
    sources = []
    for _ in range(n):
        s, i = _str(b, i); sources.append(s)
    (m,) = struct.unpack_from('<i', b, i); i += 4
    pieces = []
    for _ in range(m):
        cls = b[i]; i += 1
        src, ln = struct.unpack_from('<ii', b, i); i += 8
        pts = struct.unpack_from('<%dd' % ln, b, i); i += 8 * ln
        pieces.append((cls, sources[src], pts))
    return pieces

if __name__ == '__main__':
    for cls, src, pts in load(sys.argv[1]):
        print(cls, src[:8], len(pts) // 2, ['%.4f' % v for v in pts[:8]], '...', ['%.4f' % v for v in pts[-4:]])
