using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace ClaudeUsageDock.Configuration;

/// <summary>
/// The extension's own settings, on its page in Command Palette settings.
/// The accounts are not here: they live with the bridge (accounts.json), which
/// is the only process that writes them.
/// </summary>
internal sealed class DockSettings : JsonSettingsManager
{
    private readonly TextSetting _python = new(
        "python",
        "Python",
        "The Python that runs the widget's code: a full path to python.exe, or empty to use the py launcher (py -3), then python on the PATH. It needs Pillow (pip install pillow), as the widget does.",
        string.Empty);

    private readonly TextSetting _upstream = new(
        "upstream",
        "Widget source folder",
        "Empty to use the copy of the widget built into this package. Point it at the src folder of a clone of claude-usage-widget to run that code instead, with no rebuild after a merge.",
        string.Empty);

    private readonly ChoiceSetSetting _refresh = new(
        "refreshMinutes",
        "Refresh every",
        "How often each account is read. The Claude Code endpoint is rate limited: with several accounts, keep this at 3 minutes or more.",
        [
            new ChoiceSetSetting.Choice("1 minute", "1"),
            new ChoiceSetSetting.Choice("3 minutes", "3"),
            new ChoiceSetSetting.Choice("5 minutes", "5"),
            new ChoiceSetSetting.Choice("10 minutes", "10"),
            new ChoiceSetSetting.Choice("15 minutes", "15"),
            new ChoiceSetSetting.Choice("30 minutes", "30"),
        ])
    {
        Value = "5",
    };

    private readonly ToggleSetting _model = new(
        "dockModel",
        "Show the per-model weekly limit",
        "Adds a button for the model-specific 7-day limit (Sonnet or Opus, as in the widget) after the session and the week.",
        false);

    public DockSettings()
    {
        FilePath = Path.Combine(Log.Folder, "settings.json");
        Settings.Add(_refresh);
        Settings.Add(_model);
        Settings.Add(_python);
        Settings.Add(_upstream);
        LoadSettings();
        Settings.SettingsChanged += (_, _) =>
        {
            SaveSettings();
            Changed?.Invoke();
        };
    }

    /// <summary>Raised after the user saves the settings form.</summary>
    public event Action? Changed;

    public string Python => (_python.Value ?? string.Empty).Trim().Trim('"');

    public string Upstream => (_upstream.Value ?? string.Empty).Trim().Trim('"');

    public TimeSpan RefreshInterval =>
        TimeSpan.FromMinutes(int.TryParse(_refresh.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : 5);

    public bool ShowModel => _model.Value;
}
