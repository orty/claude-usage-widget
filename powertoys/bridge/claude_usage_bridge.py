"""The usage of several Claude accounts, for the PowerToys Command Palette Dock.

    python claude_usage_bridge.py --data-dir DIR [--upstream SRC_DIR]
    python claude_usage_bridge.py --data-dir DIR --once CMD ['{"arg": ...}']

Command Palette extensions are WinRT COM servers written in C#, and the widget
is Python. Porting the widget's reading of the usage to C# would make a second
copy of the part of upstream that changes most often (the endpoints, the
Claude Code login and its saved copies, the rate-limit pause, the key
claude.ai rotates), and every upstream fix to it would have to be ported again
by hand. So this file ports nothing: it loads upstream's own widget.pyw as a
module, the way cc_cli.py is handed it, and calls the same fetch_usage the
widget calls on every refresh. Merging upstream into this branch updates the
Dock with no change here.

The extension starts one bridge and keeps it for its lifetime: a long-lived
process keeps upstream's in-memory state (the usage reused for two minutes,
the pause after a 429) that one process per refresh would lose, which is what
caused the storm of 429s upstream describes on 25 Sep 2026.

Protocol: one JSON object per line on stdin, one answer per line on stdout,
in order. A request is {"id": any, "cmd": "usage", "args": {...}}; every answer
carries the request's id and "ok". stdout is kept for the protocol alone:
anything upstream prints goes to stderr.

The accounts live in a file of their own (DIR/accounts.json), never in the
widget's config.json. The widget that is running owns that file, rewrites it
from memory, and would undo any write made here. And the two must not share
a session key: claude.ai rotates a key on an answer and retires the old one,
so the first of the two to read would leave the other holding a dead key.
Accounts imported from the widget therefore come without their session key;
the Claude Code login, which a read does not rotate, is shared freely.
"""
import argparse
import importlib.machinery
import importlib.util
import json
import os
import sys
import traceback

PROTOCOL = 1
BRIDGE_VERSION = '1.0.0'

HERE = os.path.dirname(os.path.abspath(__file__))
# In the repository the bridge sits in powertoys/bridge and upstream in src;
# the extension's package puts upstream next to the bridge instead.
DEFAULT_UPSTREAM = next(
    (p for p in (os.path.join(HERE, 'upstream'), os.path.join(HERE, '..', '..', 'src'))
     if os.path.exists(os.path.join(p, 'widget.pyw'))),
    os.path.join(HERE, '..', '..', 'src'))

# What this file needs of upstream. tests/test_contract.py checks every name,
# so a rename upstream fails the build instead of the Dock at run time.
UPSTREAM_API = (
    'APP_VERSION', 'CFG', 'CFG_BAK', 'LOG_FILE', 'CRASH_LOG_FILE',
    'load_cfg', 'save_cfg', 'fetch_usage', 'fetch_account_info',
    'fetch_claude_code_profile', 'scoped_model', 'account_methods',
    'plausible_key', 'set_lang', 'wlog', '_redact', '_new_id',
    'CCLoginElsewhere', 'CCLoginExpired', 'CCNoLogin', 'CCNoOrg',
    'RateLimited', 'NoActivePlan', 'AUTH_AUTO',
    'BAR_BG', 'BAR_FILL_SESSION', 'BAR_TRACK_SESSION', 'BAR_FILL_WEEKLY',
    'BAR_TRACK_WEEKLY', 'BAR_FILL_HIGH', 'BAR_TRACK_HIGH',
)

# The Dock's gauge icons: one per 5 %, per bar.
GAUGE_STEP = 5


def load_upstream(src_dir):
    """Upstream's widget.pyw as the module `widget`. Its __main__ block does
    not run, so no window, no single-instance mutex, no update check."""
    src_dir = os.path.abspath(src_dir)
    path = os.path.join(src_dir, 'widget.pyw')
    if src_dir not in sys.path:
        sys.path.insert(0, src_dir)
    loader = importlib.machinery.SourceFileLoader('widget', path)
    spec = importlib.util.spec_from_loader('widget', loader)
    module = importlib.util.module_from_spec(spec)
    sys.modules['widget'] = module
    loader.exec_module(module)
    # The widget installs a hook that answers a start-up failure with a
    # message box. Nobody is at this process to click it.
    sys.excepthook = sys.__excepthook__
    return module


