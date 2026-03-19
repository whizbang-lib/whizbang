using Whizbang.Core.Observability;

namespace Whizbang.Core.Security;

/// <summary>
/// Establishes security context from incoming messages.
/// Invoked ONCE per message scope, BEFORE any receptors run.
/// </summary>
/// <remarks>
/// This is the primary hook point for security context establishment in message-based scenarios.
/// Unlike HTTP middleware (WhizbangScopeMiddleware), this works for messages arriving via transports
/// like Service Bus or Kafka where there is no HTTP context.
///
/// The provider:
/// 1. Iterates through registered ISecurityContextExtractor instances in priority order
/// 2. Stops at the first successful extraction
/// 3. Calls all ISecurityContextCallback instances after context is established
/// 4. Returns an ImmutableScopeContext that cannot be modified
/// </remarks>
/// <docs>fundamentals/security/message-security</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/MessageSecurityContextProviderTests.cs</tests>
/// <example>
/// // Register the provider
/// services.AddWhizbangMessageSecurity(options => {
///   options.AllowAnonymous = false; // Least privilege (default)
/// });
///
/// // Usage in TransportConsumerWorker
/// var context = await provider.EstablishContextAsync(envelope, scopedProvider, ct);
/// if (context is not null) {
///   scopeContextAccessor.Current = context;
/// }
/// </example>
public interface IMessageSecurityContextProvider {
  /// <summary>
  /// Establishes security context for the current message scope.
  /// </summary>
  /// <param name="envelope">The message envelope with hops and payload</param>
  /// <param name="scopedProvider">The scoped service provider for this message</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The established security context, or null if anonymous access is allowed</returns>
  /// <exception cref="SecurityContextRequiredException">
  /// Thrown when no extractor can establish context and AllowAnonymous is false.
  /// </exception>
  /// <exception cref="TimeoutException">
  /// Thrown when extraction exceeds the configured timeout.
  /// </exception>
  ValueTask<IScopeContext?> EstablishContextAsync(
    IMessageEnvelope envelope,
    IServiceProvider scopedProvider,
    CancellationToken cancellationToken = default);
}
