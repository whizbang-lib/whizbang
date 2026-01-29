namespace Whizbang.Core.Security;

/// <summary>
/// Accessor for ambient scope context (similar to IHttpContextAccessor).
/// Uses AsyncLocal for request-scoped propagation across async calls.
/// </summary>
/// <docs>core-concepts/security#scope-context-accessor</docs>
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
}
