import hashlib
import io
import tarfile
import unittest
from unittest.mock import patch
import prepare


def synthetic_identity():
    # Fictional formatting fixture, not a shipped signing identity or a profile.
    record = bytearray(556)
    record[:4] = b'ALC1'
    record[4:6] = b'\x01\xa8'
    record[4+40:4+48] = (15).to_bytes(8, 'big')
    record[428:492] = (3).to_bytes(64, 'big')
    record[492:556] = (5).to_bytes(64, 'big')
    return bytes(record)


def archive(core=None, *, duplicate=False, symlink=False, name=None):
    core = b'assembly-prefix' + synthetic_identity() + b'assembly-suffix' if core is None else core
    output = io.BytesIO()
    with tarfile.open(fileobj=output, mode='w:gz') as bundle:
        for _ in range(2 if duplicate else 1):
            entry = tarfile.TarInfo(name or prepare.CORE_ENTRY)
            if symlink:
                entry.type = tarfile.SYMTYPE
                entry.linkname = '/outside/assembly.dll'
                bundle.addfile(entry)
            else:
                entry.size = len(core)
                bundle.addfile(entry, io.BytesIO(core))
    return output.getvalue()


class PrepareChecks(unittest.TestCase):
    def extract(self, data):
        with patch.object(prepare, 'HASH', hashlib.sha256(data).hexdigest()), patch.object(prepare, 'IDENTITY_SHA256', hashlib.sha256(synthetic_identity()).hexdigest()):
            return prepare.extract_archive(data)

    def test_valid_pinned_archive(self):
        self.assertEqual(self.extract(archive()), synthetic_identity())

    def test_reject_changed_download(self):
        data = archive()
        with patch.object(prepare, 'HASH', hashlib.sha256(data).hexdigest()):
            with self.assertRaisesRegex(ValueError, 'checksum'):
                prepare.extract_archive(data[:-1] + bytes([data[-1] ^ 1]))

    def test_reject_ambiguous_or_unsafe_core_entry(self):
        for data in [archive(duplicate=True), archive(symlink=True), archive(name='../AchievementLabs.Core.dll')]:
            with self.assertRaisesRegex(ValueError, 'regular core'):
                self.extract(data)

    def test_reject_missing_multiple_and_wrong_identity(self):
        for core in [b'no resource', synthetic_identity() * 2]:
            with self.assertRaisesRegex(ValueError, 'exactly one'):
                self.extract(archive(core))
        data = archive()
        with patch.object(prepare, 'HASH', hashlib.sha256(data).hexdigest()), patch.object(prepare, 'IDENTITY_SHA256', '0' * 64):
            with self.assertRaisesRegex(ValueError, 'approved identity'):
                prepare.extract_archive(data)

    def test_size_bounds(self):
        with patch.object(prepare, 'MAX_SOURCE_BYTES', 4):
            with self.assertRaisesRegex(ValueError, 'size limit'):
                prepare.extract_archive(b'12345')
        with patch.object(prepare, 'MAX_CORE_BYTES', 4):
            with self.assertRaisesRegex(ValueError, 'size limit'):
                self.extract(archive())

    def test_download_bound_and_canonical_source(self):
        class Response(io.BytesIO):
            headers = {}
        with patch.object(prepare, 'MAX_SOURCE_BYTES', 4), patch.object(prepare.urllib.request, 'urlopen', return_value=Response(b'12345')) as opened:
            with self.assertRaisesRegex(ValueError, 'size limit'):
                prepare.download()
            self.assertTrue(opened.call_args.args[0].full_url.startswith('https://github.com/Achievement-Labs/achievement-labs/releases/download/'))
        self.assertEqual(prepare.MAX_SOURCE_BYTES, 96 * 1024 * 1024)


if __name__ == '__main__':
    unittest.main()
