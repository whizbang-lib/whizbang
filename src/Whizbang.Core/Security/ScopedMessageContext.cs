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
  public string? UserId {
    get {
      var scopeUserId = _scopeContextAccessor.Current?.Scope.UserId;
      var msgUserId = _messageContextAccessor.Current?.UserId;
      System.Diagnostics.Debug.WriteLine($"[ScopedMessageContext.UserId] ScopeContextAccessor.Current={(_scopeContextAccessor.Current == null ? "NULL" : "set")}, Scope.UserId={scopeUserId}, MessageContextAccessor.Current={(_messageContextAccessor.Current == null ? "NULL" : "set")}, UserId={msgUserId}");
      return scopeUserId ?? msgUserId;
    }
  }

  /// <inheritdoc />
  /// <remarks>
  /// TenantId is read from the security scope context (populated from envelope hop SecurityContext).
  /// Falls back to the message context's TenantId if scope context is not available.
  /// This ensures tenant context is available even in deferred lifecycle stages like PostPerspectiveAsync.
  /// </remarks>
  public string? TenantId =>
    _scopeContextAccessor.Current?.Scope.TenantId
    ?? _messageContextAccessor.Current?.TenantId;

  /// <inheritdoc />
  public IReadOnlyDictionary<string, object> Metadata =>
    _messageContextAccessor.Current?.Metadata
    ?? new Dictionary<string, object>();
}
