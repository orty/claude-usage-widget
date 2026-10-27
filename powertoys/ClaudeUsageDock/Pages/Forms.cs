using ClaudeUsageDock.Dock;
using ClaudeUsageDock.Usage;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace ClaudeUsageDock.Pages;

/// <summary>A page holding one form.</summary>
internal sealed partial class FormPage : ContentPage
{
    private readonly FormContent _form;

    public FormPage(string id, string name, string title, IconInfo icon, FormContent form)
    {
        _form = form;
        Id = id;
        Name = name;
        Title = title;
        Icon = icon;
    }

    public override IContent[] GetContent() => [_form];
}

/// <summary>A new account read with a session key from the browser.</summary>
internal sealed partial class AddKeyForm : FormContent
{
    private readonly UsageService _service;

    public AddKeyForm(UsageService service)
    {
        _service = service;
        TemplateJson = """
        {
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "type": "AdaptiveCard",
          "version": "1.5",
          "body": [
            { "type": "TextBlock", "text": "Add a Claude account with a session key", "weight": "bolder", "size": "medium", "wrap": true },
            { "type": "TextBlock", "wrap": true, "isSubtle": true,
              "text": "Copy the sessionKey cookie of claude.ai with the Claude Session Key browser extension, or from the browser's DevTools (Application > Cookies). Use a key of its own: claude.ai renews a key as it is used and retires the old one, so a key shared with the Claude Usage widget stops working for one of the two." },
            { "type": "Input.Text", "id": "name", "label": "Name on the Dock", "placeholder": "Work, Personal… (empty: the account's name)", "maxLength": 40 },
            { "type": "Input.Text", "id": "sessionKey", "label": "Session key", "style": "password", "placeholder": "sk-ant-…", "isRequired": true, "errorMessage": "Paste the session key" },
            { "type": "Input.Text", "id": "orgId", "label": "Organization id (only if asked)", "placeholder": "Leave empty: the one claude.ai used last" }
          ],
          "actions": [ { "type": "Action.Submit", "title": "Add account" } ]
        }
        """;
    }

    public override ICommandResult SubmitForm(string inputs)
    {
        if (Background.Inputs(inputs) is not { } form)
        {
            return CommandResult.KeepOpen();
        }

        var name = Background.Input(form, "name");
        var key = Background.Input(form, "sessionKey");
        var org = Background.Input(form, "orgId");
        Background.Run(() => _service.AddKeyAsync(name, key, org));
        return CommandResult.GoBack();
    }
}

/// <summary>A new session key for an existing account.</summary>
internal sealed partial class SetKeyForm : FormContent
{
    private readonly string _accountId;
    private readonly UsageService _service;

    /// <summary>The form as a page, the same everywhere it is offered.</summary>
    public static FormPage Page(AccountInfo account, UsageService service) =>
        new($"com.claudeusage.dock.setKey.{account.Id}",
            account.HasKey ? "Replace session key" : "Add session key",
            "Session key for " + account.Name, Icons.Key, new SetKeyForm(account, service));

    public SetKeyForm(AccountInfo account, UsageService service)
    {
        _accountId = account.Id;
        _service = service;
        TemplateJson = $$"""
        {
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "type": "AdaptiveCard",
          "version": "1.5",
          "body": [
            { "type": "TextBlock", "text": {{Background.Quote("New session key for " + account.Name)}}, "weight": "bolder", "size": "medium", "wrap": true },
            { "type": "TextBlock", "wrap": true, "isSubtle": true, "text": "Copy the sessionKey cookie of claude.ai with the Claude Session Key browser extension, or from the browser's DevTools (Application > Cookies). Use a key of its own, not the widget's: claude.ai renews a key as it is used and retires the old one, so a shared key stops working for one of the two. Sign in again, or in another browser profile, to get a second one. The key is checked against the account first: a key of another account is refused." },
            { "type": "Input.Text", "id": "sessionKey", "label": "Session key", "style": "password", "placeholder": "sk-ant-…", "isRequired": true, "errorMessage": "Paste the session key" }
          ],
          "actions": [ { "type": "Action.Submit", "title": "Save key" } ]
        }
        """;
    }

    public override ICommandResult SubmitForm(string inputs)
    {
        if (Background.Inputs(inputs) is not { } form)
        {
            return CommandResult.KeepOpen();
        }

        var key = Background.Input(form, "sessionKey");
        Background.Run(() => _service.SetKeyAsync(_accountId, key));
        return CommandResult.GoBack();
    }
}

/// <summary>The name an account carries on its band.</summary>
internal sealed partial class RenameForm : FormContent
{
    private readonly string _accountId;
    private readonly UsageService _service;

    public RenameForm(AccountInfo account, UsageService service)
    {
        _accountId = account.Id;
        _service = service;
        TemplateJson = $$"""
        {
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "type": "AdaptiveCard",
          "version": "1.5",
          "body": [
            { "type": "Input.Text", "id": "name", "label": "Name on the Dock", "value": {{Background.Quote(account.Name)}}, "maxLength": 40, "isRequired": true, "errorMessage": "A name cannot be empty" }
          ],
          "actions": [ { "type": "Action.Submit", "title": "Rename" } ]
        }
        """;
    }

    public override ICommandResult SubmitForm(string inputs)
    {
        if (Background.Inputs(inputs) is not { } form)
        {
            return CommandResult.KeepOpen();
        }

        var name = Background.Input(form, "name");
        Background.Run(() => _service.RenameAsync(_accountId, name));
        return CommandResult.GoBack();
    }
}
