using Whizbang.Core.ValueObjects;

namespace Whizbang.Core;

/// <summary>
/// Default implementation of <see cref="IMessageContext"/>.
/// </summary>
public class MessageContext : IMessageContext {
  /// <inheritdoc />
  public MessageId MessageId { get; init; } = MessageId.New();

  /// <inheritdoc />
  public CorrelationId CorrelationId { get; init; }

  /// <inheritdoc />
  public MessageId CausationId { get; init; }

  /// <inheritdoc />
  public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

  /// <inheritdoc />
  public string? UserId { get; init; }

  private readonly Dictionary<string, object> _metadata = [];

  /// <inheritdoc />
  public IReadOnlyDictionary<string, object> Metadata => _metadata;

  /// <summary>
  /// Creates a new context with a new MessageId and the specified CorrelationId.
  /// </summary>
  public static MessageContext Create(CorrelationId correlationId, MessageId? causationId = null) {
    return new MessageContext {
      CorrelationId = correlationId,
      CausationId = causationId ?? MessageId.New()
    };
  }

  /// <summary>
  /// Creates a new context with new identifiers.
  /// </summary>
  public static MessageContext New() {
    return new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New()
    };
  }
}
