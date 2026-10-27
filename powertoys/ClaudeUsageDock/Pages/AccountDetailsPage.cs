using System;
using System.Globalization;
using System.Linq;
using System.Text;
using ClaudeUsageDock.Dock;
using ClaudeUsageDock.Usage;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace ClaudeUsageDock.Pages;

/// <summary>The full view of one account: the three bars, as in the widget's normal mode.</summary>
internal sealed partial class AccountDetailsPage : ContentPage
{
    private readonly string _accountId;
    private readonly UsageService _service;

    public AccountDetailsPage(string accountId, UsageService service)
    {
        _accountId = accountId;
        _service = service;
        Id = $"com.claudeusage.dock.details.{accountId}";
        Name = "Show usage";
        Title = "Claude usage";
        Icon = Icons.App;
        Commands = [new CommandContextItem(new RefreshAccountCommand(accountId, service))];
        if (service.Accounts.FirstOrDefault(a => a.Id == accountId) is { } account)
        {
            Commands = [.. Commands, new CommandContextItem(SetKeyForm.Page(account, service))];
        }
    }

    public override IContent[] GetContent()
    {
        var account = _service.Accounts.FirstOrDefault(a => a.Id == _accountId);
        var reading = _service.ReadingOf(_accountId);
        var now = DateTimeOffset.Now;
        var md = new StringBuilder();
        md.Append("## ").AppendLine(account?.Name ?? "Account removed");
        if (account is not null)
        {
            md.AppendLine();
            md.AppendLine(string.Join(" · ", new[] { account.Email, account.Plan, "read with " + account.HowRead }
                .Where(s => !string.IsNullOrEmpty(s))));
        }

        md.AppendLine();
        if (_service.BridgeError is { } bridgeError)
        {
            md.Append("> **The bridge cannot run.** ").AppendLine(bridgeError);
        }
        else if (reading is null)
        {
            md.AppendLine("_Reading…_");
        }
        else if (!reading.Ok)
        {
            md.Append("> **").Append(UsageFormat.ErrorWord(reading.ErrorKind)).Append(".** ").AppendLine(reading.ErrorMessage);
        }
        else
        {
            Bar(md, "Current session (5 h)", reading.Session, now);
            Bar(md, "All models (7 d)", reading.Weekly, now);
            if (reading.Model is { } model)
            {
                Bar(md, $"{model.Name ?? "Sonnet"} only (7 d)", model, now);
            }

            md.AppendLine().Append("_Updated ").Append(reading.At.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)).AppendLine("_");
            if (reading.ErrorMessage is { } note)
            {
                md.AppendLine().Append("> ").AppendLine(note);
            }
        }

        return [new MarkdownContent(md.ToString())];
    }

    private static void Bar(StringBuilder md, string label, UsageBar? bar, DateTimeOffset now)
    {
        md.Append("**").Append(label).AppendLine("**  ");
        if (bar is null)
        {
            md.AppendLine("not available").AppendLine();
            return;
        }

        md.Append('`').Append(UsageFormat.Meter(bar, 20)).Append("` ").Append(UsageFormat.Percent(bar));
        if (bar.ResetsAt is not null)
        {
            md.Append(" · reset ").Append(UsageFormat.ResetClock(bar.ResetsAt, now))
              .Append(" (").Append(UsageFormat.Until(bar.ResetsAt, now)).Append(')');
        }

        md.AppendLine().AppendLine();
    }
}
