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
  /// UserId is read with the following priority:
  /// 1. InitiatingContext (SOURCE OF TRUTH - the IMessageContext that started this scope)
  /// 2. IScopeContext.Scope (populated from envelope hop SecurityContext)
  /// 3. IMessageContextAccessor.Current (fallback)
  /// </remarks>
  /// <docs>core-concepts/cascade-context#scoped-message-context</docs>
  public string? UserId =>
    _scopeContextAccessor.InitiatingContext?.UserId
    ?? _scopeContextAccessor.Current?.Scope.UserId
    ?? _messageContextAccessor.Current?.UserId;

  /// <inheritdoc />
  /// <remarks>
  /// TenantId is read with the following priority:
  /// 1. InitiatingContext (SOURCE OF TRUTH - the IMessageContext that started this scope)
  /// 2. IScopeContext.Scope (populated from envelope hop SecurityContext)
  /// 3. IMessageContextAccessor.Current (fallback)
  /// This ensures tenant context is available even in deferred lifecycle stages like PostPerspectiveAsync.
  /// </remarks>
  /// <docs>core-concepts/cascade-context#scoped-message-context</docs>
  public string? TenantId =>
    _scopeContextAccessor.InitiatingContext?.TenantId
    ?? _scopeContextAccessor.Current?.Scope.TenantId
    ?? _messageContextAccessor.Current?.TenantId;

  /// <inheritdoc />
  public IReadOnlyDictionary<string, object> Metadata =>
    _messageContextAccessor.Current?.Metadata
    ?? new Dictionary<string, object>();

  /// <inheritdoc />
  /// <remarks>
  /// ScopeContext is read from the initiating context which OWNS and CARRIES it.
  /// Fallback to accessor's Current for backward compatibility.
  /// </remarks>
  public IScopeContext? ScopeContext =>
    _scopeContextAccessor.InitiatingContext?.ScopeContext
    ?? _scopeContextAccessor.Current;
}
