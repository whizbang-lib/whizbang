using Whizbang.Core.Observability;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// No-op implementation of <see cref="ISystemEventEmitter"/>.
/// Used as a default when system event auditing is not configured.
/// Replaced by <see cref="SystemEventEmitter"/> when auditing is enabled.
/// </summary>
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
