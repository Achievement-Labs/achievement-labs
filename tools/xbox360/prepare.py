"""Prepare the user-authorized shared CON identity from the pinned public fork build.

Never executes the downloaded EXE. Bundle layout follows dotnet/runtime's
Microsoft.NET.HostModel/Bundle/{Manifest,FileEntry}.cs. No profile is an input.
Generated binary assets are ignored by Git; no signing values are logged.
"""
import argparse, hashlib, io, pathlib, shutil, struct, subprocess, urllib.request, zlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
URL = 'https://github.com/thevampirebat/achievement-labs/releases/download/v1.0.8-fork.14/AchievementLabs.exe'
HASH = 'fee8b254273983df5c9e5087bba3bf1fcba4c928e01fca05205bf40a96b35c25'
MARKER = bytes.fromhex('8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae')

def text(stream):
    size = 0
    for shift in range(0, 35, 7):
        b = stream.read(1)[0]
        size |= (b & 127) << shift
        if b < 128:
            if size > 4096: raise ValueError('Invalid bundle name')
            return stream.read(size).decode('utf-8')
    raise ValueError('Invalid bundle string')

def extract(data):
    if hashlib.sha256(data).hexdigest() != HASH: raise ValueError('Pinned Windows build checksum mismatch')
    pos = data.find(MARKER)
    if pos < 8: raise ValueError('Bundle header missing')
    offset, = struct.unpack_from('<q', data, pos - 8)
    stream = io.BytesIO(data); stream.seek(offset)
    major, minor, count = struct.unpack('<IIi', stream.read(12))
    if major != 6 or minor != 0 or not 0 < count < 10000: raise ValueError('Unsupported bundle')
    text(stream); stream.read(40)
    found = None
    for _ in range(count):
        start, size, compressed = struct.unpack('<qqq', stream.read(24))
        stream.read(1); name = text(stream)
        if name != 'AchievementLabs.Core.dll': continue
        if not 0 < size < 256 * 1024 * 1024 or start < 0 or start + (compressed or size) > len(data): raise ValueError('Invalid core assembly range')
        blob = data[start:start + (compressed or size)]
        if compressed: blob = zlib.decompress(blob, -15)
        if len(blob) != size: raise ValueError('Core assembly size mismatch')
        candidates = []
        i = -1
        while True:
            i = blob.find(b'ALC1', i + 1)
            if i < 0: break
            candidate = blob[i:i+556]; cert = candidate[4:428]
            if len(candidate) != 556: continue
            n = int.from_bytes(b''.join(cert[j:j+8] for j in range(160, 39, -8)), 'big')
            if cert[:2] == b'\x01\xa8' and int.from_bytes(candidate[428:492], 'big') * int.from_bytes(candidate[492:556], 'big') == n:
                candidates.append(candidate)
        if len(candidates) != 1: raise ValueError('Expected exactly one valid signing resource')
        found = candidates[0]
    if found is None: raise ValueError('Signing resource missing')
    return found

def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--exe', type=pathlib.Path)
    args = parser.parse_args()
    if args.exe: data = args.exe.read_bytes()
    else:
        with urllib.request.urlopen(URL, timeout=120) as response:
            data = response.read(140 * 1024 * 1024)
    identity = extract(data)
    shared = ROOT / 'tools/xbox360/shared'
    (shared / 'signing.dat').write_bytes(identity)
    subprocess.run(['node', str(shared / 'core-checks.cjs'), '--fixture', str(shared / 'synthetic.dat')], check=True)
    print('Prepared isolated mobile Xbox 360 assets and validated shared signing resource. No profile or account data used.')

if __name__ == '__main__': main()
