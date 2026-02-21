using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Security;

/// <summary>
/// Scoped implementation of <see cref="IMessageContext"/> that reads from
/// <see cref="IMessageContextAccessor"/> and <see cref="IScopeContextAccessor"/>.
/// Enables DI injection of IMessageContext in receptors.
/// </summary>
/// <docs>core-concepts/message-security#scoped-message-context</docs>
internal sealed class ScopedMessageContext : IMessageContext {
  private readonly IMessageContextAccessor _messageContextAccessor;
  private readonly IScopeContextAccessor _scopeContextAccessor;

  public ScopedMessageContext(
    IMessageContextAccessor messageContextAccessor,
    IScopeContextAccessor scopeContextAccessor) {
    _messageContextAccessor = messageContextAccessor;
    _scopeContextAccessor = scopeContextAccessor;
  }

  /// <inheritdoc />
  public MessageId MessageId =>
    _messageContextAccessor.Current?.MessageId ?? MessageId.New();

  /// <inheritdoc />
  public CorrelationId CorrelationId =>
    _messageContextAccessor.Current?.CorrelationId ?? CorrelationId.New();

  /// <inheritdoc />
  public MessageId CausationId =>
    _messageContextAccessor.Current?.CausationId ?? MessageId.New();

  /// <inheritdoc />
  public DateTimeOffset Timestamp =>
    _messageContextAccessor.Current?.Timestamp ?? DateTimeOffset.UtcNow;

  /// <inheritdoc />
  /// <remarks>
  /// UserId is read from the security scope context (populated from envelope hop SecurityContext).
  /// Falls back to the message context's UserId if scope context is not available.
  /// </remarks>
  public string? UserId =>
    _scopeContextAccessor.Current?.Scope.UserId
    ?? _messageContextAccessor.Current?.UserId;

  /// <inheritdoc />
  public IReadOnlyDictionary<string, object> Metadata =>
    _messageContextAccessor.Current?.Metadata
    ?? new Dictionary<string, object>();
}
