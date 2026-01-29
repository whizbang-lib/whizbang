using Whizbang.Core.Observability;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Emits system events to the dedicated system stream.
/// Used by decorators and pipeline behaviors to emit audit and monitoring events.
/// </summary>
/// <remarks>
/// <para>
/// System events are emitted to the <c>$wb-system</c> stream for isolation from
/// domain events. The emitter respects <see cref="SystemEventOptions"/> configuration.
/// </para>
/// <para>
/// Events with <c>[AuditEvent(Exclude = true)]</c> are not re-audited to prevent
/// infinite loops.
/// </para>
/// </remarks>
/// <docs>core-concepts/system-events#emitter</docs>
public interface ISystemEventEmitter {
  /// <summary>
  /// Emits an <see cref="EventAudited"/> system event for a domain event that was appended.
  /// </summary>
  /// <typeparam name="TEvent">The type of the original event.</typeparam>
  /// <param name="streamId">The stream the event was appended to.</param>
  /// <param name="streamPosition">The position in the stream.</param>
  /// <param name="envelope">The message envelope containing the event.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task representing the async operation.</returns>
  Task EmitEventAuditedAsync<TEvent>(
      Guid streamId,
      long streamPosition,
      MessageEnvelope<TEvent> envelope,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Emits a <see cref="CommandAudited"/> system event for a command that was processed.
  /// </summary>
  /// <typeparam name="TCommand">The type of the command.</typeparam>
  /// <typeparam name="TResponse">The type of the response.</typeparam>
  /// <param name="command">The command that was processed.</param>
  /// <param name="response">The response from the receptor.</param>
  /// <param name="receptorName">Name of the receptor that handled the command.</param>
  /// <param name="context">The message context with scope information.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task representing the async operation.</returns>
  Task EmitCommandAuditedAsync<TCommand, TResponse>(
      TCommand command,
      TResponse response,
      string receptorName,
      IMessageContext? context,
      CancellationToken cancellationToken = default) where TCommand : notnull;

  /// <summary>
  /// Emits a generic system event to the system stream.
  /// </summary>
  /// <typeparam name="TSystemEvent">The type of system event to emit.</typeparam>
  /// <param name="systemEvent">The system event to emit.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task representing the async operation.</returns>
  Task EmitAsync<TSystemEvent>(
      TSystemEvent systemEvent,
      CancellationToken cancellationToken = default) where TSystemEvent : ISystemEvent;

  /// <summary>
  /// Checks if the given type should be excluded from auditing.
  /// Types with <c>[AuditEvent(Exclude = true)]</c> are excluded.
  /// </summary>
  /// <param name="type">The type to check.</param>
  /// <returns>True if the type should be excluded from auditing.</returns>
  bool ShouldExcludeFromAudit(Type type);
}
