// The IExtension the host activates. The [Guid] is the CLSID in
// Package.appxmanifest (com:Class Id and CreateInstance ClassId).
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CommandPalette.Extensions;

namespace ClaudeUsageDock;

[Guid("46E1ADAA-20B3-449C-8C01-AA73036A0555")]
public sealed partial class ClaudeUsageDockExtension : IExtension, IDisposable
{
    private readonly ManualResetEvent _disposed;
    private readonly ClaudeUsageCommandsProvider _provider = new();

    public ClaudeUsageDockExtension(ManualResetEvent disposed)
    {
        _disposed = disposed;
    }

    public object? GetProvider(ProviderType providerType) => providerType switch
    {
        ProviderType.Commands => _provider,
        _ => null,
    };

    public void Dispose()
    {
        _provider.Dispose();
        _disposed.Set();
    }
}
