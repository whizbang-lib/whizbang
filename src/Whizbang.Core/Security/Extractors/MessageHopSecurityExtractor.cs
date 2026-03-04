using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Security.Extractors;

/// <summary>
/// Extracts security context from the message envelope's hop chain.
/// Walks backwards through HopType.Current hops to find the most recent security context.
/// </summary>
/// <remarks>
/// This is the default extractor for distributed message security propagation.
/// When a message flows between services, the security context is preserved
/// in the MessageHop.SecurityContext property and can be extracted here.
///
/// Priority: 100 (runs first among default extractors)
///
/// The extractor maps the simple SecurityContext (TenantId, UserId) to the
/// full SecurityExtraction, leaving roles, permissions, and claims empty
/// (since the hop SecurityContext doesn't contain these).
/// </remarks>
/// <docs>core-concepts/message-security#message-hop-extractor</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/MessageHopSecurityExtractorTests.cs</tests>
public sealed class MessageHopSecurityExtractor : ISecurityContextExtractor {
  private static readonly HashSet<string> _emptyRoles = [];
  private static readonly HashSet<Permission> _emptyPermissions = [];
  private static readonly HashSet<SecurityPrincipalId> _emptyPrincipals = [];
  private static readonly Dictionary<string, string> _emptyClaims = [];

  /// <summary>
  /// Default priority for MessageHopSecurityExtractor.
  /// Lower values run first. This extractor runs at priority 100.
  /// </summary>
  public int Priority => 100;

  /// <inheritdoc />
  public ValueTask<SecurityExtraction?> ExtractAsync(
    IMessageEnvelope envelope,
    MessageSecurityOptions options,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(options);

    // Check for cancellation
    cancellationToken.ThrowIfCancellationRequested();

    // Walk backwards through current hops to find the most recent security context
    var hopSecurityContext = _getCurrentSecurityContext(envelope.Hops);

    // No security context in hop chain
    if (hopSecurityContext is null) {
      return ValueTask.FromResult<SecurityExtraction?>(null);
    }

    // Empty security context (no TenantId or UserId)
    if (string.IsNullOrEmpty(hopSecurityContext.TenantId) &&
        string.IsNullOrEmpty(hopSecurityContext.UserId)) {
      return ValueTask.FromResult<SecurityExtraction?>(null);
    }

    // Map to SecurityExtraction
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope {
        TenantId = hopSecurityContext.TenantId,
        UserId = hopSecurityContext.UserId
      },
      Roles = _emptyRoles,
      Permissions = _emptyPermissions,
      SecurityPrincipals = _emptyPrincipals,
      Claims = _emptyClaims,
      Source = "MessageHop"
    };

    return ValueTask.FromResult<SecurityExtraction?>(extraction);
  }

  /// <summary>
  /// Gets the most recent security context from current hops.
  /// Walks backwards through HopType.Current hops only (ignores causation hops).
  /// </summary>
  private static SecurityContext? _getCurrentSecurityContext(List<MessageHop> hops) {
    for (int i = hops.Count - 1; i >= 0; i--) {
      if (hops[i].Type == HopType.Current && hops[i].SecurityContext != null) {
        return hops[i].SecurityContext;
      }
    }

    return null;
  }
}
