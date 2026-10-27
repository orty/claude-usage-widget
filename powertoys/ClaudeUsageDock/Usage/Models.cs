using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace ClaudeUsageDock.Usage;

/// <summary>An account as the bridge lists it. It never carries a key or a token.</summary>
internal sealed record AccountInfo(
    string Id,
    string Name,
    string Email,
    string Plan,
    bool ClaudeCode,
    bool HasKey,
    string[] Methods)
{
    public static AccountInfo? From(JsonNode? node)
    {
        if (node is not JsonObject o || Str(o, "id") is not { Length: > 0 } id)
        {
            return null;
        }

        var methods = (o["methods"] as JsonArray)?.Select(m => m?.GetValue<string>() ?? string.Empty)
            .Where(m => m.Length > 0).ToArray() ?? [];
        return new AccountInfo(id, Str(o, "name") ?? "Claude", Str(o, "email") ?? string.Empty,
            Str(o, "plan") ?? string.Empty, Bool(o, "cc_linked"), Bool(o, "has_key"), methods);
    }

    /// <summary>How the account is read, in words.</summary>
    public string HowRead => Methods.Length == 0
        ? "no way to read it yet: add a session key"
        : string.Join(", then ", Methods.Select(m => m == "claude_code" ? "Claude Code login" : "session key"));

    internal static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static bool Bool(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}

/// <summary>One limit: how much of it is used and when it resets.</summary>
internal sealed record UsageBar(double? Percent, DateTimeOffset? ResetsAt, string? Name)
{
    public static UsageBar? From(JsonNode? node)
    {
        if (node is not JsonObject o)
        {
            return null;
        }

        double? pct = o["pct"] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
        DateTimeOffset? resets = AccountInfo.Str(o, "resets_at") is { Length: > 0 } s
            && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
            ? when
            : null;
        return new UsageBar(pct, resets, AccountInfo.Str(o, "name"));
    }
}

/// <summary>A reading of one account: the three bars, or why there are none.</summary>
internal sealed record UsageReading(
    UsageBar? Session,
    UsageBar? Weekly,
    UsageBar? Model,
    string? ErrorKind,
    string? ErrorMessage,
    DateTimeOffset At)
{
    public bool Ok => ErrorKind is null;

    public static UsageReading From(JsonObject answer, DateTimeOffset at)
    {
        if (AccountInfo.Bool(answer, "ok"))
        {
            return new UsageReading(UsageBar.From(answer["session"]), UsageBar.From(answer["weekly"]),
                UsageBar.From(answer["model"]), null, null, at);
        }

        var error = answer["error"] as JsonObject;
        return new UsageReading(null, null, null,
            (error is null ? null : AccountInfo.Str(error, "kind")) ?? "error",
            (error is null ? null : AccountInfo.Str(error, "message")) ?? "Unknown error",
            at);
    }
}
