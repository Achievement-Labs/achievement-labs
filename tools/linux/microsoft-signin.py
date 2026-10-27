"""Ephemeral WebKitGTK sign-in. Input/output use anonymous pipes, never argv/files."""
import json
import sys
from urllib.parse import parse_qs, urlsplit


def callback(uri, state):
    parsed = urlsplit(uri)
    if (parsed.scheme != 'https' or parsed.hostname != 'login.live.com'
            or parsed.port not in (None, 443) or parsed.username or parsed.password
            or parsed.path != '/oauth20_desktop.srf' or parsed.fragment):
        raise ValueError('Invalid callback origin')
    query = parse_qs(parsed.query, keep_blank_values=True)
    if (query.get('state') != [state] or len(query.get('code', [])) != 1
            or not query['code'][0].strip() or 'error' in query):
        raise ValueError('Invalid callback parameters')
    return uri


def main():
    self_test = sys.argv[1:] == ['--self-test']
    state = 'synthetic-state-no-account'
    if self_test:
        valid = 'https://login.live.com/oauth20_desktop.srf?state=' + state + '&code=synthetic'
        assert callback(valid, state) == valid
        for invalid in [valid + '&code=other', valid + '&state=other', valid + '&error=denied',
                        valid + '#fragment', valid.replace('login.live.com', 'example.com'),
                        valid.replace('https:', 'http:'), valid.replace(state, 'wrong')]:
            try:
                callback(invalid, state)
            except ValueError:
                continue
            raise AssertionError('Invalid callback accepted')
    else:
        request = sys.stdin.buffer.readline(16385)
        if len(request) > 16384:
            raise ValueError('Request too large')
        start = json.loads(request)['startUri']
        parsed = urlsplit(start)
        if (parsed.scheme != 'https' or parsed.hostname != 'login.live.com'
                or parsed.username or parsed.password or parsed.port not in (None, 443)
                or parsed.path != '/oauth20_authorize.srf' or parsed.fragment):
            raise ValueError('Invalid sign-in origin')
        values = parse_qs(parsed.query)
        if len(values.get('state', [])) != 1 or not values['state'][0]:
            raise ValueError('Missing state')
        state = values['state'][0]

    import gi
    gi.require_version('Gtk', '3.0')
    gi.require_version('WebKit2', '4.1')
    from gi.repository import Gtk, WebKit2, GLib

    context = WebKit2.WebContext.new_ephemeral()
    view = WebKit2.WebView.new_with_context(context)
    view.get_settings().set_enable_media_stream(False)
    view.get_settings().set_media_playback_requires_user_gesture(True)
    window = Gtk.Window(title='Achievement Labs — Microsoft sign-in')
    window.set_default_size(600, 760)
    window.add(view)
    finished = False

    def finish(status, redirect=None):
        nonlocal finished
        if finished:
            return False
        finished = True
        result = {'status': status}
        if redirect is not None:
            result['redirect'] = redirect
        print(json.dumps(result), flush=True)
        Gtk.main_quit()
        return False

    def decide(_view, decision, kind):
        if kind not in (WebKit2.PolicyDecisionType.NAVIGATION_ACTION, WebKit2.PolicyDecisionType.NEW_WINDOW_ACTION):
            return False
        uri = decision.get_navigation_action().get_request().get_uri()
        parsed = urlsplit(uri)
        if self_test and uri == 'about:blank':
            return False
        if parsed.hostname == 'login.live.com' and parsed.path == '/oauth20_desktop.srf':
            decision.ignore()  # Capture before the landing page can consume/clear the code.
            try:
                callback(uri, state)
                finish('selfTestPassed' if self_test else 'completed', None if self_test else uri)
            except ValueError:
                finish('invalid')
            return True
        if self_test or parsed.scheme not in ('https', 'about'):
            decision.ignore()
            if self_test:
                finish('invalid')
            return True
        if kind == WebKit2.PolicyDecisionType.NEW_WINDOW_ACTION:
            decision.ignore()
            view.load_uri(uri)
            return True
        return False

    view.connect('decide-policy', decide)
    view.connect('permission-request', lambda _view, permission: (permission.deny(), True)[1])
    window.connect('delete-event', lambda *_: (finish('cancelled'), False)[1])
    GLib.timeout_add_seconds(30 if self_test else 900, lambda: finish('timeout'))
    window.show_all()
    if self_test:
        view.load_html('<html><script>location.href=' + json.dumps(valid) + '</script></html>', 'about:blank')
    else:
        view.load_uri(start)
    Gtk.main()
    window.destroy()


if __name__ == '__main__':
    try:
        main()
    except Exception:
        # Never print exceptions containing start/callback URLs or authentication data.
        print('{"status":"unavailable"}', flush=True)
        sys.exit(1)
