"""What the bridge needs of upstream, checked against the upstream in this tree.

After every merge of upstream this is the test that says whether the Dock
still has what it calls: a name upstream renamed or a signature it changed
fails here, in the build, instead of on the Dock at run time.
"""
import inspect
import json
import os
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _support  # noqa: E402

W = _support.W
B = _support.bridge_mod


class ContractTest(unittest.TestCase):
    def test_every_name_exists(self):
        missing = [n for n in B.UPSTREAM_API if not hasattr(W, n)]
        self.assertEqual(missing, [], 'upstream no longer provides these')

    def test_signatures(self):
        expect = {
            'fetch_usage': ['cfg'],
            'fetch_account_info': ['session_key'],
            'scoped_model': ['d'],
            'account_methods': ['acc'],
            'save_cfg': ['data'],
        }
        for name, params in expect.items():
            got = list(inspect.signature(getattr(W, name)).parameters)
            self.assertEqual(got[:len(params)], params, name)
        self.assertIn('force', inspect.signature(W.fetch_claude_code_profile).parameters)

    def test_exceptions_are_classes(self):
        for name in ('CCLoginElsewhere', 'CCLoginExpired', 'CCNoLogin', 'RateLimited',
                     'NoActivePlan', 'CCNoOrg'):
            self.assertTrue(issubclass(getattr(W, name), Exception), name)
        # The bridge reports these as "Claude Code" problems before the
        # generic PermissionError: they must stay ordered that way.
        self.assertTrue(issubclass(W.CCLoginExpired, PermissionError))

    def test_scoped_model_shapes(self):
        self.assertEqual(W.scoped_model({'limits': [{'percent': 5, 'resets_at': 'r',
                                                     'scope': {'model': {'display_name': 'M'}}}]}),
                         (5, 'r', 'M'))
        self.assertEqual(W.scoped_model({'seven_day_sonnet': {'utilization': 3, 'resets_at': 'r'}}),
                         (3, 'r', None))
        self.assertEqual(W.scoped_model({}), (None, None, None))

    def test_import_has_no_window_and_no_mutex(self):
        # Importing must stay headless: the bridge runs next to the widget.
        self.assertNotEqual(W.__name__, '__main__')
        self.assertFalse(hasattr(W, '_mutex'))


@unittest.skipUnless(sys.platform == 'win32', 'runs upstream for real, which needs Windows')
class ProcessTest(unittest.TestCase):
    """The bridge as the extension starts it: a separate python, real stdio."""

    def test_stdout_carries_the_protocol_alone(self):
        data = tempfile.mkdtemp(prefix='bridge-proc-')
        script = os.path.join(_support.BRIDGE_DIR, 'claude_usage_bridge.py')
        requests = [{'id': 1, 'cmd': 'hello'}, {'id': 2, 'cmd': 'accounts'}]
        proc = subprocess.run(
            [sys.executable, script, '--data-dir', data, '--upstream', _support.UPSTREAM],
            input='\n'.join(json.dumps(r) for r in requests) + '\n',
            capture_output=True, text=True, encoding='utf-8', timeout=120, env=os.environ.copy())
        self.assertEqual(proc.returncode, 0, proc.stderr)
        answers = [json.loads(line) for line in proc.stdout.splitlines()]
        self.assertEqual([a['id'] for a in answers], [1, 2])
        self.assertEqual(answers[0]['upstream_version'], W.APP_VERSION)
        self.assertEqual(answers[1]['accounts'], [])


if __name__ == '__main__':
    unittest.main()
