using Whizbang.Core.Observability;

namespace Whizbang.Core.Security;

/// <summary>
/// Callback invoked after security context is established.
/// Use this to initialize custom scoped services with the security context.
/// </summary>
/// <remarks>
/// Callbacks are called after all extractors have run and a context is established.
/// They are NOT called if no context could be established (i.e., when returning null
/// with AllowAnonymous=true).
///
/// Common use cases:
/// - Populating custom UserContextManager services
/// - Setting up tenant-specific database connections
/// - Initializing audit/logging contexts
/// </remarks>
/// <docs>fundamentals/security/message-security#callbacks</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/MessageSecurityContextProviderTests.cs:EstablishContextAsync_WithCallbacks_CallsAllCallbacksAfterContextEstablishedAsync</tests>
/// <example>
/// public class UserContextManagerCallback : ISecurityContextCallback {
///   private readonly UserContextManager _userContextManager;
///
///   public UserContextManagerCallback(UserContextManager ucm) => _userContextManager = ucm;
///
///   public ValueTask OnContextEstablishedAsync(
///     IScopeContext context, IMessageEnvelope envelope,
///     IServiceProvider scopedProvider, CancellationToken ct) {
///     _userContextManager.SetFromScopeContext(context);
///     return ValueTask.CompletedTask;
///   }
/// }
/// </example>
public interface ISecurityContextCallback {
  /// <summary>
  /// Called after security context is successfully established.
  /// </summary>
  /// <param name="context">The established security context</param>
  /// <param name="envelope">The original message envelope</param>
  /// <param name="scopedProvider">The scoped service provider for this message</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>A ValueTask representing the asynchronous operation</returns>
  ValueTask OnContextEstablishedAsync(
    IScopeContext context,
    IMessageEnvelope envelope,
    IServiceProvider scopedProvider,
    CancellationToken cancellationToken = default);
}
