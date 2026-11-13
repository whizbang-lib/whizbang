// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// The one way the framework resolves the outbox and inbox batch strategies it appends to (#2220). A strategy registered
/// through the pipeline is already bounded; one a consumer registered directly as <see cref="IOutboxBatchStrategy"/> or
/// <see cref="IInboxBatchStrategy"/> is wrapped here, once per instance, so it is bounded too and nobody has to remember
/// to wrap it. A host without the batch buffer (no worker pipeline) gets the registered strategy as it is.
/// </summary>
/// <docs>messaging/outbox-pattern#bounded-buffer</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/BatchStrategyResolutionTests.cs</tests>
internal static class BatchStrategyResolution {
  /// <summary>The outbox batch strategy to append to, bounded; null when none is registered.</summary>
  public static IOutboxBatchStrategy? Outbox(IServiceProvider services) {
    var registered = services.GetService<IOutboxBatchStrategy>();
    if (registered is null or BoundedOutboxBatchStrategy) {
      return registered;
    }
    return services.GetService<BoundedStrategyWrappers>()?.Outbox(registered) ?? registered;
  }

  /// <summary>The inbox batch strategy to append to, bounded; null when none is registered.</summary>
  public static IInboxBatchStrategy? Inbox(IServiceProvider services) {
    var registered = services.GetService<IInboxBatchStrategy>();
    if (registered is null or BoundedInboxBatchStrategy) {
      return registered;
    }
    return services.GetService<BoundedStrategyWrappers>()?.Inbox(registered) ?? registered;
  }
}

/// <summary>The bounded wrapper of each directly registered strategy instance, made once and kept with the instance.</summary>
/// <param name="buffer">The process-wide batch buffer.</param>
internal sealed class BoundedStrategyWrappers(BatchBuffer buffer) {
  private readonly ConditionalWeakTable<IOutboxBatchStrategy, BoundedOutboxBatchStrategy> _outbox = [];
  private readonly ConditionalWeakTable<IInboxBatchStrategy, BoundedInboxBatchStrategy> _inbox = [];

  /// <summary>The bounded wrapper of <paramref name="inner"/>.</summary>
  public BoundedOutboxBatchStrategy Outbox(IOutboxBatchStrategy inner) =>
    _outbox.GetValue(inner, strategy => new BoundedOutboxBatchStrategy(strategy, buffer));

  /// <summary>The bounded wrapper of <paramref name="inner"/>.</summary>
  public BoundedInboxBatchStrategy Inbox(IInboxBatchStrategy inner) =>
    _inbox.GetValue(inner, strategy => new BoundedInboxBatchStrategy(strategy, buffer));
}
