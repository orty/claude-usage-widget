// Out-of-process COM server entry point. Command Palette starts this
// executable with "-RegisterProcessAsComServer" and talks to it over COM,
// as in the official extension template.
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CommandPalette.Extensions;
using Shmuelie.WinRTServer;
using Shmuelie.WinRTServer.CsWinRT;

namespace ClaudeUsageDock;

public static partial class Program
{
    private const string ComServerArgument = "-RegisterProcessAsComServer";

    [MTAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == ComServerArgument)
        {
            RunComServer();
        }
        else
        {
            // Started from the Start menu: this is not an app, say what it is
            // instead of vanishing without a window.
            _ = MessageBoxW(
                IntPtr.Zero,
                "Claude Usage for the Dock is a PowerToys Command Palette extension.\n\n"
                + "Open Command Palette, type \"Claude usage\" to add your accounts, then "
                + "right-click the Dock, choose Edit Dock and add one band per account.",
                "Claude Usage for the Dock",
                0x00000040 /* MB_ICONINFORMATION */);
        }
    }

    private static void RunComServer()
    {
        using ManualResetEvent disposed = new(false);
        ComServer server = new();
        ClaudeUsageDockExtension extension = new(disposed);
        server.RegisterClass<ClaudeUsageDockExtension, IExtension>(() => extension);
        server.Start();
        Log.Line("COM server started");
        disposed.WaitOne();
        Log.Line("extension disposed by the host; stopping");
        server.Stop();
        server.UnsafeDispose();
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