def retarget(w, data_dir):
    """Point the module's files at the bridge's own folder. Returns the path
    of the widget's own config, which is only ever read (import_widget).

    Upstream reads these globals at call time, so rebinding them is enough:
    load_cfg and save_cfg, with their backup and atomic replace, then work on
    the bridge's file exactly as they do on the widget's."""
    os.makedirs(data_dir, exist_ok=True)
    widget_cfg = w.CFG
    w.CFG = os.path.join(data_dir, 'accounts.json')
    w.CFG_BAK = w.CFG + '.bak'
    w.LOG_FILE = os.path.join(data_dir, 'bridge.log')
    w.CRASH_LOG_FILE = os.path.join(data_dir, 'crash.log')
    return widget_cfg


def _error(kind, message):
    return {'ok': False, 'error': {'kind': kind, 'message': message}}


class Bridge:
    def __init__(self, w, widget_cfg):
        self.w = w
        self.widget_cfg = widget_cfg
        self.cfg = w.load_cfg() or {}
        self.cfg.setdefault('accounts', [])
        w.set_lang(self.cfg.get('language') or 'en')

    # ── helpers ──────────────────────────────────────

    def _save(self):
        self.w.save_cfg(self.cfg)

    def _find(self, account_id):
        return next((a for a in self.cfg['accounts'] if a.get('id') == account_id), None)

    def _public(self, acc):
        """An account as the extension sees it: never a key, never a token."""
        return {
            'id': acc.get('id'),
            'name': acc.get('name') or acc.get('email') or 'Claude',
            'email': acc.get('email') or '',
            'plan': acc.get('plan') or '',
            'org_id': acc.get('org_id') or '',
            'cc_linked': bool(acc.get('cc_linked')),
            'has_key': bool(self.w.plausible_key(acc.get('session_key') or '')),
            'methods': list(self.w.account_methods(acc)),
        }

    def _explain(self, e):
        """An exception of upstream's as {kind, message}, in upstream's words.
        The most specific class first: the Claude Code ones are PermissionErrors."""
        w = self.w
        kinds = (('CCLoginElsewhere', 'cc_elsewhere'), ('CCLoginExpired', 'cc_expired'),
                 ('CCNoLogin', 'cc_no_login'), ('CCNoOrg', 'cc_no_org'),
                 ('RateLimited', 'rate_limited'), ('NoActivePlan', 'no_plan'))
        kind = 'error'
        for name, label in kinds:
            cls = getattr(w, name, None)
            if isinstance(cls, type) and isinstance(e, cls):
                kind = label
                break
        else:
            if isinstance(e, PermissionError):
                kind = 'auth'
        message = w._redact(str(e)) or type(e).__name__
        return {'kind': kind, 'message': message}

    def _apply_rotation(self, rotation):
        """Keep the key claude.ai issued in place of the one it retired, on the
        account it was issued for and on any other account of the same login
        still holding the old key, as Widget._apply_rotated_key does."""
        if not rotation:
            return
        acct_id, new_key, used_key = rotation
        if not self.w.plausible_key(new_key):
            self.w.wlog('BRIDGE refused a rotated key that does not look like one')
            return
        acc = self._find(acct_id)
        if acc is None or (used_key is not None and acc.get('session_key') != used_key):
            self.w.wlog('BRIDGE rotated key dropped: the account holds another key now')
            return
        for a in self.cfg['accounts']:
            if a is acc or (used_key and a.get('session_key') == used_key):
                a['session_key'] = new_key
        self._save()
        self.w.wlog('BRIDGE kept a rotated session key')

    @staticmethod
    def _bar(pct, resets_at, name=None):
        if pct is None and not resets_at:
            return None
        bar = {'pct': pct, 'resets_at': resets_at}
        if name is not None:
            bar['name'] = name
        return bar

    @staticmethod
    def _same_person(acc, email):
        """Whether a login of the same organization is this account's login.
        The organization alone is not enough: the seats of a Team plan share
        one organization but each has its own usage. Two known emails that
        differ are two people; an unknown email on either side cannot tell
        them apart, and the organization decides, as in the widget."""
        mine = (acc.get('email') or '').strip().lower()
        theirs = (email or '').strip().lower()
        return not mine or not theirs or mine == theirs

    def _find_identity(self, org_id, email):
        """The account this login is: same organization and same person."""
        if not org_id:
            return None
        return next((a for a in self.cfg['accounts']
                     if a.get('org_id') == org_id and self._same_person(a, email)), None)

    def _merge(self, fields, label='', fallback=''):
        """Add an account, or complete the one this login already is. An
        account already listed keeps its name: adding never renames or
        replaces one (renaming is the rename command's)."""
        acc = self._find_identity(fields.get('org_id'), fields.get('email'))
        added = acc is None
        if added:
            acc = {'id': self.w._new_id(), 'auth_pref': self.w.AUTH_AUTO,
                   'name': label or fallback or 'Claude'}
            self.cfg['accounts'].append(acc)
        for k, v in fields.items():
            if v not in (None, '') or k not in acc:
                acc[k] = v
        self._save()
        return {'ok': True, 'added': added, 'account': self._public(acc)}

    # ── commands ─────────────────────────────────────

    def cmd_hello(self):
        return {'ok': True, 'protocol': PROTOCOL, 'bridge_version': BRIDGE_VERSION,
                'upstream_version': getattr(self.w, 'APP_VERSION', '?'),
                'data_file': self.w.CFG}

    def cmd_gauges(self, size=64):
        """Ring gauges for the Dock buttons' icons, drawn in the widget's own
        bar colours (fill and track per bar, the empty gray at 0 %), one image
        per GAUGE_STEP. The folder is named after what they are drawn from, so
        a change of colours upstream draws a new set."""
        import hashlib
        from PIL import Image, ImageDraw
        w = self.w
        bars = {'session': (w.BAR_FILL_SESSION, w.BAR_TRACK_SESSION),
                'weekly': (w.BAR_FILL_WEEKLY, w.BAR_TRACK_WEEKLY),
                'model': (w.BAR_FILL_HIGH, w.BAR_TRACK_HIGH)}
        size = max(16, min(256, int(size)))
        tag = hashlib.sha1(json.dumps([bars, w.BAR_BG, size, GAUGE_STEP]).encode()).hexdigest()[:10]
        folder = os.path.join(os.path.dirname(os.path.abspath(w.CFG)), 'gauges-' + tag)
        os.makedirs(folder, exist_ok=True)
        scale = 4  # drawn large, then reduced: smooth edges
        big = size * scale
        inset = big * 0.10
        ring = max(scale, int(big * 0.20))
        box = (inset, inset, big - inset, big - inset)
        for kind, (fill, track) in bars.items():
            for pct in range(0, 101, GAUGE_STEP):
                path = os.path.join(folder, f'{kind}-{pct:03d}.png')
                if os.path.exists(path):
                    continue
                img = Image.new('RGBA', (big, big), (0, 0, 0, 0))
                d = ImageDraw.Draw(img)
                # As in the widget: the dark track only once there is usage.
                d.arc(box, 0, 360, fill=track if pct else w.BAR_BG, width=ring)
                if pct:
                    d.arc(box, -90, -90 + 360 * pct / 100, fill=fill, width=ring)
                tmp = path + '.tmp'
                img.resize((size, size), Image.LANCZOS).save(tmp, 'PNG')
                os.replace(tmp, path)
        return {'ok': True, 'dir': folder, 'step': GAUGE_STEP}

    def cmd_accounts(self):
        return {'ok': True, 'accounts': [self._public(a) for a in self.cfg['accounts']]}

    def cmd_usage(self, account_id):
        acc = self._find(account_id)
        if acc is None:
            return _error('unknown_account', 'No such account.')
        if not self.w.account_methods(acc):
            # Imported from the widget (keys are never copied) or its key
            # removed: nothing to read with, which is not an expired key.
            return _error('no_credentials',
                          'This account has no session key of its own and no Claude Code login. '
                          'Add a session key from its menu, or add the account Claude Code is '
                          'signed in as.')
        # fetch_usage reads the active account of the config it is given: a
        # view holding this account alone, over the very same dict, so a key
        # rotation lands on the stored account.
        view = {'accounts': [acc], 'active_account': acc['id']}
        try:
            data, rotation = self.w.fetch_usage(view)
        except Exception as e:
            self._apply_rotation(getattr(e, 'rotation', None))
            return {'ok': False, 'error': self._explain(e)}
        self._apply_rotation(rotation)
        fh = data.get('five_hour') or {}
        sd = data.get('seven_day') or {}
        mp, mr, mname = self.w.scoped_model(data)
        return {'ok': True,
                'session': self._bar(fh.get('utilization'), fh.get('resets_at')),
                'weekly': self._bar(sd.get('utilization'), sd.get('resets_at')),
                'model': self._bar(mp, mr, mname)}

    def cmd_add_key(self, session_key, name='', org_id=''):
        key = (session_key or '').strip()
        if not self.w.plausible_key(key):
            return _error('invalid_key', 'That is not a session key: it starts with sk-ant-.')
        try:
            info = self.w.fetch_account_info(key)
        except Exception as e:
            return {'ok': False, 'error': self._explain(e)}
        choices = info.get('org_choices') or []
        org = (org_id or '').strip() or info.get('org_id')
        if org_id and choices and org not in {c.get('id') for c in choices}:
            return _error('unknown_org', 'This login does not belong to that organization.')
        if not org:
            # The login reads several organizations and none is the obvious
            # one: the user picks, as in the widget's picker.
            return dict(_error('choose_org', 'This login belongs to several organizations: '
                                             'pick the one to track.'), org_choices=choices)
        # The same login added twice is not a second account. One that has no
        # key yet (imported from the widget) takes this one; one that has a
        # key is left alone: replacing a key is "Replace session key", never
        # a side effect of adding.
        same = self._find_identity(org, info.get('email'))
        if same is not None and self.w.plausible_key(same.get('session_key') or ''):
            return dict(_error('already_listed',
                               f'This key signs in to {same.get("name") or "an account"}, which is '
                               'already listed with a key. Nothing was changed. To give it this key, '
                               'use "Replace session key" on it.'),
                        account=self._public(same))
        return self._merge({'session_key': key, 'org_id': org,
                            'email': info.get('email') or '', 'plan': info.get('plan') or ''},
                           (name or '').strip(), info.get('name') or info.get('email'))

    def cmd_add_claude_code(self, name=''):
        """The account Claude Code is signed in as on this machine."""
        try:
            prof = self.w.fetch_claude_code_profile(force=True)
        except Exception as e:
            return {'ok': False, 'error': self._explain(e)}
        if not prof.get('org_id'):
            return _error('cc_no_org', 'Claude Code did not say which organization it is signed in to.')
        return self._merge({'org_id': prof['org_id'], 'cc_linked': True,
                            'email': prof.get('email') or '', 'plan': prof.get('plan') or ''},
                           (name or '').strip(), prof.get('name') or prof.get('email'))

    def cmd_import_widget(self):
        """The widget's accounts, read and never written, without their keys."""
        try:
            with open(self.widget_cfg, encoding='utf-8') as f:
                source = json.load(f)
        except (OSError, ValueError):
            return _error('no_widget', 'The Claude Usage widget has no accounts on this computer.')
        added, updated, need_key = [], [], []
        for a in (source.get('accounts') if isinstance(source, dict) else None) or []:
            if not isinstance(a, dict) or not a.get('org_id'):
                continue
            # Named as in the widget when first imported; renamed here, an
            # account keeps the name given here on a second import.
            res = self._merge({'org_id': a['org_id'], 'email': a.get('email') or '',
                               'plan': a.get('plan') or '', 'cc_linked': bool(a.get('cc_linked'))},
                              fallback=a.get('name') or a.get('email'))
            (added if res['added'] else updated).append(res['account']['name'])
            if not res['account']['methods']:
                need_key.append(res['account']['name'])
        return {'ok': True, 'added': added, 'updated': updated, 'need_key': need_key}

    def cmd_set_key(self, account_id, session_key):
        acc = self._find(account_id)
        if acc is None:
            return _error('unknown_account', 'No such account.')
        key = (session_key or '').strip()
        if not self.w.plausible_key(key):
            return _error('invalid_key', 'That is not a session key: it starts with sk-ant-.')
        try:
            info = self.w.fetch_account_info(key)
        except Exception as e:
            return {'ok': False, 'error': self._explain(e)}
        orgs = {info.get('org_id')} | {c.get('id') for c in info.get('org_choices') or []}
        if (acc.get('org_id') and acc['org_id'] not in orgs) or not self._same_person(acc, info.get('email')):
            # The widget's rule: never store a credential under the name of
            # an account it does not belong to; a colleague's seat in the same
            # organization is another account too.
            who = info.get('email') or info.get('name') or 'another login'
            return _error('wrong_account', f'This key signs in as {who}, not as '
                                           f'{acc.get("email") or acc.get("name") or "this account"}. '
                                           'Nothing was changed.')
        acc['session_key'] = key
        acc.setdefault('org_id', info.get('org_id'))
        self._save()
        return {'ok': True, 'account': self._public(acc)}

    def cmd_rename(self, account_id, name):
        acc = self._find(account_id)
        if acc is None:
            return _error('unknown_account', 'No such account.')
        name = (name or '').strip()
        if not name:
            return _error('invalid_name', 'A name cannot be empty.')
        acc['name'] = name
        self._save()
        return {'ok': True, 'account': self._public(acc)}

    def cmd_remove(self, account_id):
        before = len(self.cfg['accounts'])
        self.cfg['accounts'] = [a for a in self.cfg['accounts'] if a.get('id') != account_id]
        if len(self.cfg['accounts']) == before:
            return _error('unknown_account', 'No such account.')
        self._save()
        return {'ok': True}

    # ── dispatch ─────────────────────────────────────

    def handle(self, request):
        if not isinstance(request, dict):
            return _error('protocol', 'A request is a JSON object.')
        cmd, args = request.get('cmd'), request.get('args') or {}
        handler = getattr(self, 'cmd_' + cmd, None) if isinstance(cmd, str) and cmd.isidentifier() else None
        if handler is None or not isinstance(args, dict):
            return _error('protocol', f'Unknown command {cmd!r}.')
        try:
            return handler(**args)
        except TypeError as e:
            return _error('protocol', f'Bad arguments for {cmd}: {e}')
        except Exception:
            self.w.wlog('BRIDGE failed: ' + self.w._redact(traceback.format_exc()))
            return _error('internal', 'The bridge failed; see bridge.log.')


