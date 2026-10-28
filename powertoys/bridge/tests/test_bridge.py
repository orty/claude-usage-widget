"""The bridge, end to end through upstream's own code.

Only the transport is faked (widget._curl_call, the one place a request leaves
the process), so what runs is upstream's fetch_usage, its Claude Code checks,
its key rotation and its config writer, as the Dock will run them.

    python -m unittest discover -s powertoys/bridge/tests
"""
import io
import json
import os
import shutil
import sys
import tempfile
import time
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _support  # noqa: E402

W = _support.W
B = _support.bridge_mod

KEY_OLD = 'sk-ant-sid01-' + 'a' * 60
KEY_NEW = 'sk-ant-sid01-' + 'b' * 60
KEY_OTHER = 'sk-ant-sid01-' + 'c' * 60
ORG_A, ORG_B = 'org-aaaa', 'org-bbbb'

USAGE = {
    'five_hour': {'utilization': 42.0, 'resets_at': '2099-01-01T10:00:00+00:00'},
    'seven_day': {'utilization': 17.0, 'resets_at': '2099-01-05T10:00:00+00:00'},
    'limits': [{'percent': 8, 'resets_at': '2099-01-05T10:00:00+00:00',
                'scope': {'model': {'display_name': 'Fable'}}}],
}


class FakeNet:
    """Stands in for widget._curl_call: routes by URL, records every call."""

    def __init__(self):
        self.routes = []
        self.calls = []

    def on(self, fragment, status=200, body=None, set_cookie=None):
        self.routes.insert(0, (fragment, status, body, set_cookie))

    def __call__(self, url, cookie, timeout=20, retry_transient=True, headers=(), data=None):
        self.calls.append({'url': url, 'cookie': cookie, 'headers': headers})
        for fragment, status, body, set_cookie in self.routes:
            if fragment in url:
                head = f'HTTP/2 {status}\r\ncontent-type: application/json\r\n'
                if set_cookie:
                    head += f'set-cookie: sessionKey={set_cookie}; Path=/; HttpOnly\r\n'
                return head, json.dumps(body) if body is not None else ''
        return 'HTTP/2 404\r\n', '{}'


class BridgeTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix='bridge-')
        self.saved = {n: getattr(W, n) for n in ('CFG', 'CFG_BAK', 'LOG_FILE', 'CRASH_LOG_FILE',
                                                 '_curl_call', 'fetch_account_info')}
        self.net = FakeNet()
        W._curl_call = self.net
        # Upstream keeps per-process caches; each test starts from none.
        W._CC_USAGE_CACHE.update(key=None, at=0.0, data=None)
        W._CC_PROFILE_CACHE.clear()
        W._SAVED_TOKEN_ORG.clear()
        W._cc_paused.clear()
        self.widget_cfg = os.path.join(self.tmp, 'widget-config.json')
        B.retarget(W, os.path.join(self.tmp, 'pt'))
        self.bridge = B.Bridge(W, self.widget_cfg)
        cred = os.environ['CLAUDE_WIDGET_CC_CRED']
        if os.path.exists(cred):
            os.remove(cred)

    def tearDown(self):
        for n, v in self.saved.items():
            setattr(W, n, v)
        shutil.rmtree(self.tmp, ignore_errors=True)

    # ── helpers ──

    def stored(self):
        with open(W.CFG, encoding='utf-8') as f:
            return json.load(f)

    def add_key_account(self, name, key, org):
        W.fetch_account_info = lambda k: {'org_id': org, 'email': f'{name}@x', 'name': name,
                                          'plan': 'Max', 'org_choices': None}
        res = self.bridge.handle({'cmd': 'add_key', 'args': {'session_key': key, 'name': name}})
        self.assertTrue(res['ok'], res)
        return res['account']

    def sign_in_claude_code(self, token='cc-token-1', expires_in=3600):
        with open(os.environ['CLAUDE_WIDGET_CC_CRED'], 'w', encoding='utf-8') as f:
            json.dump({'claudeAiOauth': {'accessToken': token,
                                         'expiresAt': int((time.time() + expires_in) * 1000),
                                         'subscriptionType': 'max'}}, f)

    # ── tests ──

    def test_hello_names_upstream_version(self):
        res = self.bridge.handle({'cmd': 'hello'})
        self.assertTrue(res['ok'])
        self.assertEqual(res['upstream_version'], W.APP_VERSION)
        self.assertEqual(res['protocol'], B.PROTOCOL)

    def test_files_are_the_bridges_own(self):
        self.add_key_account('Work', KEY_OLD, ORG_A)
        self.assertTrue(W.CFG.startswith(self.tmp))
        self.assertFalse(os.path.exists(self.widget_cfg), 'the widget config must never be written')

    def test_usage_with_session_key_and_rotation(self):
        work = self.add_key_account('Work', KEY_OLD, ORG_A)
        # A second account of the same login in another org holds the same key.
        self.bridge.cfg['accounts'].append({'id': 'twin', 'name': 'Team', 'org_id': ORG_B,
                                            'session_key': KEY_OLD})
        self.net.on(f'/organizations/{ORG_A}/usage', body=USAGE, set_cookie=KEY_NEW)
        res = self.bridge.handle({'cmd': 'usage', 'args': {'account_id': work['id']}})
        self.assertTrue(res['ok'], res)
        self.assertEqual(res['session'], {'pct': 42.0, 'resets_at': USAGE['five_hour']['resets_at']})
        self.assertEqual(res['weekly']['pct'], 17.0)
        self.assertEqual(res['model'], {'pct': 8, 'resets_at': USAGE['limits'][0]['resets_at'],
                                        'name': 'Fable'})
        self.assertIn(f'sessionKey={KEY_OLD}', self.net.calls[-1]['cookie'])
        keys = {a['id']: a['session_key'] for a in self.stored()['accounts']}
        self.assertEqual(keys[work['id']], KEY_NEW)
        self.assertEqual(keys['twin'], KEY_NEW, 'the retired key must not stay on the twin')

    def test_rotation_ignored_for_a_cookie_that_is_not_a_key(self):
        work = self.add_key_account('Work', KEY_OLD, ORG_A)
        self.net.on('/usage', body=USAGE, set_cookie='""')
        self.assertTrue(self.bridge.handle({'cmd': 'usage', 'args': {'account_id': work['id']}})['ok'])
        self.assertEqual(self.stored()['accounts'][0]['session_key'], KEY_OLD)

    def test_expired_key_is_auth_error(self):
        work = self.add_key_account('Work', KEY_OLD, ORG_A)
        self.net.on('/usage', status=401, body={'error': 'nope'})
        res = self.bridge.handle({'cmd': 'usage', 'args': {'account_id': work['id']}})
        self.assertFalse(res['ok'])
        self.assertEqual(res['error']['kind'], 'auth')
        self.assertNotIn(KEY_OLD, json.dumps(res))

    def test_claude_code_account(self):
        self.sign_in_claude_code()
        self.net.on('/api/oauth/profile', body={'account': {'email_address': 'me@x', 'display_name': 'Me'},
                                                'organization': {'uuid': ORG_A, 'name': 'Mine'}})
        self.net.on('/api/oauth/usage', body=USAGE)
        res = self.bridge.handle({'cmd': 'add_claude_code', 'args': {'name': 'Personal'}})
        self.assertTrue(res['ok'], res)
        acc = res['account']
        self.assertEqual((acc['name'], acc['org_id'], acc['methods']), ('Personal', ORG_A, ['claude_code']))
        res = self.bridge.handle({'cmd': 'usage', 'args': {'account_id': acc['id']}})
        self.assertTrue(res['ok'], res)
        self.assertEqual(res['session']['pct'], 42.0)
        usage_call = [c for c in self.net.calls if c['url'].endswith('/api/oauth/usage')][-1]
        self.assertIn('Authorization: Bearer cc-token-1', usage_call['headers'])

    def test_claude_code_signed_in_as_another_account(self):
        self.sign_in_claude_code()
        self.net.on('/api/oauth/profile', body={'account': {'email_address': 'other@x'},
                                                'organization': {'uuid': ORG_B}})
        self.bridge.cfg['accounts'].append({'id': 'a', 'name': 'Work', 'org_id': ORG_A, 'cc_linked': True})
        res = self.bridge.handle({'cmd': 'usage', 'args': {'account_id': 'a'}})
        self.assertFalse(res['ok'])
        # Upstream refuses to show another account's numbers under this name.
        self.assertEqual(res['error']['kind'], 'cc_elsewhere')
        self.assertFalse([c for c in self.net.calls if c['url'].endswith('/api/oauth/usage')])

    def test_choose_org_when_ambiguous(self):
        W.fetch_account_info = lambda k: {'org_id': None, 'email': 'x', 'name': 'x', 'plan': '',
                                          'org_choices': [{'id': ORG_A, 'name': 'A'},
                                                          {'id': ORG_B, 'name': 'B'}]}
        res = self.bridge.handle({'cmd': 'add_key', 'args': {'session_key': KEY_OLD}})
        self.assertEqual(res['error']['kind'], 'choose_org')
        self.assertEqual([c['id'] for c in res['org_choices']], [ORG_A, ORG_B])
        res = self.bridge.handle({'cmd': 'add_key', 'args': {'session_key': KEY_OLD, 'org_id': ORG_B}})
        self.assertTrue(res['ok'], res)
        self.assertEqual(res['account']['org_id'], ORG_B)

    def login(self, org, email, name='Someone'):
        W.fetch_account_info = lambda k: {'org_id': org, 'email': email, 'name': name,
                                          'plan': 'Max', 'org_choices': None}

    def test_adding_never_replaces_an_account(self):
        work = self.add_key_account('Work', KEY_OLD, ORG_A)
        # The same login again: refused, Work untouched.
        self.login(ORG_A, 'Work@x')
        res = self.bridge.handle({'cmd': 'add_key', 'args': {'session_key': KEY_OTHER, 'name': 'Third'}})
        self.assertEqual(res['error']['kind'], 'already_listed')
        self.assertEqual(res['account']['id'], work['id'])
        # A colleague's seat in the same organization: an account of its own.
        self.login(ORG_A, 'colleague@x')
        res = self.bridge.handle({'cmd': 'add_key', 'args': {'session_key': KEY_NEW, 'name': 'Third'}})
        self.assertTrue(res['ok'], res)
        self.assertTrue(res['added'])
        accounts = {a['name']: a for a in self.stored()['accounts']}
        self.assertEqual(sorted(accounts), ['Third', 'Work'])
        self.assertEqual(accounts['Work']['session_key'], KEY_OLD)
        self.assertEqual(accounts['Work']['email'], 'Work@x')
        self.assertEqual(accounts['Third']['session_key'], KEY_NEW)

    def test_adding_a_key_completes_an_imported_account(self):
        with open(self.widget_cfg, 'w', encoding='utf-8') as f:
            json.dump({'accounts': [{'id': '1', 'name': 'Work', 'org_id': ORG_A, 'email': 'w@x'}]}, f)
        self.bridge.handle({'cmd': 'import_widget'})
        self.login(ORG_A, 'w@x')
        res = self.bridge.handle({'cmd': 'add_key', 'args': {'session_key': KEY_OLD, 'name': 'Other name'}})
        self.assertTrue(res['ok'], res)
        self.assertFalse(res['added'])
        [acc] = self.stored()['accounts']
        self.assertEqual((acc['name'], acc['session_key']), ('Work', KEY_OLD))

    def test_a_login_of_two_organizations_is_offered_both(self):
        W.fetch_account_info = lambda k: {
            'org_id': ORG_B, 'email': 'me@x', 'name': 'Me', 'plan': 'Team',
            'org_choices': [{'id': ORG_B, 'name': 'Powell', 'tier': ''},
                            {'id': ORG_A, 'name': "me@x's Organization", 'tier': ''}]}
        res = self.bridge.handle({'cmd': 'key_orgs', 'args': {'session_key': KEY_OLD}})
        self.assertEqual(res['default'], ORG_B)
        self.assertEqual([c['id'] for c in res['choices']], [ORG_B, ORG_A])
        res = self.bridge.handle({'cmd': 'add_key', 'args': {'session_key': KEY_OLD, 'org_id': ORG_A}})
        self.assertTrue(res['ok'], res)
        acc = self.stored()['accounts'][0]
        self.assertEqual(acc['org_id'], ORG_A)
        self.assertEqual(acc['name'], 'Me')  # a personal org is named after its person
        res = self.bridge.handle({'cmd': 'add_key', 'args': {'session_key': KEY_NEW, 'org_id': ORG_B}})
        self.assertTrue(res['added'], res)
        self.assertEqual(res['account']['name'], 'Powell')  # a team org after itself

    def test_adding_refuses_an_organization_the_key_cannot_read(self):
        # A personal key put under a team organization it is no member of
        # (one organization, so no choices to check against).
        self.login(ORG_A, 'me@x')
        res = self.bridge.handle({'cmd': 'add_key', 'args': {'session_key': KEY_OLD, 'org_id': ORG_B}})
        self.assertEqual(res['error']['kind'], 'unknown_org')
        self.assertIn('me@x', res['error']['message'])
        self.assertFalse(os.path.exists(W.CFG))  # nothing written

    def test_set_key_refuses_a_colleagues_key(self):
        work = self.add_key_account('Work', KEY_OLD, ORG_A)
        self.login(ORG_A, 'colleague@x')
        res = self.bridge.handle({'cmd': 'set_key', 'args': {'account_id': work['id'],
                                                              'session_key': KEY_OTHER}})
        self.assertEqual(res['error']['kind'], 'wrong_account')
        self.assertEqual(self.stored()['accounts'][0]['session_key'], KEY_OLD)

    def test_set_key_refuses_another_accounts_key(self):
        work = self.add_key_account('Work', KEY_OLD, ORG_A)
        W.fetch_account_info = lambda k: {'org_id': ORG_B, 'org_choices': None}
        res = self.bridge.handle({'cmd': 'set_key', 'args': {'account_id': work['id'],
                                                              'session_key': KEY_OTHER}})
        self.assertEqual(res['error']['kind'], 'wrong_account')
        self.assertEqual(self.stored()['accounts'][0]['session_key'], KEY_OLD)

    def test_import_from_widget_leaves_keys_behind(self):
        with open(self.widget_cfg, 'w', encoding='utf-8') as f:
            json.dump({'accounts': [
                {'id': '1', 'name': 'Work', 'org_id': ORG_A, 'session_key': KEY_OLD, 'cc_linked': True},
                {'id': '2', 'name': 'Side', 'org_id': ORG_B, 'session_key': KEY_OTHER},
                {'id': '3', 'name': 'Broken'},
            ]}, f)
        before = os.path.getmtime(self.widget_cfg)
        res = self.bridge.handle({'cmd': 'import_widget'})
        self.assertTrue(res['ok'], res)
        self.assertEqual(sorted(res['added']), ['Side', 'Work'])
        self.assertEqual(res['need_key'], ['Side'])
        with open(W.CFG, encoding='utf-8') as f:
            text = f.read()
        self.assertNotIn(KEY_OLD, text)
        self.assertNotIn(KEY_OTHER, text)
        self.assertEqual(os.path.getmtime(self.widget_cfg), before)
        # Without a key of its own, an imported account says so: it is not
        # an expired key, and nothing is sent to claude.ai.
        side = next(a for a in self.bridge.handle({'cmd': 'accounts'})['accounts'] if a['name'] == 'Side')
        calls = len(self.net.calls)
        res = self.bridge.handle({'cmd': 'usage', 'args': {'account_id': side['id']}})
        self.assertEqual(res['error']['kind'], 'no_credentials')
        self.assertEqual(len(self.net.calls), calls)

    def test_gauges(self):
        res = self.bridge.handle({'cmd': 'gauges', 'args': {'size': 32}})
        self.assertTrue(res['ok'], res)
        names = sorted(os.listdir(res['dir']))
        self.assertEqual(len(names), 3 * 21)
        from PIL import Image
        empty = Image.open(os.path.join(res['dir'], 'session-000.png'))
        full = Image.open(os.path.join(res['dir'], 'session-100.png'))
        self.assertEqual(empty.size, (32, 32))
        # The top of the ring: the empty gray at 0 %, the session amber at 100 %.
        top = (16, 5)
        self.assertNotEqual(empty.getpixel(top)[:3], full.getpixel(top)[:3])
        self.assertEqual(self.bridge.handle({'cmd': 'gauges', 'args': {'size': 32}})['dir'], res['dir'])

    def test_rename_and_remove(self):
        work = self.add_key_account('Work', KEY_OLD, ORG_A)
        self.assertEqual(self.bridge.handle({'cmd': 'rename', 'args': {'account_id': work['id'],
                                                                        'name': 'Job'}})['account']['name'], 'Job')
        self.assertTrue(self.bridge.handle({'cmd': 'remove', 'args': {'account_id': work['id']}})['ok'])
        self.assertEqual(self.stored()['accounts'], [])
        self.assertEqual(self.bridge.handle({'cmd': 'remove', 'args': {'account_id': work['id']}})
                         ['error']['kind'], 'unknown_account')

    def test_protocol_stream(self):
        self.add_key_account('Work', KEY_OLD, ORG_A)
        inp = io.StringIO('\n'.join([
            json.dumps({'id': 1, 'cmd': 'hello'}),
            'not json',
            json.dumps({'id': 'x', 'cmd': 'nope'}),
            json.dumps({'id': 3, 'cmd': 'usage', 'args': {'wrong': 1}}),
            json.dumps({'id': 4, 'cmd': 'accounts'}),
            json.dumps({'id': 5, 'cmd': '__init__'}),
        ]) + '\n')
        out = io.StringIO()
        B.serve(self.bridge, inp, out)
        answers = [json.loads(line) for line in out.getvalue().splitlines()]
        self.assertEqual([a['id'] for a in answers], [1, None, 'x', 3, 4, 5])
        self.assertEqual([a['ok'] for a in answers], [True, False, False, False, True, False])
        self.assertEqual(answers[4]['accounts'][0]['name'], 'Work')
        self.assertTrue(answers[4]['accounts'][0]['has_key'])
        self.assertNotIn(KEY_OLD, out.getvalue(), 'a key must never cross the protocol')


if __name__ == '__main__':
    unittest.main()
