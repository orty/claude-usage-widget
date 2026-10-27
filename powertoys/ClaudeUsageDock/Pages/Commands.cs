using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ClaudeUsageDock.Dock;
using ClaudeUsageDock.Usage;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace ClaudeUsageDock.Pages;

/// <summary>Runs an account operation off the COM thread and says how it went in a toast.</summary>
internal static class Background
{
    public static void Run(Func<Task<string>> work)
    {
        _ = Task.Run(async () =>
        {
            string message;
            try
            {
                message = await work().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Line($"operation failed: {ex.GetType().Name}: {ex.Message}");
                message = "It did not work: " + ex.Message;
            }

            try
            {
                new ToastStatusMessage(message) { Duration = 6000 }.Show();
            }
            catch (Exception ex)
            {
                Log.Line("toast failed: " + ex.Message);
            }
        });
    }

    /// <summary>A form's inputs; null when the payload is not an object.</summary>
    public static JsonObject? Inputs(string payload)
    {
        try
        {
            return JsonNode.Parse(payload) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static string Input(JsonObject inputs, string key) =>
        (inputs[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty).Trim();

    /// <summary>A JSON string literal, for user text placed in a card template.</summary>
    public static string Quote(string text) => JsonValue.Create(text)!.ToJsonString();
}

internal sealed partial class RefreshAccountCommand : InvokableCommand
{
    private readonly string _accountId;
    private readonly UsageService _service;

    public RefreshAccountCommand(string accountId, UsageService service)
    {
        _accountId = accountId;
        _service = service;
        Id = $"com.claudeusage.dock.refresh.{accountId}";
        Name = "Refresh now";
        Icon = Icons.Refresh;
    }

    public override ICommandResult Invoke()
    {
        _ = _service.RefreshAsync(_accountId);
        return CommandResult.KeepOpen();
    }
}

internal sealed partial class RefreshAllCommand : InvokableCommand
{
    private readonly UsageService _service;

    public RefreshAllCommand(UsageService service)
    {
        _service = service;
        Id = "com.claudeusage.dock.refreshAll";
        Name = "Refresh all accounts";
        Icon = Icons.Refresh;
    }

    public override ICommandResult Invoke()
    {
        _ = _service.RefreshAllNowAsync();
        return CommandResult.KeepOpen();
    }
}

internal sealed partial class AddClaudeCodeCommand : InvokableCommand
{
    private readonly UsageService _service;

    public AddClaudeCodeCommand(UsageService service)
    {
        _service = service;
        Id = "com.claudeusage.dock.addClaudeCode";
        Name = "Add";
        Icon = Icons.Terminal;
    }

    public override ICommandResult Invoke()
    {
        Background.Run(() => _service.AddClaudeCodeAsync(string.Empty));
        return CommandResult.KeepOpen();
    }
}

internal sealed partial class ImportWidgetCommand : InvokableCommand
{
    private readonly UsageService _service;

    public ImportWidgetCommand(UsageService service)
    {
        _service = service;
        Id = "com.claudeusage.dock.importWidget";
        Name = "Import";
        Icon = Icons.Import;
    }

    public override ICommandResult Invoke()
    {
        Background.Run(_service.ImportWidgetAsync);
        return CommandResult.KeepOpen();
    }
}

internal sealed partial class RemoveAccountCommand : InvokableCommand
{
    private readonly AccountInfo _account;
    private readonly UsageService _service;

    public RemoveAccountCommand(AccountInfo account, UsageService service)
    {
        _account = account;
        _service = service;
        Id = $"com.claudeusage.dock.remove.{account.Id}";
        Name = "Remove account";
        Icon = Icons.Delete;
    }

    public override ICommandResult Invoke() => CommandResult.Confirm(new ConfirmationArgs
    {
        Title = $"Remove {_account.Name}?",
        Description = "Its band leaves the Dock and its session key, if any, is deleted from this extension. "
            + "The Claude Usage widget and Claude Code are not touched.",
        IsPrimaryCommandCritical = true,
        PrimaryCommand = new AnonymousCommand(() => Background.Run(() => _service.RemoveAsync(_account.Id)))
        {
            Name = "Remove",
            Result = CommandResult.GoBack(),
        },
    });
}

/// <summary>
/// Opens an account's page in Command Palette's own window. The Dock shows a
/// page a band item opens directly in a popup that it places itself, and on
/// some setups places partly off-screen; the main window is always on screen.
/// </summary>
internal sealed partial class OpenAccountCommand : InvokableCommand
{
    private readonly string _pageId;

    public OpenAccountCommand(string accountId)
    {
        _pageId = AccountDetailsPage.IdFor(accountId);
        Id = $"com.claudeusage.dock.open.{accountId}";
        Name = "Show usage";
        Icon = Icons.App;
    }

    public override ICommandResult Invoke() =>
        CommandResult.GoToPage(new GoToPageArgs { PageId = _pageId, NavigationMode = NavigationMode.Push });
}
