"""Prepare the user-authorized shared CON identity from the pinned official native bootstrap package.

Never executes the downloaded EXE. Bundle layout follows dotnet/runtime's
Microsoft.NET.HostModel/Bundle/{Manifest,FileEntry}.cs. No profile is an input.
Generated binary assets are ignored by Git; no signing values are logged.
"""
import argparse, hashlib, io, pathlib, struct, subprocess, tarfile, urllib.request, zlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
URL = 'https://github.com/Achievement-Labs/achievement-labs/releases/download/desktop-preview-v0.1.0/AchievementLabs-linux-0.1.0.1-x64.tar.gz'
HASH = '4676b49fed17c75a650ded70801aaa51c007ab264480e53619e0f55df7c11a24'
LEGACY_EXE_HASH = 'fee8b254273983df5c9e5087bba3bf1fcba4c928e01fca05205bf40a96b35c25'
CORE_ENTRY = 'AchievementLabs-linux-x64/AchievementLabs.Core.dll'
MAX_SOURCE_BYTES = 96 * 1024 * 1024
MAX_CORE_BYTES = 256 * 1024 * 1024
IDENTITY_SHA256 = '02c83dd940b10edaedebcb091b9fd8c61db6456ede6296902aee02df1a77a5d6'
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
    if hashlib.sha256(data).hexdigest() != LEGACY_EXE_HASH: raise ValueError('Pinned legacy Windows build checksum mismatch')
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
        if not 0 < size < MAX_CORE_BYTES or start < 0 or start + (compressed or size) > len(data): raise ValueError('Invalid core assembly range')
        blob = data[start:start + (compressed or size)]
        if compressed: blob = zlib.decompress(blob, -15)
        if len(blob) != size: raise ValueError('Core assembly size mismatch')
        found = extract_identity(blob)
    if found is None: raise ValueError('Signing resource missing')
    return found

def extract_identity(blob):
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
    if hashlib.sha256(candidates[0]).hexdigest() != IDENTITY_SHA256:
        raise ValueError('Signing resource does not match the approved identity')
    return candidates[0]


def extract_archive(data):
    if len(data) > MAX_SOURCE_BYTES: raise ValueError('Bootstrap package exceeds its size limit')
    if hashlib.sha256(data).hexdigest() != HASH: raise ValueError('Official bootstrap package checksum mismatch')
    with tarfile.open(fileobj=io.BytesIO(data), mode='r:gz') as archive:
        entries = [entry for entry in archive.getmembers() if entry.name == CORE_ENTRY]
        if len(entries) != 1 or not entries[0].isfile():
            raise ValueError('Expected one regular core assembly entry')
        entry = entries[0]
        if not 0 < entry.size < MAX_CORE_BYTES: raise ValueError('Core assembly exceeds its size limit')
        with archive.extractfile(entry) as stream:
            core = stream.read(MAX_CORE_BYTES + 1)
        if len(core) != entry.size: raise ValueError('Core assembly size mismatch')
    return extract_identity(core)


def download():
    request = urllib.request.Request(URL, headers={'User-Agent': 'AchievementLabs-Native-Build/1'})
    with urllib.request.urlopen(request, timeout=120) as response:
        length = response.headers.get('Content-Length')
        if length is not None and int(length) > MAX_SOURCE_BYTES:
            raise ValueError('Bootstrap package exceeds its size limit')
        data = response.read(MAX_SOURCE_BYTES + 1)
    if len(data) > MAX_SOURCE_BYTES: raise ValueError('Bootstrap package exceeds its size limit')
    return data


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group()
    source.add_argument('--archive', type=pathlib.Path, help='Saved official native bootstrap archive; checksum is still required')
    source.add_argument('--exe', type=pathlib.Path, help='Saved legacy pinned Windows build; no legacy network download')
    args = parser.parse_args()
    identity = extract(args.exe.read_bytes()) if args.exe else extract_archive(args.archive.read_bytes() if args.archive else download())
    shared = ROOT / 'tools/xbox360/shared'
    shared.mkdir(parents=True, exist_ok=True)
    (shared / 'signing.dat').write_bytes(identity)
    subprocess.run(['node', str(shared / 'core-checks.cjs'), '--fixture', str(shared / 'synthetic.dat')], check=True)
    print('Prepared native Xbox 360 build assets from a verified pinned source. No profile or account data used.')


if __name__ == '__main__': main()
