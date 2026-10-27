using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeUsageDock.Bridge;

/// <summary>
/// The Python process that runs upstream's own code (powertoys/bridge).
/// One per extension, kept alive for its lifetime so upstream's in-memory
/// reuse and rate-limit pause survive between refreshes; started again on the
/// next call if it dies. Calls are one at a time: the bridge answers in order
/// and upstream's caches are not built for concurrent callers.
/// </summary>
internal sealed partial class BridgeClient : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Func<string> _python;
    private readonly Func<string> _upstream;
    private readonly string _dataDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private long _nextId;
    private bool _disposed;

    public BridgeClient(Func<string> python, Func<string> upstream, string dataDir)
    {
        _python = python;
        _upstream = upstream;
        _dataDir = dataDir;
    }

    public static string PackagedBridgeDir => Path.Combine(AppContext.BaseDirectory, "bridge");

    /// <summary>
    /// Sends one command and returns the bridge's answer. Never throws for a
    /// bridge that cannot start or answer: that comes back as
    /// {"ok": false, "error": {"kind": "bridge", ...}} like any other failure.
    /// </summary>
    public async Task<JsonObject> CallAsync(string cmd, JsonObject? args = null, TimeSpan? timeout = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return Failure("The extension is shutting down.");
            }

            var process = EnsureStarted();
            var id = Interlocked.Increment(ref _nextId);
            var request = new JsonObject { ["id"] = id, ["cmd"] = cmd };
            if (args is not null)
            {
                request["args"] = args;
            }

            await process.StandardInput.WriteLineAsync(request.ToJsonString()).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);

            // A refresh can take a while: upstream's curl has a 20 s timeout
            // and retries a transient failure once.
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(90));
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    Stop();
                    return Failure("The bridge stopped. See bridge.log and extension.log in " + _dataDir);
                }

                if (JsonNode.Parse(line) is not JsonObject answer)
                {
                    continue;
                }

                var answerId = answer["id"];
                if (answerId is null && answer["error"]?["kind"]?.GetValue<string>() == "startup")
                {
                    // The bridge could not load upstream (no Pillow, wrong
                    // folder...) and exits: report what it said.
                    Log.Line("bridge failed to start: " + answer["error"]?["message"]?.GetValue<string>());
                    Stop();
                    return Failure("The widget's code could not be loaded by Python: "
                        + (answer["error"]?["message"]?.GetValue<string>() ?? "unknown error"));
                }

                if (answerId is not null && answerId.GetValueKind() == System.Text.Json.JsonValueKind.Number
                    && answerId.GetValue<long>() == id)
                {
                    return answer;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Line($"bridge did not answer {cmd} in time; restarting it");
            Stop();
            return Failure("The bridge did not answer in time.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception
                                       or System.Text.Json.JsonException)
        {
            Log.Line($"bridge call {cmd} failed: {ex.GetType().Name}: {ex.Message}");
            Stop();
            return Failure(ex is System.ComponentModel.Win32Exception
                ? "Python could not be started. Install Python 3 (with Pillow) or set its path in the extension's settings."
                : "The bridge failed: " + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops the bridge; the next call starts a fresh one (after a settings change).</summary>
    public void Restart()
    {
        _gate.Wait();
        try
        {
            Stop();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static JsonObject Failure(string message) => new()
    {
        ["ok"] = false,
        ["error"] = new JsonObject { ["kind"] = "bridge", ["message"] = message },
    };

    private Process EnsureStarted()
    {
        if (_process is { HasExited: false })
        {
            return _process;
        }

        _process?.Dispose();
        var (exe, prefix) = ResolvePython(_python());
        var bridgeDir = PackagedBridgeDir;
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = _dataDir,
        };
        foreach (var arg in prefix)
        {
            info.ArgumentList.Add(arg);
        }

        info.ArgumentList.Add("-X");
        info.ArgumentList.Add("utf8");
        info.ArgumentList.Add(Path.Combine(bridgeDir, "claude_usage_bridge.py"));
        info.ArgumentList.Add("--data-dir");
        info.ArgumentList.Add(_dataDir);
        var upstream = _upstream();
        info.ArgumentList.Add("--upstream");
        info.ArgumentList.Add(string.IsNullOrEmpty(upstream) ? Path.Combine(bridgeDir, "upstream") : upstream);
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        // Upstream's development mode points every Claude Code path at an
        // empty temporary home: never inherit it by accident.
        info.Environment.Remove("CLAUDE_USAGE_DEV");

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                Log.Line("bridge stderr: " + e.Data);
            }
        };
        process.Start();
        process.BeginErrorReadLine();
        Log.Line($"bridge started: {exe} (pid {process.Id})");
        _process = process;
        return process;
    }

    /// <summary>
    /// The Python to run: the setting if there is one, else the py launcher,
    /// else python on the PATH. pythonw is not used: it has no standard streams.
    /// </summary>
    private static (string Exe, string[] Prefix) ResolvePython(string configured)
    {
        if (!string.IsNullOrEmpty(configured))
        {
            return (configured, []);
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            try
            {
                if (dir.Length > 0 && File.Exists(Path.Combine(dir, "py.exe")))
                {
                    return (Path.Combine(dir, "py.exe"), ["-3"]);
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }

        return ("python.exe", []);
    }

    private void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                // Closing stdin ends the bridge's loop; kill it if it lingers.
                process.StandardInput.Close();
                if (!process.WaitForExit(2000))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // The gate is left to the finalizer: a call still in flight releases it.
        _disposed = true;
        Stop();
    }
}
