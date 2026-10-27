"""Shared set-up for the bridge's tests.

On Windows upstream imports as it is. Elsewhere (a Linux container, a Mac) the
three Windows/GUI imports at the top of widget.pyw are stood in for, so the
headless part the bridge uses can still be exercised; nothing here draws.

Every Claude Code path points into a temporary home before upstream is
imported, so no test ever reads or writes a real login.
"""
import importlib
import os
import sys
import tempfile
import types

HERE = os.path.dirname(os.path.abspath(__file__))
BRIDGE_DIR = os.path.dirname(HERE)
REPO = os.path.abspath(os.path.join(BRIDGE_DIR, '..', '..'))
UPSTREAM = os.path.join(REPO, 'src')

HOME = tempfile.mkdtemp(prefix='claude-usage-bridge-test-')
os.environ['CLAUDE_WIDGET_CC_STORE'] = os.path.join(HOME, 'claude-accounts')
os.environ['CLAUDE_WIDGET_CC_CRED'] = os.path.join(HOME, 'credentials.json')
os.environ['CLAUDE_WIDGET_CC_CACHE'] = os.path.join(HOME, 'claude.json')
os.environ.pop('CLAUDE_USAGE_DEV', None)
os.environ['LOCALAPPDATA'] = os.path.join(HOME, 'LocalAppData')
os.makedirs(os.environ['LOCALAPPDATA'], exist_ok=True)


def _stub_module(name, **attrs):
    mod = types.ModuleType(name)

    def __getattr__(attr):  # any class or constant the module body names
        if attr.startswith('__'):
            raise AttributeError(attr)
        return type(attr, (), {'__init__': lambda self, *a, **k: None})

    mod.__getattr__ = __getattr__
    mod.__dict__.update(attrs)
    sys.modules[name] = mod
    return mod


def _stub_windows_imports():
    if sys.platform == 'win32':
        return
    import ctypes
    if not hasattr(ctypes, 'WINFUNCTYPE'):
        ctypes.WINFUNCTYPE = ctypes.CFUNCTYPE
    for name in ('winreg', 'tkinter', 'tkinter.font', 'tkinter.ttk', 'tkinter.colorchooser'):
        if name not in sys.modules:
            _stub_module(name)
    sys.modules['tkinter'].font = sys.modules['tkinter.font']
    try:
        importlib.import_module('PIL.ImageTk')
    except Exception:
        _stub_module('PIL.ImageTk')
        import PIL
        PIL.ImageTk = sys.modules['PIL.ImageTk']


_stub_windows_imports()
if BRIDGE_DIR not in sys.path:
    sys.path.insert(0, BRIDGE_DIR)

import claude_usage_bridge as bridge_mod  # noqa: E402

W = bridge_mod.load_upstream(UPSTREAM)
