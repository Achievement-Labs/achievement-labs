"""Offline edition-routing regression checks; never submits any event requests."""
import contextlib
import copy
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import prepare_catalogue as catalogue


class EditionCatalogueTests(unittest.TestCase):
    def setUp(self):
        self.block = json.loads((catalogue.SOURCE / 'Data.json').read_text(encoding='utf-8-sig'))['2154930']
        self.template = (catalogue.SOURCE / '2154930.json').read_text(encoding='utf-8-sig')

    def generate(self, block=None, template=None):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source = root / 'src/AchievementLabs.App/Events'
            output = root / 'mobile/Catalogue'
            source.mkdir(parents=True)
            (root / 'Events').mkdir()
            metadata = root / 'src/AchievementLabs.Enhancements/EditionMappings.json'
            metadata.parent.mkdir(parents=True)
            metadata.write_text('[]')
            (root / 'catalog').mkdir()
            for name in ['achievement-packs.json', 'achievement-hubs.json', 'achievement-totals.json']:
                (root / 'catalog' / name).write_text('{}')
            data = json.dumps({'2154930':block or self.block, 'SupportedTitleIDs':['2154930']})
            for path in [source / 'Data.json', root / 'Events/Data.json']:
                path.write_text(data)
            (source / '2154930.json').write_text(template or self.template, encoding='utf-8')
            with patch.multiple(catalogue, ROOT=root, SOURCE=source, OUTPUT=output), contextlib.redirect_stdout(io.StringIO()):
                catalogue.prepare()
            status = json.loads((output / 'MobileIndex.json').read_text())['2154930']
            mapping = output / 'Events/2154930.mapping.json'
            return status, json.loads(mapping.read_text())['recipes'] if mapping.exists() else {}

    def test_legacy_and_corrected_catalogues_use_same_xbox_ids(self):
        status, legacy = self.generate()
        self.assertIsNone(status['reason'])
        self.assertEqual(len(legacy), 50)
        expected = catalogue.EDITIONS['2154930']['Entries']
        self.assertEqual(set(legacy), {entry['XboxId'] for entry in expected})
        self.assertNotIn('43', legacy)
        corrected = copy.deepcopy(self.block)
        corrected['Achievements'] = {entry['XboxId']:self.block['Achievements'][entry['CatalogId']] for entry in expected}
        self.assertEqual(self.generate(corrected)[1], legacy)
        for entry in expected:
            self.assertEqual(legacy[entry['XboxId']][0][0]['value'], entry['XboxId'])

    def test_unknown_or_missing_catalogue_id_disables_whole_title(self):
        for mutation in ['extra', 'missing']:
            block = copy.deepcopy(self.block)
            if mutation == 'extra': block['Achievements']['999'] = copy.deepcopy(block['Achievements']['4'])
            else: del block['Achievements']['4']
            status, recipes = self.generate(block)
            self.assertIsNotNone(status['reason'])
            self.assertFalse(recipes)

    def test_changed_events_properties_service_or_title_are_rejected(self):
        for before, after in [('AchievementUnlocked','OtherEvent'), ('"ID": "REPLACEID"','"DifferentProperty": "REPLACEID"'), ('d8890100-b864-45cd-b20e-62d40020e1b2','00000000-0000-0000-0000-000000000001'), ('"2154930"','"649881449"')]:
            status, recipes = self.generate(template=self.template.replace(before, after))
            self.assertIsNotNone(status['reason'])
            self.assertFalse(recipes)

    def test_changed_mapping_and_duplicate_requests_are_rejected(self):
        for duplicate in [False, True]:
            block = copy.deepcopy(self.block)
            if duplicate:
                request = block['Achievements']['4']
                block['Achievements']['4'] = {'First':request, 'Second':copy.deepcopy(request)}
            else: block['Achievements']['4']['Replacement']['Replacement'] = 999
            status, recipes = self.generate(block)
            self.assertIsNotNone(status['reason'])
            self.assertFalse(recipes)

    def test_author_disabled_title_stays_disabled(self):
        block = copy.deepcopy(self.block)
        block['Enabled'] = False
        status, recipes = self.generate(block)
        self.assertIn('disabled', status['reason'])
        self.assertFalse(recipes)


if __name__ == '__main__': unittest.main()
