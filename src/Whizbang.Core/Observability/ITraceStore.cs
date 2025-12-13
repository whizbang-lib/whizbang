using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Observability;

/// <summary>
/// Store for message traces enabling observability queries.
/// Stores complete message envelopes with all hops, metadata, and policy decisions.
/// </summary>
/// <docs>core-concepts/observability</docs>
public interface ITraceStore {
  /// <summary>
  /// Stores a message envelope trace.
  /// </summary>
  Task StoreAsync(IMessageEnvelope envelope, CancellationToken ct = default);

  /// <summary>
  /// Retrieves a message envelope by MessageId.
  /// </summary>
  Task<IMessageEnvelope?> GetByMessageIdAsync(MessageId messageId, CancellationToken ct = default);

  /// <summary>
  /// Retrieves all message envelopes with the same CorrelationId.
  /// Returns messages in chronological order by first hop timestamp.
  /// </summary>
  Task<List<IMessageEnvelope>> GetByCorrelationAsync(CorrelationId correlationId, CancellationToken ct = default);

  /// <summary>
  /// Retrieves the complete causal chain for a message.
  /// Includes the message itself, all parent messages (via CausationId), and all child messages.
  /// Returns messages in chronological order.
  /// </summary>
  Task<List<IMessageEnvelope>> GetCausalChainAsync(MessageId messageId, CancellationToken ct = default);

  /// <summary>
  /// Retrieves all message envelopes within a time range.
  /// Time range is based on first hop timestamp.
  /// Returns messages in chronological order.
  /// </summary>
  Task<List<IMessageEnvelope>> GetByTimeRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}
