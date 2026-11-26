// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

// For DispatchSurfaceDerivedIdentityComponentTests: a command another service owns, so a cascade of it writes a copy (#2543).

namespace Whizbang.Core.Component.Tests.DerivedSurface.Unowned;

/// <summary>A command another service owns that a receptor returns, so the generated cascade writes it to the outbox by its type.</summary>
public record DsCascadedCommand(Guid Key) : ICommand;

/// <summary>What makes a receptor return <see cref="DsCascadedCommand"/>.</summary>
public record DsCascadeTrigger(Guid Key) : ICommand;

/// <summary>Returns a command, which cascades.</summary>
public sealed class DsCascadeTriggerReceptor : IReceptor<DsCascadeTrigger, DsCascadedCommand> {
  /// <inheritdoc />
  public ValueTask<DsCascadedCommand> HandleAsync(DsCascadeTrigger message, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(new DsCascadedCommand(message.Key));
}
