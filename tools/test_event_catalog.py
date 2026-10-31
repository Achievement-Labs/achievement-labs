import copy
import json
import tempfile
import unittest
import zipfile
from pathlib import Path
from event_catalog import archive, dump, flatten, read_catalog, read_index, reconcile, runtime_title, write_split


class CatalogChecks(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.source = self.root / 'source'
        self.source.mkdir()
        self.block = {'Enabled': True, 'FullySupported': False, 'Notes': 'Research belongs in the backup',
                      'Achievements': {'1': {'Name': 'Research label', 'Request1_Event': {
                          'Id': {'ReplacementType': 'Replace', 'Target': 'REPLACEID', 'Replacement': 1}}}}}
        dump(self.source / 'Data.json', {'7': self.block, 'SupportedTitleIDs': ['7']})
        (self.source / '7.json').write_bytes(b'{"id":REPLACEID}')

    def tearDown(self):
        self.temporary.cleanup()

    def test_split_roundtrip_backup_and_schema(self):
        before = (self.source / 'Data.json').read_bytes()
        output = self.root / 'split'
        backup = self.root / 'backup'
        (self.source / 'historical-backup.json').write_text('not a catalog input')
        write_split(self.source, output, backup)
        self.assertEqual(before, (self.source / 'Data.json').read_bytes())
        self.assertEqual(before, (backup / 'Data.json').read_bytes())
        self.assertFalse((backup / 'historical-backup.json').exists())
        self.assertNotIn('Notes', read_index(output)['7'])
        self.assertNotIn('Achievements', read_index(output)['7'])
        title = read_catalog(output)['7']
        self.assertNotIn('Name', title['Achievements']['1'])
        self.assertEqual(title['Achievements']['1']['Request1']['Id']['Replacement'], 1)
        self.assertEqual((output / '7/7.json').read_bytes(), (self.source / '7.json').read_bytes())
        self.assertNotIn(b'\n', (output / '7/Data.json').read_bytes().replace(b'\r\n', b''))
        flat = self.root / 'flat'
        flatten(output, flat)
        self.assertEqual(read_catalog(flat), read_catalog(output))
        (output / 'private-backup.json').write_text('private')
        zipped = self.root / 'Events-Indexed.zip'
        archive(output, zipped)
        with zipfile.ZipFile(zipped) as z:
            self.assertEqual(set(z.namelist()), {'Events/index.json', 'Events/7/Data.json', 'Events/7/7.json'})

    def test_reconcile_priority_and_mixed_formats(self):
        indexed = self.root / 'indexed'
        write_split(self.source, indexed, self.root / 'first-backup')
        second = self.root / 'second'
        second.mkdir()
        block = copy.deepcopy(self.block)
        block['Achievements']['1']['Request1_Event']['Id']['Replacement'] = 2
        dump(second / 'Data.json', {'7': block, 'SupportedTitleIDs': [7]})
        (second / '7.json').write_text('{"other":REPLACEID}')
        output = self.root / 'merged'
        reconcile([indexed, second], output, self.root / 'merge-backup')
        self.assertEqual(read_catalog(output)['7']['Achievements']['1']['Request1']['Id']['Replacement'], 2)
        self.assertEqual(read_catalog(output)['SupportedTitleIDs'], ['7'])
        self.assertEqual((output / '7/7.json').read_text(), '{"other":REPLACEID}')

    def test_reject_unsupported_operations_and_overlapping_paths(self):
        block = copy.deepcopy(self.block)
        block['Achievements']['1']['Request1_Event']['Id']['ReplacementType'] = 'RawBody'
        with self.assertRaises(ValueError):
            runtime_title(block)
        with self.assertRaises(ValueError):
            write_split(self.source, self.root / 'split', self.source / 'backup')
        self.assertFalse((self.root / 'split').exists())


if __name__ == '__main__':
    unittest.main()
