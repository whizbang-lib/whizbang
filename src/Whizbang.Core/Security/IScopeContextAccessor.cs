namespace Whizbang.Core.Security;

/// <summary>
/// Accessor for ambient scope context (similar to IHttpContextAccessor).
/// Uses AsyncLocal for request-scoped propagation across async calls.
/// </summary>
/// <docs>fundamentals/security/security#scope-context-accessor</docs>
/// <tests>Whizbang.Core.Tests/Security/ScopeContextAccessorTests.cs</tests>
/// <example>
/// public class OrderService {
///   private readonly IScopeContextAccessor _scopeContextAccessor;
///
///   public OrderService(IScopeContextAccessor scopeContextAccessor) {
///     _scopeContextAccessor = scopeContextAccessor;
///   }
///
///   public async Task&lt;Order&gt; GetOrderAsync(string orderId) {
///     var context = _scopeContextAccessor.Current;
///     var tenantId = context?.Scope.TenantId;
///     // Use tenantId for filtering...
///   }
/// }
/// </example>
public interface IScopeContextAccessor {
  /// <summary>
  /// Gets or sets the current scope context.
  /// Returns null if no context has been set for the current async flow.
  /// </summary>
  IScopeContext? Current { get; set; }

  /// <summary>
  /// Gets or sets the IMessageContext that initiated the current scope.
  /// This is the SOURCE OF TRUTH for security context (UserId, TenantId).
  /// </summary>
  /// <remarks>
  /// <para>
  /// In event-sourcing systems, messages carry the state. The InitiatingContext
  /// stores the IMessageContext that started this scope, providing:
  /// </para>
  /// <para>
  /// - Single source of truth for UserId and TenantId
  /// - Full message tracing context (MessageId, CorrelationId, CausationId)
  /// - Debugging support - can inspect the exact message that initiated the scope
  /// </para>
  /// <para>
  /// The IScopeContext.Scope.UserId/TenantId should match InitiatingContext.UserId/TenantId
  /// since they derive from the same source.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/messages/cascade-context#initiating-context</docs>
  /// <tests>Whizbang.Core.Tests/Security/ScopeContextAccessorInitiatingContextTests.cs</tests>
  IMessageContext? InitiatingContext { get; set; }

  /// <summary>
  /// Gets UserId directly from InitiatingContext (SOURCE OF TRUTH).
  /// This is a POINTER to InitiatingContext.UserId, not a copy.
  /// </summary>
  /// <docs>fundamentals/messages/cascade-context#pointer-properties</docs>
  string? UserId => InitiatingContext?.UserId;

  /// <summary>
  /// Gets TenantId directly from InitiatingContext (SOURCE OF TRUTH).
  /// This is a POINTER to InitiatingContext.TenantId, not a copy.
  /// </summary>
  /// <docs>fundamentals/messages/cascade-context#pointer-properties</docs>
  string? TenantId => InitiatingContext?.TenantId;

  /// <summary>
  /// Gets the full IScopeContext (Roles, Permissions, SecurityPrincipals, Claims).
  /// This is DERIVED from InitiatingContext via authorization enrichment.
  /// </summary>
  IScopeContext? ScopeContext => Current;
}
