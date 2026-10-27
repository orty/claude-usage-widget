using System;
using ClaudeUsageDock.Configuration;
using ClaudeUsageDock.Pages;
using ClaudeUsageDock.Usage;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace ClaudeUsageDock.Dock;

/// <summary>What one button of an account's band shows.</summary>
internal enum Segment
{
    /// <summary>The account's name, with the icon.</summary>
    Name,

    /// <summary>The 5-hour session: % used and the time left before it resets.</summary>
    Session,

    /// <summary>The 7-day limit: % used and the time left before it resets.</summary>
    Weekly,

    /// <summary>The per-model 7-day limit.</summary>
    Model,
}

/// <summary>
/// One button of an account's Dock band. The Dock draws a band item's Title
/// and Subtitle as text and repaints when they change (the toolkit raises
/// PropChanged on every assignment), which is how the built-in clock band
/// stays live; these items do the same with the usage.
///
/// An account is several buttons rather than one because the Dock decides a
/// button's width, not the extension (nothing in the SDK sets it), and cuts
/// a long title short; in its Compact size it also hides subtitles. One
/// short title per figure keeps every figure on screen, the way the widget
/// shows the session and the week side by side, each with its reset.
/// Selecting any of them opens the account's page in Command Palette's window.
/// </summary>
internal sealed partial class AccountBandItem : ListItem
{
    private readonly UsageService _service;

    public AccountBandItem(AccountInfo account, Segment segment, UsageService service)
        : base(new OpenAccountCommand(account.Id))
    {
        _service = service;
        Account = account;
        Segment = segment;
        if (segment == Segment.Name)
        {
            Icon = Icons.App;
        }

        MoreCommands =
        [
            new CommandContextItem(new RefreshAccountCommand(account.Id, service)),
            new CommandContextItem(new AccountDetailsPage(account.Id, service)) { Title = "Show usage here (Dock popup)" },
            new CommandContextItem(SetKeyForm.Page(account, service)),
            new CommandContextItem(new OpenUrlCommand("https://claude.ai/settings/usage") { Name = "Open usage on claude.ai" }),
        ];
        Render();
    }

    public AccountInfo Account { get; }

    public Segment Segment { get; }

    /// <summary>Writes the latest reading on the button, touching only what changed.</summary>
    public void Render()
    {
        var reading = _service.ReadingOf(Account.Id);
        var now = DateTimeOffset.Now;
        var (title, subtitle) = Segment switch
        {
            Segment.Name => (Account.Name, Account.Plan),
            _ when reading is null => (Segment == Segment.Session ? (_service.BridgeError is null ? "…" : "bridge error") : Label(Segment) + " –", string.Empty),
            _ when !reading.Ok => (Segment == Segment.Session ? UsageFormat.ErrorWord(reading.ErrorKind) : Label(Segment) + " –", string.Empty),
            Segment.Session => Figure(reading.Session, now),
            Segment.Weekly => Figure(reading.Weekly, now),
            _ => Figure(reading.Model, now),
        };

        if (Title != title)
        {
            Title = title;
        }

        if (Subtitle != subtitle)
        {
            Subtitle = subtitle;
        }

        (string, string) Figure(UsageBar? bar, DateTimeOffset at)
        {
            var left = UsageFormat.Until(bar?.ResetsAt, at).Replace(" ", string.Empty, StringComparison.Ordinal);
            var text = $"{Label(Segment, bar)} {UsageFormat.Percent(bar)}";
            return (left.Length == 0 ? text : $"{text} · {left}",
                bar?.ResetsAt is null ? string.Empty : "resets " + UsageFormat.ResetClock(bar.ResetsAt, at));
        }
    }

    private static string Label(Segment segment, UsageBar? bar = null) => segment switch
    {
        Segment.Session => "5h",
        Segment.Weekly => "7d",
        _ => bar?.Name is { Length: > 0 } name ? name : "Model",
    };
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
