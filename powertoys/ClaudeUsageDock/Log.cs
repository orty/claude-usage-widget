using System;
using System.IO;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace ClaudeUsageDock;

/// <summary>
/// A small capped log next to the settings: extension.log for this side,
/// bridge.log (upstream's own wlog) for the Python side.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    public static string Folder { get; } = CreateFolder();

    private static string CreateFolder()
    {
        var folder = Utilities.BaseSettingsPath("ClaudeUsageDock");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static void Line(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = Path.Combine(Folder, "extension.log");
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Move(path, path + ".old", overwrite: true);
                }

                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
