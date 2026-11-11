// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>Resolves the batch strategies the framework appends to. Inert stub.</summary>
/// <docs>messaging/outbox-pattern#bounded-buffer</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/BatchStrategyResolutionTests.cs</tests>
internal static class BatchStrategyResolution {
  /// <summary>Inert stub.</summary>
  public static IOutboxBatchStrategy? Outbox(IServiceProvider services) => services.GetService<IOutboxBatchStrategy>();

  /// <summary>Inert stub.</summary>
  public static IInboxBatchStrategy? Inbox(IServiceProvider services) => services.GetService<IInboxBatchStrategy>();
}
