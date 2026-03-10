using Whizbang.Core.Observability;

namespace Whizbang.Core.Security;

/// <summary>
/// Extracts security context from incoming messages.
/// Multiple extractors can be registered and are tried in priority order (lower = first).
/// </summary>
/// <remarks>
/// Implement this interface to extract security information from different sources:
/// - MessageHop.Scope (propagated from previous hop via ScopeDelta)
/// - Message payload (e.g., embedded JWT token)
/// - Transport metadata (Service Bus properties, Kafka headers)
///
/// Extractors should return null if they cannot handle the message, allowing
/// the next extractor in priority order to try.
/// </remarks>
/// <docs>core-concepts/message-security#extractors</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/MessageHopSecurityExtractorTests.cs</tests>
/// <example>
/// public class JwtPayloadExtractor : ISecurityContextExtractor {
///   public int Priority => 50; // Run before MessageHop extractor (100)
///
///   public async ValueTask&lt;SecurityExtraction?&gt; ExtractAsync(
///     IMessageEnvelope envelope, MessageSecurityOptions options, CancellationToken ct) {
///     if (envelope.Payload is not ISecurityTokenMessage tokenMessage)
///       return null;
///
///     if (string.IsNullOrEmpty(tokenMessage.Token))
///       return null;
///
///     var claims = DecodeJwt(tokenMessage.Token, validate: options.ValidateCredentials);
///     return new SecurityExtraction {
///       Scope = new PerspectiveScope { TenantId = claims["tenant_id"], UserId = claims["sub"] },
///       Source = "JwtPayload"
///       // ... other properties
///     };
///   }
/// }
/// </example>
public interface ISecurityContextExtractor {
  /// <summary>
  /// Priority order for this extractor. Lower values run first.
  /// Default extractors use priority 100+.
  /// </summary>
  /// <remarks>
  /// Recommended priority ranges:
  /// - 0-49: High priority custom extractors (e.g., explicit tokens)
  /// - 50-99: Standard custom extractors
  /// - 100-199: Built-in extractors (MessageHop)
  /// - 200-299: Transport-specific extractors
  /// - 300+: Fallback extractors
  /// </remarks>
  int Priority { get; }

  /// <summary>
  /// Attempts to extract security context from the given envelope.
  /// </summary>
  /// <param name="envelope">The message envelope containing payload and hops</param>
  /// <param name="options">Security options including validation settings</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>
  /// A SecurityExtraction if this extractor can handle the message, null otherwise.
  /// Returning null allows the next extractor in priority order to try.
  /// </returns>
  ValueTask<SecurityExtraction?> ExtractAsync(
    IMessageEnvelope envelope,
    MessageSecurityOptions options,
    CancellationToken cancellationToken = default);
}
