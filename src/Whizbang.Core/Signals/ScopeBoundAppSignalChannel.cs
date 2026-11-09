// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Notifications.AppSignals;

namespace Whizbang.Core.Signals;

/// <summary>
/// The application's <see cref="IAppSignalChannel"/>, resolved per provider over the one <see cref="SignalBus"/> (#2076).
/// Resolved in a scope, a publish carries the first open transaction the scope's <see cref="IAppSignalTransactionSource"/>s
/// report, and the topic transports send the notification inside it, so it is delivered only on commit. Resolved from the
/// root (the root provider itself, or as a singleton's dependency), it reads no scope and a publish goes out at once.
/// </summary>
/// <remarks>
/// <para>
/// The root is recognized by comparing the provider that resolved this channel with the one a singleton factory receives,
/// held by <see cref="AppSignalRootProvider"/>. Both are the container's root scope; a singleton's dependencies are always
/// resolved from it, even when the singleton is first built inside a scope. <c>ScopeBoundAppSignalChannelTests</c> fails if
/// a container release ever changes that, in either direction.
/// </para>
/// <para>
/// Subscriptions are not tied to a scope: they go to the bus, as before.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/app-signals#transactional-publish</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Signals/ScopeBoundAppSignalChannelTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AppSignalTransactionalPublishTests.cs</tests>
internal sealed class ScopeBoundAppSignalChannel : IAppSignalChannel {
  private readonly SignalBus _bus;
  private readonly IServiceProvider? _scope;

  /// <summary>Binds a channel to <paramref name="resolvingProvider"/>, or to no scope when that provider is the root.</summary>
  internal ScopeBoundAppSignalChannel(SignalBus bus, IServiceProvider resolvingProvider, AppSignalRootProvider root) {
    _bus = bus;
    _scope = ReferenceEquals(resolvingProvider, root.Provider) ? null : resolvingProvider;
  }

  /// <summary>True when this channel was resolved in a scope and reads that scope's transaction.</summary>
  internal bool IsScopeBound => _scope is not null;

  /// <inheritdoc />
  public Task PublishAsync(string topic, string payload, CancellationToken cancellationToken = default) =>
    _bus.PublishTopicAsync(topic, payload, _openTransaction(), cancellationToken);

  /// <inheritdoc />
  public IDisposable Subscribe(string topic, Func<string, CancellationToken, Task> handler) =>
    ((IAppSignalChannel)_bus).Subscribe(topic, handler);

  private DbTransaction? _openTransaction() {
    if (_scope is null) {
      return null;
    }
    foreach (var source in _scope.GetServices<IAppSignalTransactionSource>()) {
      if (source.CurrentTransaction is { } transaction) {
        return transaction;
      }
    }
    return null;
  }
}

/// <summary>The container's root provider, as a singleton factory receives it: what <see cref="ScopeBoundAppSignalChannel"/> compares against.</summary>
/// <param name="Provider">The root provider.</param>
internal sealed record AppSignalRootProvider(IServiceProvider Provider);
