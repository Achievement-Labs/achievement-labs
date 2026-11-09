"""WSGI read-only metadata API. Run behind the existing HTTPS reverse proxy."""
import argparse
import base64
from contextlib import contextmanager
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import sqlite3
from urllib.parse import parse_qsl

from .build import canonical, digest, normalized, title_id

PREFIX = '/api/v2/catalog'
STATUSES = ('Supported', 'PartiallySupported', 'Testing', 'Unsupported')


class ApiError(Exception):
    def __init__(self, status, code, message):
        self.status, self.code, self.message = status, code, message


def bad(message):
    raise ApiError(400, 'invalid_request', message)


def parameters(query, allowed):
    if len(query) > 4096:
        bad('Query exceeds 4096 characters.')
    try:
        entries = parse_qsl(query, keep_blank_values=True, strict_parsing=True, max_num_fields=12, errors='strict')
    except (ValueError, UnicodeError):
        bad('Malformed query.')
    result = {}
    for key, value in entries:
        if key not in allowed or key in result:
            bad('Unknown or repeated query parameter.')
        result[key] = value
    return result


def parse_id(value):
    try:
        return title_id(value)
    except ValueError:
        bad('Invalid title ID.')


def encode_cursor(revision, filters, row):
    return base64.urlsafe_b64encode(canonical({'revision': revision, 'filters': filters,
        'last': [row['sort_name'], row['title_id']]}).encode()).decode().rstrip('=')


def decode_cursor(value, revision, filters):
    try:
        if not re.fullmatch(r'[A-Za-z0-9_-]{1,2048}', value):
            raise ValueError()
        document = json.loads(base64.b64decode(value + '=' * (-len(value) % 4), altchars=b'-_', validate=True))
        if not isinstance(document, dict) or set(document) != {'revision', 'filters', 'last'}:
            raise ValueError()
        last = document['last']
        if not isinstance(last, list) or len(last) != 2 or not isinstance(last[0], str) or len(last[0]) > 2048:
            raise ValueError()
        parse_id(last[1])
        if document['filters'] != filters:
            raise ValueError()
        if document['revision'] != revision:
            raise ApiError(409, 'catalog_changed', 'Catalog changed; restart pagination without a cursor.')
        return last
    except ApiError:
        raise
    except (ValueError, TypeError, KeyError, UnicodeError):
        bad('Invalid cursor or mismatched filters.')


@contextmanager
def database(path):
    connection = sqlite3.connect(Path(path).resolve().as_uri() + '?mode=ro', uri=True, timeout=5)
    connection.row_factory = sqlite3.Row
    try:
        connection.execute('PRAGMA query_only=ON')
        connection.execute('BEGIN')  # All fields in one response use the same snapshot.
        yield connection
    finally:
        connection.close()


def route(db, path, query):
    metadata = json.loads(db.execute('SELECT document FROM metadata').fetchone()[0])
    if metadata['schemaVersion'] != 1:
        raise ApiError(503, 'catalog_unavailable', 'Catalog schema is unavailable.')
    revision = metadata['catalogRevision']
    envelope = {'catalogRevision': revision, 'sourceRevision': metadata['sourceRevision']}
    if path == PREFIX:
        parameters(query, set())
        counts = dict(db.execute('SELECT status,count(*) FROM titles GROUP BY status').fetchall())
        return {**metadata, 'statusCounts': {s: counts.get(s, 0) for s in STATUSES},
                'platforms': sorted({p for row in db.execute('SELECT document FROM titles') for p in json.loads(row[0])['platforms']})}

    if path == PREFIX + '/titles':
        args = parameters(query, {'query', 'platform', 'status', 'limit', 'cursor'})
        search, platform = args.get('query', '').strip(), args.get('platform', '').strip()
        if len(search) > 200 or len(platform) > 64:
            bad('Search or platform is too long.')
        limit_text = args.get('limit', '50')
        if not re.fullmatch(r'[1-9][0-9]{0,2}', limit_text) or not 1 <= int(limit_text) <= 100:
            bad('Limit must be between 1 and 100.')
        limit = int(limit_text)
        status = None
        if 'status' in args:
            status = next((s for s in STATUSES if s.casefold() == args['status'].casefold()), None)
            if status is None:
                bad('Unknown support status.')
        clauses, values = [], []
        if search:
            clauses.append('instr(t.search_text,?)>0'); values.append(normalized(search))
        if status:
            clauses.append('t.status=?'); values.append(status)
        if platform:
            clauses.append('EXISTS(SELECT 1 FROM title_platforms p WHERE p.title_id=t.title_id AND p.platform=?)')
            values.append(normalized(platform))
        where = ' AND '.join(clauses) or '1'
        count = db.execute('SELECT count(*) FROM titles t WHERE ' + where, values).fetchone()[0]
        filters = digest([normalized(search), normalized(platform), status, limit])
        if 'cursor' in args:
            name, key = decode_cursor(args['cursor'], revision, filters)
            where += ' AND (t.sort_name>? OR (t.sort_name=? AND t.title_id>?))'
            values.extend([name, name, key])
        rows = db.execute('SELECT t.* FROM titles t WHERE ' + where + ' ORDER BY t.sort_name,t.title_id LIMIT ?', [*values, limit + 1]).fetchall()
        page = rows[:limit]
        return {**envelope, 'totalMatched': count, 'items': [json.loads(r['document']) for r in page],
                'nextCursor': encode_cursor(revision, filters, page[-1]) if len(rows) > limit else None}

    if path == PREFIX + '/totals':
        args = parameters(query, {'ids', 'platform'})
        raw_ids = args.get('ids', '').split(',')
        if not 1 <= len(raw_ids) <= 100 or any(not value for value in raw_ids):
            bad('Provide 1 to 100 comma-separated IDs.')
        keys = list(dict.fromkeys(parse_id(value) for value in raw_ids))
        platform = args.get('platform', '').strip()
        if len(platform) > 64:
            bad('Platform is too long.')
        # Placeholder count comes only from the bounded validated ID list.
        rows = db.execute('SELECT title_id,document FROM totals WHERE title_id IN (' + ','.join('?' for _ in keys) + ')', keys).fetchall()
        found = {r['title_id']: json.loads(r['document']) for r in rows}
        if platform:
            found = {key: value for key, value in found.items() if normalized(platform) in map(normalized, value['platforms'])}
        return {**envelope, 'items': [found[key] for key in keys if key in found],
                'missingIds': [key for key in keys if key not in found]}

    match = re.fullmatch(re.escape(PREFIX) + r'/titles/([^/]+)', path)
    if match:
        parameters(query, set())
        key = parse_id(match[1])
        row = db.execute('SELECT document FROM titles WHERE title_id=?', (key,)).fetchone()
        if row is None:
            raise ApiError(404, 'title_not_found', 'Title is absent from this event catalog; absence is not a support determination for other unlock methods.')
        return {**envelope, **json.loads(row[0])}
    raise ApiError(404, 'not_found', 'Endpoint not found.')


