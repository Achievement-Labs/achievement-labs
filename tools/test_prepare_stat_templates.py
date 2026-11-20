import copy
import json
import tempfile
import unittest
from pathlib import Path

from prepare_stat_templates import encode, prepare


class StatTemplatePreparationTests(unittest.TestCase):
    def test_stat_only_title_is_not_an_achievement_catalog_entry_and_reruns_preserve_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            index = b'{"SupportedTitleIDs":[]}'
            (root / 'index.json').write_bytes(index)
            (root / 'Stats.json').write_bytes(b'{}')
            row = {'TitleId': '7', 'Name': 'Example', 'ServiceConfigId': '11111111-2222-3333-4444-555555555555',
                   'PackageFamilyName': 'Publisher.Example_abc', 'AchievementsType': 'Title Based',
                   'StatsType': 'Event Based', 'StatsTypeSource': 'list'}
            writes, report = prepare(root, [row], {'7': row})
            self.assertNotIn(root / '7' / 'Data.json', writes)
            block = json.loads(writes[root / '7' / 'Stats.json'])
            self.assertEqual(list(block), ['7', 'SupportedTitleIDs'])
            self.assertEqual(block['7']['Stats'], {})
            self.assertEqual(report[0]['ExistingEventRecipes'], 0)
            for path, content in writes.items():
                path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(content)
            self.assertEqual(prepare(root, [row], {'7': row})[0], {})
            self.assertEqual((root / 'index.json').read_bytes(), index)
            authored = b'authored template must survive'
            (root / '7' / '7.json').write_bytes(authored)
            self.assertEqual(prepare(root, [row], {'7': row})[0], {})
            self.assertEqual((root / '7' / '7.json').read_bytes(), authored)

    def test_inline_template_and_recipes_are_preserved_in_named_layout(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); title = root / '9 - Existing'; title.mkdir()
            block = {'TitleName': 'Existing', 'Stats': {'Count': {'Request1': {'Data': {'ReplacementType': 'Replace',
                     'Target': 'REPLACEDATA', 'Replacement': 'authored'}}}}, 'StatTemplate': 'existing envelope'}
            original = copy.deepcopy(block)
            (title / 'Stats.json').write_bytes(encode({'9': block, 'SupportedTitleIDs': ['9']}))
            writes, _ = prepare(root, [], {'9': {'TitleId': '9', 'Name': 'Existing'}}, named=True)
            self.assertEqual(writes, {title / '9.json': b'existing envelope'})
            self.assertEqual(json.loads((title / 'Stats.json').read_bytes())['9'], original)

    def test_no_identity_does_not_generate_a_donor_template(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); title = root / '9'; title.mkdir()
            (title / 'Stats.json').write_bytes(encode({'9': {'Stats': {}}, 'SupportedTitleIDs': ['9']}))
            writes, report = prepare(root, [], {'9': {'TitleId': '9', 'Name': 'Unknown'}})
            self.assertEqual(writes, {})
            self.assertEqual(report[0]['TemplateSource'], 'missing-package-identity-no-template-generated')


if __name__ == '__main__':
    unittest.main()
