using System;
using System.Collections.Generic;
using ClaudeUsageDock.Dock;
using ClaudeUsageDock.Usage;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace ClaudeUsageDock.Pages;

/// <summary>
/// "Claude usage" in Command Palette: every account with its numbers, and the
/// way to add, import, rename, re-key and remove them.
/// </summary>
internal sealed partial class AccountsPage : ListPage
{
    private readonly UsageService _service;
    private readonly FormPage _addKey;
    private readonly AddClaudeCodeCommand _addClaudeCode;
    private readonly ImportWidgetCommand _import;
    private readonly RefreshAllCommand _refreshAll;

    public AccountsPage(UsageService service)
    {
        _service = service;
        Id = "com.claudeusage.dock.accounts";
        Name = "Open";
        Title = "Claude usage";
        Icon = Icons.App;
        PlaceholderText = "Search accounts";
        _addKey = new FormPage("com.claudeusage.dock.addKey", "Add", "Add an account with a session key",
            Icons.Key, new AddKeyForm(service));
        _addClaudeCode = new AddClaudeCodeCommand(service);
        _import = new ImportWidgetCommand(service);
        _refreshAll = new RefreshAllCommand(service);
        _service.AccountsChanged += Changed;
        _service.ReadingChanged += _ => Changed();
    }

    private void Changed()
    {
        try
        {
            RaiseItemsChanged();
        }
        catch (Exception ex)
        {
            // The host went away (Command Palette restarted); it asks again when shown.
            Log.Line($"accounts page update failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
    }

    public override IListItem[] GetItems()
    {
        var items = new List<IListItem>();
        var now = DateTimeOffset.Now;
        if (_service.BridgeError is { } error)
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = "The widget's code cannot run",
                Subtitle = error,
                Icon = new IconInfo(""),
            });
        }

        foreach (var account in _service.Accounts)
        {
            var reading = _service.ReadingOf(account.Id);
            items.Add(new ListItem(new AccountDetailsPage(account.Id, _service))
            {
                Title = account.Name,
                Subtitle = Summary(account, reading, now),
                Icon = Icons.App,
                Tags = reading is { Ok: true }
                    ? [new Tag($"5h {UsageFormat.Percent(reading.Session)}"), new Tag($"7d {UsageFormat.Percent(reading.Weekly)}")]
                    : [],
                Section = "Accounts",
                MoreCommands =
                [
                    new CommandContextItem(new RefreshAccountCommand(account.Id, _service)),
                    new CommandContextItem(new FormPage($"com.claudeusage.dock.rename.{account.Id}", "Rename",
                        "Rename " + account.Name, Icons.Rename, new RenameForm(account, _service))),
                    new CommandContextItem(new FormPage($"com.claudeusage.dock.setKey.{account.Id}",
                        account.HasKey ? "Replace session key" : "Add session key",
                        "Session key for " + account.Name, Icons.Key, new SetKeyForm(account, _service))),
                    new CommandContextItem(new RemoveAccountCommand(account, _service)) { IsCritical = true },
                ],
            });
        }

        items.Add(new ListItem(_addKey)
        {
            Title = "Add an account with a session key",
            Subtitle = "A claude.ai login, from the browser",
            Section = "Add accounts",
        });
        items.Add(new ListItem(_addClaudeCode)
        {
            Title = "Add the account Claude Code is signed in as",
            Subtitle = "Reads with Claude Code's own login on this PC: nothing to paste",
            Section = "Add accounts",
        });
        items.Add(new ListItem(_import)
        {
            Title = "Import accounts from the Claude Usage widget",
            Subtitle = "Names and organizations; session keys stay with the widget",
            Section = "Add accounts",
        });
        items.Add(new ListItem(_refreshAll) { Title = "Refresh all accounts", Section = "Add accounts" });
        return items.ToArray();
    }

    private static string Summary(AccountInfo account, UsageReading? reading, DateTimeOffset now)
    {
        if (reading is null)
        {
            return "reading…";
        }

        if (!reading.Ok)
        {
            return reading.ErrorMessage ?? UsageFormat.ErrorWord(reading.ErrorKind);
        }

        var model = reading.Model is null ? string.Empty
            : $" · {reading.Model.Name ?? "Sonnet"} {UsageFormat.Percent(reading.Model)}";
        var reset = reading.Session?.ResetsAt is null ? string.Empty
            : $" · session resets in {UsageFormat.Until(reading.Session.ResetsAt, now)}";
        return $"{account.Plan}{(account.Plan.Length > 0 ? " · " : string.Empty)}5h {UsageFormat.Percent(reading.Session)} · 7d {UsageFormat.Percent(reading.Weekly)}{model}{reset}";
    }
}