def accepts_gzip(value):
    for entry in value.lower().split(','):
        parts = entry.strip().split(';')
        if parts[0] == 'gzip':
            try:
                return all(not p.strip().startswith('q=') or float(p.strip()[2:]) > 0 for p in parts[1:])
            except ValueError:
                return False
    return False


def create_app(path=None):
    path = Path(path or os.environ.get('CATALOG_DATABASE', 'artifacts/catalog-api/catalog.sqlite'))

    def application(environ, start_response):
        method = environ.get('REQUEST_METHOD', 'GET')
        headers = [('Content-Type', 'application/json; charset=utf-8'), ('X-Content-Type-Options', 'nosniff'),
                   ('Access-Control-Allow-Origin', '*'), ('Access-Control-Expose-Headers', 'ETag'),
                   ('Vary', 'Accept-Encoding')]
        status, cache = 200, True
        try:
            if method == 'OPTIONS':
                request_path = environ.get('PATH_INFO', '')
                if request_path != PREFIX and not request_path.startswith(PREFIX + '/'):
                    raise ApiError(404, 'not_found', 'Endpoint not found.')
                if environ.get('HTTP_ACCESS_CONTROL_REQUEST_METHOD', 'GET') not in ('GET', 'HEAD'):
                    raise ApiError(405, 'method_not_allowed', 'This API is read-only.')
                headers.extend([('Allow', 'GET, HEAD, OPTIONS'), ('Access-Control-Allow-Methods', 'GET, HEAD'),
                    ('Access-Control-Allow-Headers', 'If-None-Match'), ('Access-Control-Max-Age', '600'),
                    ('Cache-Control', 'public, max-age=600'), ('Content-Length', '0')])
                start_response('204 No Content', headers)
                return []
            if method not in ('GET', 'HEAD'):
                headers.append(('Allow', 'GET, HEAD, OPTIONS'))
                raise ApiError(405, 'method_not_allowed', 'This API is read-only.')
            with database(path) as db:
                body = route(db, environ.get('PATH_INFO', ''), environ.get('QUERY_STRING', ''))
        except ApiError as error:
            status, cache = error.status, False
            body = {'error': {'code': error.code, 'message': error.message}}
        except (sqlite3.Error, OSError, ValueError, KeyError, TypeError, IndexError):
            status, cache = 503, False
            body = {'error': {'code': 'catalog_unavailable', 'message': 'Catalog is temporarily unavailable.'}}
        encoded = canonical(body).encode('utf-8')
        if accepts_gzip(environ.get('HTTP_ACCEPT_ENCODING', '')) and len(encoded) >= 1024:
            encoded = gzip.compress(encoded, mtime=0)
            headers.append(('Content-Encoding', 'gzip'))
        headers.append(('Cache-Control', 'public, max-age=60, must-revalidate' if cache else 'no-store'))
        if cache:
            etag = '"' + hashlib.sha256(encoded).hexdigest() + '"'
            headers.append(('ETag', etag))
            supplied = [part.strip().removeprefix('W/') for part in environ.get('HTTP_IF_NONE_MATCH', '').split(',')]
            if etag in supplied or '*' in supplied:
                start_response('304 Not Modified', headers)
                return []
        labels = {200: 'OK', 400: 'Bad Request', 404: 'Not Found', 405: 'Method Not Allowed', 409: 'Conflict', 503: 'Service Unavailable'}
        headers.append(('Content-Length', str(len(encoded))))
        start_response(str(status) + ' ' + labels[status], headers)
        return [] if method == 'HEAD' else [encoded]

    return application


def main():
    from wsgiref.simple_server import make_server
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--database', type=Path, required=True)
    parser.add_argument('--host', default='127.0.0.1')
    parser.add_argument('--port', type=int, default=8091)
    options = parser.parse_args()
    with make_server(options.host, options.port, create_app(options.database)) as server:
        server.serve_forever()


if __name__ == '__main__':
    main()
