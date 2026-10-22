// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Observability;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// No-op implementation of <see cref="ISystemEventEmitter"/>.
/// The default where no event store is registered: there is nowhere to store a system event. Wherever an
/// event store is registered, the default is <see cref="SystemEventEmitter"/> instead (#1621).
/// </summary>
/// <docs>fundamentals/events/system-events#emitter</docs>
public sealed class NullSystemEventEmitter : ISystemEventEmitter {
  /// <inheritdoc />
  public Task EmitEventAuditedAsync<TEvent>(
      Guid streamId,
      long streamPosition,
      MessageEnvelope<TEvent> envelope,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

  /// <inheritdoc />
  public Task EmitCommandAuditedAsync<TCommand, TResponse>(
      TCommand command,
      TResponse response,
      string receptorName,
      IMessageContext? context,
      CancellationToken cancellationToken = default) where TCommand : notnull => Task.CompletedTask;

  /// <inheritdoc />
  public Task EmitAsync<TSystemEvent>(
      TSystemEvent systemEvent,
      CancellationToken cancellationToken = default) where TSystemEvent : ISystemEvent => Task.CompletedTask;

  /// <inheritdoc />
  public bool ShouldExcludeFromAudit(Type type) => false;
}