def _protocol_stream():
    """The real stdout for answers; sys.stdout and fd 1 go to stderr from here,
    so nothing upstream prints can corrupt the protocol."""
    out = os.fdopen(os.dup(1), 'w', encoding='utf-8', newline='\n')
    try:
        os.dup2(2, 1)
    except OSError:
        pass
    sys.stdout = sys.stderr
    return out


def serve(bridge, inp, out):
    for raw in inp:
        raw = raw.strip()
        if not raw:
            continue
        try:
            request = json.loads(raw)
        except ValueError:
            request = None
        answer = bridge.handle(request) if request is not None else _error('protocol', 'Not JSON.')
        answer['id'] = request.get('id') if isinstance(request, dict) else None
        out.write(json.dumps(answer, ensure_ascii=False) + '\n')
        out.flush()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--data-dir', required=True)
    parser.add_argument('--upstream', default=DEFAULT_UPSTREAM)
    parser.add_argument('--once', nargs='+', metavar=('CMD', 'ARGS'))
    opts = parser.parse_args(argv)
    out = _protocol_stream()
    try:
        w = load_upstream(opts.upstream)
        widget_cfg = retarget(w, opts.data_dir)
        bridge = Bridge(w, widget_cfg)
    except Exception:
        # Before upstream is up there is no log of its own to write to.
        out.write(json.dumps(dict(_error('startup', traceback.format_exc(limit=3)), id=None)) + '\n')
        out.flush()
        return 1
    w.wlog(f'BRIDGE started, upstream {getattr(w, "APP_VERSION", "?")}')
    if opts.once:
        args = json.loads(opts.once[1]) if len(opts.once) > 1 else {}
        answer = bridge.handle({'cmd': opts.once[0], 'args': args})
        out.write(json.dumps(answer, ensure_ascii=False, indent=2) + '\n')
        out.flush()
        return 0 if answer.get('ok') else 2
    serve(bridge, sys.stdin, out)
    return 0


if __name__ == '__main__':
    sys.exit(main())
