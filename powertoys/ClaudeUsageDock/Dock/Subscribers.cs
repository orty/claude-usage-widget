using System;
using Windows.Foundation;

namespace ClaudeUsageDock.Dock;

/// <summary>
/// The handlers of an event Command Palette subscribes to over COM, called
/// one by one. The toolkit raises its events as one multicast call inside a
/// catch-all: when Command Palette's window restarts and this extension keeps
/// running, the dead window's handler stays first in the list, throws, and
/// the new window's handler is never called, with nothing logged. The Dock
/// then shows the numbers of the restart for good. Here a handler whose
/// process is gone is dropped and the others are still called.
/// </summary>
internal sealed class Subscribers<TArgs>
{
    private readonly string _what;
    private readonly object _lock = new();
    private TypedEventHandler<object, TArgs>[] _handlers = [];

    public Subscribers(string what) => _what = what;

    public void Add(TypedEventHandler<object, TArgs>? handler)
    {
        if (handler is null)
        {
            return;
        }

        lock (_lock)
        {
            _handlers = [.. _handlers, handler];
        }
    }

    public void Remove(TypedEventHandler<object, TArgs>? handler)
    {
        lock (_lock)
        {
            var i = Array.IndexOf(_handlers, handler);
            if (i >= 0)
            {
                _handlers = [.. _handlers[..i], .. _handlers[(i + 1)..]];
            }
        }
    }

    public void Raise(object sender, TArgs args)
    {
        foreach (var handler in _handlers)
        {
            try
            {
                handler(sender, args);
            }
            catch (Exception ex) when (IsGone(ex))
            {
                Remove(handler);
                Log.Line($"{_what}: dropped a subscriber whose process is gone (0x{ex.HResult:X8})");
            }
            catch (Exception ex)
            {
                Log.Line($"{_what}: a subscriber failed ({ex.GetType().Name} 0x{ex.HResult:X8})");
            }
        }
    }

    /// <summary>The errors COM answers for a caller that no longer exists.</summary>
    private static bool IsGone(Exception ex) => (uint)ex.HResult
        is 0x80010108 // RPC_E_DISCONNECTED
        or 0x800706BA // RPC_S_SERVER_UNAVAILABLE
        or 0x800706BE // RPC_S_CALL_FAILED
        or 0x800706BF; // RPC_S_CALL_FAILED_DNE
}
