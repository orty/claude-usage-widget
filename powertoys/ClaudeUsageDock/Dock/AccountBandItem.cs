using System;
using ClaudeUsageDock.Configuration;
using ClaudeUsageDock.Pages;
using ClaudeUsageDock.Usage;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace ClaudeUsageDock.Dock;

/// <summary>
/// One account on the Dock. The Dock draws a band item's Title and Subtitle
/// as text and repaints when they change (the toolkit raises PropChanged on
/// every assignment), which is how the built-in clock band stays live; this
/// item does the same with the usage. Selecting it opens the account's page.
/// </summary>
internal sealed partial class AccountBandItem : ListItem
{
    private readonly UsageService _service;
    private readonly DockSettings _settings;

    public AccountBandItem(AccountInfo account, UsageService service, DockSettings settings)
        : base(new AccountDetailsPage(account.Id, service))
    {
        _service = service;
        _settings = settings;
        Account = account;
        Icon = Icons.App;
        MoreCommands =
        [
            new CommandContextItem(new RefreshAccountCommand(account.Id, service)),
            new CommandContextItem(new OpenUrlCommand("https://claude.ai/settings/usage") { Name = "Open usage on claude.ai" }),
        ];
        Render();
    }

    public AccountInfo Account { get; }

    /// <summary>Writes the latest reading on the band, touching only what changed.</summary>
    public void Render()
    {
        var reading = _service.ReadingOf(Account.Id);
        var now = DateTimeOffset.Now;
        string title, subtitle;
        if (reading is null)
        {
            title = $"{Account.Name} …";
            subtitle = _service.BridgeError is null ? "reading…" : "bridge error";
        }
        else if (!reading.Ok)
        {
            title = $"{Account.Name} {UsageFormat.ErrorWord(reading.ErrorKind)}";
            subtitle = reading.ErrorMessage ?? string.Empty;
        }
        else
        {
            title = _settings.Title switch
            {
                TitleStyle.Meter => $"{Account.Name} {UsageFormat.Meter(reading.Session)} {UsageFormat.Percent(reading.Session)}",
                TitleStyle.All => $"{Account.Name} {UsageFormat.Percent(reading.Session)} · {UsageFormat.Percent(reading.Weekly)}"
                    + (reading.Model is null ? string.Empty : $" · {UsageFormat.Percent(reading.Model)}"),
                _ => $"{Account.Name} {UsageFormat.Percent(reading.Session)}",
            };
            subtitle = _settings.Subtitle switch
            {
                SubtitleStyle.Weekly => $"7d {UsageFormat.Percent(reading.Weekly)}",
                SubtitleStyle.None => string.Empty,
                _ => reading.Session?.ResetsAt is null ? string.Empty
                    : $"resets in {UsageFormat.Until(reading.Session.ResetsAt, now)}",
            };
        }

        if (Title != title)
        {
            Title = title;
        }

        if (Subtitle != subtitle)
        {
            Subtitle = subtitle;
        }
    }
}

internal static class Icons
{
    public static readonly IconInfo App = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
    public static readonly IconInfo Refresh = new("");
    public static readonly IconInfo Add = new("");
    public static readonly IconInfo Key = new("");
    public static readonly IconInfo Rename = new("");
    public static readonly IconInfo Delete = new("");
    public static readonly IconInfo Import = new("");
    public static readonly IconInfo Terminal = new("");
}
