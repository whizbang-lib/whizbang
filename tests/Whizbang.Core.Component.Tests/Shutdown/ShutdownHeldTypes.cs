// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Shutdown;

// Types for DetachedStagesShutdownTests (#1721). The source generator discovers both receptors, so the
// dispatcher under test is the real generated one.

namespace Whizbang.Core.Component.Tests.ShutdownHeld;

/// <summary>The held work for each command id. Each test uses a fresh id.</summary>
internal static class ShutdownHeldProbe {
  private static readonly ConcurrentDictionary<Guid, HeldWork> _held = new();
  public static HeldWork For(Guid id) => _held.GetOrAdd(id, static _ => new HeldWork());
}

public record ShutdownHeldCommand(Guid Id) : ICommand;

public sealed class ShutdownHeldCommandReceptor : IReceptor<ShutdownHeldCommand> {
  public ValueTask HandleAsync(ShutdownHeldCommand message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

/// <summary>Holds the ImmediateDetached stage, then checks the scope it was resolved from.</summary>
[FireAt(LifecycleStage.ImmediateDetached)]
public sealed class ShutdownHeldCommandImmediateDetached(IServiceProvider services) : IReceptor<ShutdownHeldCommand> {
  public async ValueTask HandleAsync(ShutdownHeldCommand message, CancellationToken cancellationToken = default) =>
    await ShutdownHeldProbe.For(message.Id).HoldAsync(services);
}
