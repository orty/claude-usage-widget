using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ClaudeUsageDock.Bridge;
using ClaudeUsageDock.Configuration;

namespace ClaudeUsageDock.Usage;

/// <summary>
/// The accounts and their latest readings, refreshed on a timer through the
/// bridge. Everything the Dock bands and the pages show comes from here.
/// </summary>
internal sealed partial class UsageService : IDisposable
{
    private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(30);

    private readonly DockSettings _settings;
    private readonly BridgeClient _bridge;
    private readonly ConcurrentDictionary<string, UsageReading> _readings = new();
    private readonly Timer _timer;
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private AccountInfo[] _accounts = [];
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;
    private string? _bridgeError;
    private bool _listed; // the bridge has answered the account list at least once

    public UsageService(DockSettings settings)
    {
        _settings = settings;
        var dataDir = Path.Combine(Log.Folder, "bridge");
        Directory.CreateDirectory(dataDir);
        _bridge = new BridgeClient(() => _settings.Python, () => _settings.Upstream, dataDir);
        _accounts = LoadSnapshot(dataDir);
        _settings.Changed += OnSettingsChanged;
        _timer = new Timer(_ => _ = OnTickAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Starts the readings, once whoever listens to the events is subscribed.</summary>
    public void Start() => _timer.Change(TimeSpan.Zero, TickEvery);

    /// <summary>The account list changed (added, removed, renamed).</summary>
    public event Action? AccountsChanged;

    /// <summary>An account has a new reading.</summary>
    public event Action<string>? ReadingChanged;

    /// <summary>Every 30 s, so the countdowns move between readings.</summary>
    public event Action? Ticked;

    public IReadOnlyList<AccountInfo> Accounts => _accounts;

    /// <summary>The folder of gauge icons the bridge drew, once it has.</summary>
    public string? GaugeDir { get; private set; }

    /// <summary>Why the bridge itself cannot run, when it cannot.</summary>
    public string? BridgeError => _bridgeError;

    public UsageReading? ReadingOf(string accountId) => _readings.GetValueOrDefault(accountId);

    /// <summary>How old numbers may get before a band says when they are from: three missed refreshes.</summary>
    public TimeSpan StaleAfter => _settings.RefreshInterval * 3;

    public Task RefreshAllNowAsync() => RefreshAllAsync(reloadAccounts: true);

    public async Task RefreshAsync(string accountId)
    {
        var answer = await _bridge.CallAsync("usage", new JsonObject { ["account_id"] = accountId }).ConfigureAwait(false);
        Store(accountId, answer);
    }

    // ── account management: each answers with a sentence for a toast ──

    public Task<string> AddKeyAsync(string name, string key, string orgId) =>
        ManageAsync("add_key", new JsonObject { ["name"] = name, ["session_key"] = key, ["org_id"] = orgId },
            a => AccountInfo.Bool(a, "added")
                ? $"Added {Name(a)}. Pin its band from Edit Dock."
                : $"Saved the key for {Name(a)}, which was already listed.");

    /// <summary>
    /// The organizations a key's login can be tracked in, the default first,
    /// or why the key cannot be used.
    /// </summary>
    public async Task<(IReadOnlyList<(string Id, string Name)> Choices, string? Error)> KeyOrgsAsync(string key)
    {
        var answer = await _bridge.CallAsync("key_orgs", new JsonObject { ["session_key"] = key }).ConfigureAwait(false);
        if (!AccountInfo.Bool(answer, "ok"))
        {
            var error = answer["error"] as JsonObject;
            return ([], (error is null ? null : AccountInfo.Str(error, "message")) ?? "The key could not be checked.");
        }

        var choices = (answer["choices"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(c => (AccountInfo.Str(c, "id") ?? string.Empty, AccountInfo.Str(c, "name") ?? string.Empty))
            .Where(c => c.Item1.Length > 0)
            .ToList();
        return (choices, choices.Count == 0 ? "This login has no organization to track." : null);
    }

    public Task<string> AddClaudeCodeAsync(string name) =>
        ManageAsync("add_claude_code", new JsonObject { ["name"] = name },
            a => $"Added {Name(a)}, read with the Claude Code login. Pin its band from Edit Dock.");

    public Task<string> ImportWidgetAsync() =>
        ManageAsync("import_widget", null, a =>
        {
            var added = Names(a["added"]);
            var updated = Names(a["updated"]);
            var needKey = Names(a["need_key"]);
            var text = added.Length + updated.Length == 0
                ? "The widget has no accounts with an organization to import."
                : $"Imported {string.Join(", ", added.Concat(updated))}.";
            return needKey.Length == 0
                ? text
                : text + $" {string.Join(", ", needKey)} need a session key of their own (keys are never copied from the widget).";
        });

    public Task<string> SetKeyAsync(string accountId, string key) =>
        ManageAsync("set_key", new JsonObject { ["account_id"] = accountId, ["session_key"] = key },
            a => $"Saved the new key for {Name(a)}.");

    public Task<string> RenameAsync(string accountId, string name) =>
        ManageAsync("rename", new JsonObject { ["account_id"] = accountId, ["name"] = name },
            a => $"Renamed to {Name(a)}.");

    public Task<string> RemoveAsync(string accountId) =>
        ManageAsync("remove", new JsonObject { ["account_id"] = accountId }, _ => "Removed.");

    private static string Name(JsonObject answer) =>
        AccountInfo.From(answer["account"])?.Name ?? "the account";

    private static string[] Names(JsonNode? node) =>
        (node as JsonArray)?.Select(n => n?.GetValue<string>() ?? string.Empty).Where(n => n.Length > 0).ToArray() ?? [];

    private async Task<string> ManageAsync(string cmd, JsonObject? args, Func<JsonObject, string> success)
    {
        var answer = await _bridge.CallAsync(cmd, args).ConfigureAwait(false);
        if (!AccountInfo.Bool(answer, "ok"))
        {
            var error = answer["error"] as JsonObject;
            var message = (error is null ? null : AccountInfo.Str(error, "message")) ?? "It did not work.";
            if (answer["org_choices"] is JsonArray choices)
            {
                // The login reads several organizations: say which ids to pick from.
                var listed = choices.OfType<JsonObject>()
                    .Select(c => $"{AccountInfo.Str(c, "name")} ({AccountInfo.Str(c, "id")})");
                message += " Its organizations: " + string.Join(", ", listed) + ".";
            }

            return message;
        }

        await ReloadAccountsAsync().ConfigureAwait(false);
        if (AccountInfo.From(answer["account"]) is { } account)
        {
            await RefreshAsync(account.Id).ConfigureAwait(false);
        }
        else
        {
            _ = RefreshAllAsync(reloadAccounts: false);
        }

        return success(answer);
    }

    // ── refresh loop ──

    private async Task OnTickAsync()
    {
        try
        {
            var now = DateTimeOffset.Now;
            var due = now - _lastRefresh >= _settings.RefreshInterval
                // A limit that has just reset is read at once, as the widget
                // does; at most once a minute, should the answer lag the reset.
                || _readings.Values.Any(r => r.Ok && now - r.At > TimeSpan.FromMinutes(1)
                    && new[] { r.Session, r.Weekly, r.Model }.Any(b => b?.ResetsAt is { } at && at <= now && at > r.At));
            if (due)
            {
                await RefreshAllAsync(reloadAccounts: !_listed).ConfigureAwait(false);
            }

            Ticked?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Line($"tick failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task RefreshAllAsync(bool reloadAccounts)
    {
        if (!await _refreshing.WaitAsync(0).ConfigureAwait(false))
        {
            return; // one refresh at a time; the running one covers this ask
        }

        try
        {
            _lastRefresh = DateTimeOffset.Now;
            if (reloadAccounts)
            {
                await ReloadAccountsAsync().ConfigureAwait(false);
            }

            if (GaugeDir is null)
            {
                await LoadGaugesAsync().ConfigureAwait(false);
            }

            foreach (var account in _accounts)
            {
                await RefreshAsync(account.Id).ConfigureAwait(false);
            }

            // One line an hour, so a log shows when readings stopped.
            if (_lastRefresh - _lastHeartbeat >= TimeSpan.FromHours(1))
            {
                _lastHeartbeat = _lastRefresh;
                Log.Line($"refreshed {_accounts.Length} accounts, {_accounts.Count(a => ReadingOf(a.Id)?.Ok == true)} read");
            }
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <summary>
    /// The accounts as the bridge last listed them (it leaves a copy with no
    /// key in it), so the bands exist from the first moment: Command Palette
    /// asks for them before Python is up, and drops a pinned band it cannot
    /// find. The bridge's own list replaces it at the first refresh.
    /// </summary>
    private static AccountInfo[] LoadSnapshot(string dataDir)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(dataDir, "accounts.public.json")));
            return (node?["accounts"] as JsonArray ?? []).Select(AccountInfo.From).OfType<AccountInfo>().ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return []; // first start, or written by an older bridge
        }
    }

    private async Task LoadGaugesAsync()
    {
        var answer = await _bridge.CallAsync("gauges", new JsonObject { ["size"] = 64 }).ConfigureAwait(false);
        if (AccountInfo.Bool(answer, "ok") && AccountInfo.Str(answer, "dir") is { Length: > 0 } dir && Directory.Exists(dir))
        {
            GaugeDir = dir;
        }
        else
        {
            Log.Line("gauges: none, the buttons keep the app icon");
        }
    }

    private async Task ReloadAccountsAsync()
    {
        var answer = await _bridge.CallAsync("accounts").ConfigureAwait(false);
        if (!AccountInfo.Bool(answer, "ok"))
        {
            _bridgeError = (answer["error"] as JsonObject) is { } e ? AccountInfo.Str(e, "message") : "The bridge failed.";
            Log.Line("accounts: " + _bridgeError);
            AccountsChanged?.Invoke();
            return;
        }

        _bridgeError = null;
        _listed = true;
        var accounts = (answer["accounts"] as JsonArray ?? []).Select(AccountInfo.From).OfType<AccountInfo>().ToArray();
        var changed = !accounts.SequenceEqual(_accounts, AccountComparer.Instance);
        _accounts = accounts;
        foreach (var gone in _readings.Keys.Except(accounts.Select(a => a.Id)).ToArray())
        {
            _readings.TryRemove(gone, out _);
        }

        if (changed)
        {
            AccountsChanged?.Invoke();
        }
    }

    private void Store(string accountId, JsonObject answer)
    {
        var reading = UsageReading.From(answer, DateTimeOffset.Now);
        if (!reading.Ok && reading.ErrorKind == "rate_limited" && _readings.TryGetValue(accountId, out var previous)
            && previous.Ok)
        {
            // Paused by a 429: keep showing the last numbers, with the pause said.
            reading = previous with { ErrorKind = null, ErrorMessage = reading.ErrorMessage };
        }

        _readings[accountId] = reading;
        ReadingChanged?.Invoke(accountId);
    }

    private void OnSettingsChanged()
    {
        // The Python or the source folder may have changed: start a new bridge.
        _bridge.Restart();
        _lastRefresh = DateTimeOffset.MinValue;
        Ticked?.Invoke();
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _timer.Dispose();
        _bridge.Dispose();
    }

    /// <summary>Compares what the bands are built from, so a refresh that changes nothing rebuilds nothing.</summary>
    private sealed class AccountComparer : IEqualityComparer<AccountInfo>
    {
        public static readonly AccountComparer Instance = new();

        public bool Equals(AccountInfo? x, AccountInfo? y) =>
            x?.Id == y?.Id && x?.Name == y?.Name && x?.Plan == y?.Plan && x?.Email == y?.Email
            && x?.HasKey == y?.HasKey && x?.ClaudeCode == y?.ClaudeCode;

        public int GetHashCode(AccountInfo obj) => obj.Id.GetHashCode(StringComparison.Ordinal);
    }
}
