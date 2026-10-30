using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeUsageDock.Configuration;
using ClaudeUsageDock.Dock;
using ClaudeUsageDock.Pages;
using ClaudeUsageDock.Usage;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;

namespace ClaudeUsageDock;

/// <summary>
/// The extension's command provider.
///
/// Several accounts at once: every account gets a Dock band of its own (pin
/// as many as you like, on any monitor's Dock), plus one band with every
/// account side by side. A band is an account rather than a configured
/// "instance" because the Dock asks an extension for its bands with no
/// context (GetDockBands takes no argument, and the same band shows on every
/// monitor), so the account is the only thing a band can be told apart by.
/// </summary>
public sealed partial class ClaudeUsageCommandsProvider : CommandProvider, INotifyItemsChanged
{
    private const string ProviderId = "com.claudeusage.dock";
    private const string AllBandId = "com.claudeusage.dock.band.all";

    private readonly DockSettings _settings = new();
    private readonly UsageService _service;
    private readonly AccountsPage _page;
    private readonly ICommandItem[] _topLevel;
    private readonly object _bandsLock = new();
    private readonly Dock.Subscribers<IItemsChangedEventArgs> _itemsChanged = new("bands");
    private volatile Bands _bands = Bands.Empty;

    public ClaudeUsageCommandsProvider()
    {
        Id = ProviderId;
        DisplayName = "Claude Usage";
        Icon = Icons.App;
        Settings = _settings.Settings;
        _service = new UsageService(_settings);
        _page = new AccountsPage(_service);
        _topLevel =
        [
            new CommandItem(_page)
            {
                Title = "Claude usage",
                Subtitle = "Usage limits of your Claude accounts",
            },
        ];

        _service.AccountsChanged += RebuildBands;
        _service.ReadingChanged += id => Render(b => b.RenderAccount(id));
        _service.Ticked += () => Render(b => b.RenderAll());
        _settings.Changed += RebuildBands; // the set of buttons may change
        RebuildBands();
        _service.Start();
    }

    /// <summary>
    /// The host's subscription, kept here rather than in the toolkit's event,
    /// which stops at a dead subscriber (see <see cref="Dock.Subscribers{TArgs}"/>).
    /// </summary>
    event TypedEventHandler<object, IItemsChangedEventArgs>? INotifyItemsChanged.ItemsChanged
    {
        add => _itemsChanged.Add(value);
        remove => _itemsChanged.Remove(value);
    }

    public override ICommandItem[] TopLevelCommands() => _topLevel;

    public override ICommandItem[]? GetDockBands() => _bands.All;

    /// <summary>Lets a pinned band, or a pinned account page, be found again by its id.</summary>
    public override ICommandItem? GetCommandItem(string id) =>
        _bands.All.FirstOrDefault(b => b.Command?.Id == id)
        ?? _topLevel.FirstOrDefault(t => t.Command?.Id == id);

    /// <summary>
    /// Applies an update to the live band items. A dead subscriber (Command
    /// Palette's window restarted while this extension kept running) is
    /// dropped where the item raises its change; anything else that fails
    /// rebuilds the bands and asks the host to fetch them again.
    /// </summary>
    private void Render(Action<Bands> update)
    {
        lock (_bandsLock)
        {
            try
            {
                update(_bands);
            }
            catch (Exception ex)
            {
                Log.Line($"band update failed ({ex.GetType().Name} 0x{ex.HResult:X8}); rebuilding the bands");
                RebuildBandsLocked();
            }
        }
    }

    private void RebuildBands()
    {
        lock (_bandsLock)
        {
            RebuildBandsLocked();
        }
    }

    private void RebuildBandsLocked()
    {
        _bands = Bands.Create(_service, _settings);
        _itemsChanged.Raise(this, new ItemsChangedEventArgs(_topLevel.Length));
    }

    public override void Dispose()
    {
        _service.Dispose();
        base.Dispose();
    }

    /// <summary>One generation of Dock bands, replaced whole when the accounts change.</summary>
    private sealed class Bands
    {
        public static readonly Bands Empty = new([], []);

        private readonly Dictionary<string, List<AccountBandItem>> _items;

        private Bands(ICommandItem[] all, Dictionary<string, List<AccountBandItem>> items)
        {
            All = all;
            _items = items;
        }

        public ICommandItem[] All { get; }

        public static Bands Create(UsageService service, DockSettings settings)
        {
            var items = new Dictionary<string, List<AccountBandItem>>();
            var bands = new List<ICommandItem>();
            var together = new List<IListItem>();
            Segment[] segments = settings.ShowModel
                ? [Segment.Name, Segment.Session, Segment.Weekly, Segment.Model]
                : [Segment.Name, Segment.Session, Segment.Weekly];
            foreach (var account in service.Accounts)
            {
                // The band's id is the account's, so a pinned band stays
                // pinned across restarts and renames.
                var solo = segments.Select(s => new AccountBandItem(account, s, service)).ToList();
                bands.Add(new WrappedDockItem([.. solo], $"com.claudeusage.dock.band.{account.Id}", $"Claude usage: {account.Name}"));
                var inAll = segments.Select(s => new AccountBandItem(account, s, service)).ToList();
                together.AddRange(inAll);
                items[account.Id] = [.. solo, .. inAll];
            }

            if (service.Accounts.Count > 1)
            {
                bands.Add(new WrappedDockItem(together.ToArray(), AllBandId, "Claude usage: every account"));
            }

            return new Bands(bands.ToArray(), items);
        }

        public void RenderAccount(string accountId)
        {
            if (_items.TryGetValue(accountId, out var list))
            {
                foreach (var item in list)
                {
                    item.Render();
                }
            }
        }

        public void RenderAll()
        {
            foreach (var list in _items.Values)
            {
                foreach (var item in list)
                {
                    item.Render();
                }
            }
        }
    }
}
