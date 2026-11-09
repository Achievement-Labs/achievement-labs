"""Check every public API record against the selected repository source."""
import argparse
import json
from pathlib import Path
from urllib.parse import urlencode

from .app import PREFIX, create_app
from .build import project


def check(repo, database):
    titles, totals = project(repo)
    app = create_app(database)

    def get(path='', query=''):
        status = []
        raw = b''.join(app({'REQUEST_METHOD': 'GET', 'PATH_INFO': PREFIX + path, 'QUERY_STRING': query},
                          lambda value, headers: status.append(value)))
        if status != ['200 OK']:
            raise AssertionError(raw.decode())
        return json.loads(raw)

    metadata = get()
    assert metadata['titleCount'] == len(titles)
    seen, cursor = [], None
    while True:
        response = get('/titles', urlencode({'limit': 100, **({'cursor': cursor} if cursor else {})}))
        assert response['catalogRevision'] == metadata['catalogRevision']
        seen.extend(response['items'])
        cursor = response['nextCursor']
        if cursor is None:
            break
    assert sorted(seen, key=lambda r: r['titleId']) == titles
    for expected in titles:
        response = get('/titles/' + expected['titleId'])
        assert response.pop('catalogRevision') == metadata['catalogRevision']
        assert response.pop('sourceRevision') == metadata['sourceRevision']
        assert response == expected
    checked_totals = []
    for start in range(0, len(totals), 100):
        batch = totals[start:start + 100]
        response = get('/totals', urlencode({'ids': ','.join(r['titleId'] for r in batch)}))
        assert not response['missingIds']
        assert response['items'] == batch
        checked_totals.extend(response['items'])
    assert checked_totals == totals
    assert sum(metadata['statusCounts'].values()) == len(titles)
    print(json.dumps({'verifiedTitles': len(titles), 'verifiedDefinitionTotals': len(totals),
                      'statusCounts': metadata['statusCounts'], 'catalogRevision': metadata['catalogRevision']}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, default=Path('.'))
    parser.add_argument('--database', type=Path, required=True)
    options = parser.parse_args()
    check(options.repo, options.database)
