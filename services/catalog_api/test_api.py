import gzip
import hashlib
import io
import json
from pathlib import Path
import sqlite3
import tempfile
import threading
import unittest
from urllib.error import HTTPError
from urllib.request import Request, urlopen
from wsgiref.simple_server import make_server, WSGIRequestHandler

from .app import PREFIX, create_app, database
from .build import build, read


class CatalogApiTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.repo = self.root / 'repo'
        (self.repo / 'Events').mkdir(parents=True)
        (self.repo / 'site/assets').mkdir(parents=True)
        (self.repo / 'catalog').mkdir()
        self.blocks = {
            '12': {'TitleName': 'Master Chief', 'Devices': ['XboxOne', 'XboxSeries'], 'FullySupported': True},
            '13': {'TitleName': 'Shared name', 'Devices': ['PC'], 'PartiallySupported': True},
            '14': {'TitleName': 'Shared name', 'Devices': ['PC'], 'FullySupported': False},
            '15': {'TitleName': 'Disabled', 'Devices': ['PC'], 'FullySupported': True, 'Enabled': False},
            '16': {'TitleName': 'Unlisted', 'Devices': ['PC'], 'FullySupported': True},
        }
        for key, block in self.blocks.items():
            block['Achievements'] = {} if key == '14' else {'1': {'Id': {'ReplacementType': 'Replace', 'Target': 'PRIVATE_TEMPLATE_TOKEN', 'Replacement': 'PRIVATE_RECIPE_VALUE'}}}
        self.listed = ['12', '13', '14', '15']
        self.write_events()
        self.write('site/assets/games.json', [{'titleId': k, 'name': 'Halo 100% Café' if k == '12' else b['TitleName'],
                   'platform': 'Windows PC', 'status': 'unsupported'} for k, b in self.blocks.items()])
        self.definitions = {'schemaVersion': 1, 'entries': [
            {'titleId': '12', 'total': 700, 'platforms': ['XboxOne', 'XboxSeries'], 'checkedAt': '2026-01-01T00:00:00Z'},
            {'titleId': '13', 'total': 30, 'platforms': ['XboxOne'], 'checkedAt': '2026-01-01T00:00:00Z'},
            {'titleId': '99', 'total': 99, 'platforms': ['PC'], 'checkedAt': '2026-01-01T00:00:00Z'},
        ]}
        self.write('catalog/achievement-totals.json', self.definitions)
        self.path = self.root / 'catalog.sqlite'
        self.metadata = build(self.repo, self.path, 'a' * 40)
        self.app = create_app(self.path)

    def tearDown(self):
        self.temporary.cleanup()

    def write(self, relative, value):
        (self.repo / relative).write_text(json.dumps(value, ensure_ascii=False), encoding='utf-8')

    def write_events(self):
        index = {}
        for key, block in self.blocks.items():
            folder = self.repo / 'Events' / key
            folder.mkdir(exist_ok=True)
            self.write('Events/' + key + '/Data.json', {key: block, 'SupportedTitleIDs': [key]})
            if key != '15':
                (folder / (key + '.json')).write_text('PRIVATE_TEMPLATE_CONTENT', encoding='utf-8')
            index[key] = {k: v for k, v in block.items() if k != 'Achievements'}
        index['SupportedTitleIDs'] = self.listed
        self.write('Events/index.json', index)

    def call(self, path='', query='', method='GET', **extra):
        received = {}
        def start(status, headers):
            received.update(status=int(status.split()[0]), headers=dict(headers))
        payload = b''.join(self.app({'REQUEST_METHOD': method, 'PATH_INFO': PREFIX + path,
            'QUERY_STRING': query, 'wsgi.input': io.BytesIO(), **extra}, start))
        received['raw'] = payload
        if received['headers'].get('Content-Encoding') == 'gzip':
            payload = gzip.decompress(payload)
        received['body'] = json.loads(payload) if payload else None
        return received

    def test_distinct_statuses_ignore_website_labels_and_mapping_coverage(self):
        expected = {'12': 'Supported', '13': 'PartiallySupported', '14': 'Testing', '15': 'Unsupported', '16': 'Unsupported'}
        for key, status in expected.items():
            record = self.call('/titles/' + key)['body']
            self.assertEqual(record['status'], status)
        supported = self.call('/titles/12')['body']
        self.assertEqual(supported['definitionAchievementCount'], 700)
        self.assertEqual(supported['mappedAchievementCount'], 1)
        self.assertEqual(supported['aliases'], ['Master Chief'])
        self.assertTrue(supported['eventAvailable'])
        testing = self.call('/titles/14')['body']
        self.assertEqual(testing['availabilityReason'], 'no-mappings')
        self.assertFalse(testing['eventAvailable'])
        self.assertEqual(self.call('/titles/16')['body']['availabilityReason'], 'unlisted')

    def test_literal_name_id_alias_unicode_and_filtered_search(self):
        for query in ('query=halo', 'query=100%25', 'query=CAF%C3%89', 'query=Master', 'query=12'):
            self.assertEqual([r['titleId'] for r in self.call('/titles', query)['body']['items']], ['12'])
        result = self.call('/titles', 'platform=pc&status=partiallySupported')['body']
        self.assertEqual([r['titleId'] for r in result['items']], ['13'])
        self.assertEqual(self.call('/titles', "query=%27%20OR%201%3D1--")['body']['items'], [])

    def test_pagination_has_stable_ties_no_duplicates_and_filter_binding(self):
        cursor, found = None, []
        while True:
            result = self.call('/titles', 'limit=1' + ('&cursor=' + cursor if cursor else ''))['body']
            found.extend(r['titleId'] for r in result['items'])
            cursor = result['nextCursor']
            if not cursor:
                break
            self.assertEqual(self.call('/titles', 'limit=2&cursor=' + cursor)['status'], 400)
        self.assertEqual(len(found), 5)
        self.assertEqual(len(set(found)), 5)
        self.assertLess(found.index('13'), found.index('14'))

    def test_cursor_rejects_changed_catalog(self):
        cursor = self.call('/titles', 'limit=1')['body']['nextCursor']
        self.blocks['12']['FullySupported'] = False
        self.write_events()
        build(self.repo, self.path, 'b' * 40)
        result = self.call('/titles', 'limit=1&cursor=' + cursor)
        self.assertEqual(result['status'], 409)
        self.assertEqual(result['body']['error']['code'], 'catalog_changed')

    def test_totals_independent_of_event_membership_and_platform_specific(self):
        result = self.call('/totals', 'ids=99,12,99,17')['body']
        self.assertEqual([r['titleId'] for r in result['items']], ['99', '12'])
        self.assertEqual(result['missingIds'], ['17'])
        self.assertEqual(self.call('/titles/99')['status'], 404)
        self.assertIsNone(self.call('/titles/13')['body']['definitionAchievementCount'])
        filtered = self.call('/totals', 'ids=13,99&platform=pc')['body']
        self.assertEqual(filtered['missingIds'], ['13'])
        self.assertEqual(filtered['items'][0]['total'], 99)

    def test_unknown_and_retired_ids_are_not_invented_support_claims(self):
        response = self.call('/titles/100')
        self.assertEqual(response['status'], 404)
        self.assertEqual(response['body']['error']['code'], 'title_not_found')
        self.assertIn('not a support determination', response['body']['error']['message'])

    def test_invalid_inputs_and_read_only_methods(self):
        for query in ('limit=0', 'limit=101', 'limit=-1', 'limit=1&limit=2', 'status=bogus',
                      'cursor=broken', 'query=' + 'x' * 201, 'unknown=1', 'query', 'query=%FF'):
            self.assertEqual(self.call('/titles', query)['status'], 400, query)
        for query in ('', 'ids=', 'ids=12,', 'ids=01', 'ids=4294967296', 'ids=' + ','.join(['12'] * 101)):
            self.assertEqual(self.call('/totals', query)['status'], 400, query)
        for key in ('01', '0', '-1', '4294967296', '%31%32', '１２'):
            self.assertEqual(self.call('/titles/' + key)['status'], 400, key)
        for method in ('POST', 'PUT', 'DELETE'):
            self.assertEqual(self.call('/titles', method=method)['status'], 405)

    def test_etag_head_gzip_and_error_cache_control(self):
        normal = self.call('/titles')
        etag = normal['headers']['ETag']
        self.assertEqual(self.call('/titles', HTTP_IF_NONE_MATCH='W/' + etag)['status'], 304)
        self.assertEqual(self.call('/titles', HTTP_IF_NONE_MATCH='*')['raw'], b'')
        head = self.call('/titles', method='HEAD')
        self.assertEqual(head['headers']['Content-Length'], str(len(normal['raw'])))
        self.assertEqual(head['raw'], b'')
        zipped = self.call('/titles', HTTP_ACCEPT_ENCODING='gzip')
        self.assertEqual(zipped['headers']['Content-Encoding'], 'gzip')
        self.assertEqual(zipped['body'], normal['body'])
        self.assertNotEqual(zipped['headers']['ETag'], etag)
        self.assertNotIn('Content-Encoding', self.call('/titles', HTTP_ACCEPT_ENCODING='gzip;q=0')['headers'])
        self.assertEqual(self.call('/titles/100')['headers']['Cache-Control'], 'no-store')

    def test_cross_origin_conditional_reads_have_a_read_only_preflight(self):
        response = self.call('/titles', method='OPTIONS', HTTP_ACCESS_CONTROL_REQUEST_METHOD='GET')
        self.assertEqual(response['status'], 204)
        self.assertEqual(response['headers']['Access-Control-Allow-Headers'], 'If-None-Match')
        self.assertEqual(response['raw'], b'')
        self.assertEqual(self.call('/titles', method='OPTIONS', HTTP_ACCESS_CONTROL_REQUEST_METHOD='POST')['status'], 405)

    def test_revision_and_public_only_projection(self):
        original = {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in self.repo.rglob('*') if p.is_file()}
        again = build(self.repo, self.root / 'second.sqlite', 'a' * 40)
        self.assertEqual(again, self.metadata)
        after = {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in self.repo.rglob('*') if p.is_file()}
        self.assertEqual(original, after)
        response = self.call('/titles', HTTP_AUTHORIZATION='Bearer PRIVATE_ACCOUNT_TOKEN')
        for token in (b'PRIVATE_ACCOUNT_TOKEN', b'PRIVATE_TEMPLATE', b'PRIVATE_RECIPE', b'ReplacementType', b'Achievements'):
            self.assertNotIn(token, response['raw'])
            self.assertNotIn(token, self.path.read_bytes())
        with database(self.path) as db:
            with self.assertRaises(sqlite3.OperationalError):
                db.execute('DELETE FROM titles')

    def test_invalid_import_preserves_working_snapshot(self):
        original = self.path.read_bytes()
        self.blocks['12']['ResearchStatus'] = 'should never be published'
        self.write_events()
        with self.assertRaises(ValueError):
            build(self.repo, self.path, 'b' * 40)
        self.assertEqual(self.path.read_bytes(), original)
        self.assertEqual(self.call('/titles/12')['body']['status'], 'Supported')

    def test_missing_active_template_does_not_downgrade_or_replace_snapshot(self):
        original = self.path.read_bytes()
        (self.repo / 'Events/12/12.json').unlink()
        with self.assertRaises(ValueError):
            build(self.repo, self.path, 'b' * 40)
        self.assertEqual(original, self.path.read_bytes())

    def test_strict_source_json_and_totals(self):
        self.definitions['entries'][0]['total'] = True
        self.write('catalog/achievement-totals.json', self.definitions)
        with self.assertRaises(ValueError):
            build(self.repo, self.path, 'b' * 40)
        duplicate = self.root / 'duplicate.json'
        duplicate.write_text('{"a":1,"a":2}')
        with self.assertRaises(ValueError):
            read(duplicate)

    def test_missing_database_returns_safe_error_without_creating_file(self):
        missing = self.root / 'missing.sqlite'
        self.app = create_app(missing)
        result = self.call('/titles')
        self.assertEqual(result['status'], 503)
        self.assertFalse(missing.exists())
        self.assertNotIn(str(self.root), result['raw'].decode())

    def test_builder_refuses_source_directory_output(self):
        with self.assertRaises(ValueError):
            build(self.repo, self.repo / 'Events/catalog.sqlite', 'a' * 40)
        self.assertFalse((self.repo / 'Events/catalog.sqlite').exists())

    def test_scalar_achievement_fields_cannot_inflate_mapping_count(self):
        before = self.path.read_bytes()
        self.blocks['12']['Achievements']['1']['Enabled'] = True
        self.write_events()
        with self.assertRaises(ValueError):
            build(self.repo, self.path, 'a' * 40)
        self.assertEqual(before, self.path.read_bytes())

    def test_mapping_operation_order_changes_revision_without_changing_count(self):
        operations = self.blocks['12']['Achievements']['1']
        operations['Second'] = {'ReplacementType': 'Replace', 'Target': 'NEXT', 'Replacement': 'LAST'}
        self.write_events()
        first = build(self.repo, self.path, 'a' * 40)
        before = self.call('/titles/12')['body']
        self.blocks['12']['Achievements']['1'] = dict(reversed(list(operations.items())))
        self.write_events()
        second = build(self.repo, self.path, 'a' * 40)
        after = self.call('/titles/12')['body']
        self.assertEqual(before['mappedAchievementCount'], after['mappedAchievementCount'])
        self.assertNotEqual(before['mappingRevision'], after['mappingRevision'])
        self.assertNotEqual(first['catalogRevision'], second['catalogRevision'])

    def test_real_http_get_head_and_conditional_request(self):
        class Quiet(WSGIRequestHandler):
            def log_message(self, *args):
                pass
        server = make_server('127.0.0.1', 0, self.app, handler_class=Quiet)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            url = 'http://127.0.0.1:' + str(server.server_port) + PREFIX + '/titles/12'
            with urlopen(url, timeout=5) as response:
                self.assertEqual(json.load(response)['status'], 'Supported')
                etag = response.headers['ETag']
            with urlopen(Request(url, method='HEAD'), timeout=5) as response:
                self.assertEqual(response.read(), b'')
            with self.assertRaises(HTTPError) as failure:
                urlopen(Request(url, headers={'If-None-Match': etag}), timeout=5)
            self.assertEqual(failure.exception.code, 304)
        finally:
            server.shutdown(); server.server_close(); thread.join(timeout=5)


if __name__ == '__main__':
    unittest.main()
