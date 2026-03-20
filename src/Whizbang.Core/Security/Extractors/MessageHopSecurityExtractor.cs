using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Security.Extractors;

/// <summary>
/// Extracts security context from the message envelope's hop chain.
/// Merges ScopeDelta from all HopType.Current hops to produce the full security context
/// including roles, permissions, principals, and claims.
/// </summary>
/// <remarks>
/// This is the default extractor for distributed message security propagation.
/// When a message flows between services, the scope delta is preserved
/// in the MessageHop.Scope property and can be extracted here.
///
/// Priority: 100 (runs first among default extractors)
///
/// Uses <see cref="ScopeDelta.ApplyTo"/> to merge all hop deltas, producing a full
/// <see cref="ScopeContext"/> with all security fields (scope, roles, permissions,
/// principals, claims, actual/effective principal, context type).
/// </remarks>
/// <docs>fundamentals/security/message-security#message-hop-extractor</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/MessageHopSecurityExtractorTests.cs</tests>
/// <remarks>
/// Creates a new instance of MessageHopSecurityExtractor.
/// </remarks>
/// <param name="logger">Optional logger for diagnostics.</param>
public sealed partial class MessageHopSecurityExtractor(ILogger<MessageHopSecurityExtractor>? logger = null) : ISecurityContextExtractor {
  private readonly ILogger<MessageHopSecurityExtractor>? _logger = logger;

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

    // Merge ScopeDelta from all Current hops to produce the full ScopeContext
    var scopeContext = _mergeScopeDeltas(envelope.Hops, _logger, envelope.MessageId);

    // No scope in hop chain
    if (scopeContext is null) {
      Log.NoScopeFound(_logger, envelope.MessageId, envelope.Hops?.Count ?? 0);
      return ValueTask.FromResult<SecurityExtraction?>(null);
    }

    // Empty scope (no TenantId or UserId)
    if (string.IsNullOrEmpty(scopeContext.Scope.TenantId) && string.IsNullOrEmpty(scopeContext.Scope.UserId)) {
      return ValueTask.FromResult<SecurityExtraction?>(null);
    }

    // Map to SecurityExtraction with full context from ScopeDelta
    var extraction = new SecurityExtraction {
      Scope = scopeContext.Scope,
      Roles = scopeContext.Roles,
      Permissions = scopeContext.Permissions,
      SecurityPrincipals = scopeContext.SecurityPrincipals,
      Claims = scopeContext.Claims,
      ActualPrincipal = scopeContext.ActualPrincipal,
      EffectivePrincipal = scopeContext.EffectivePrincipal,
      ContextType = scopeContext.ContextType,
      Source = "MessageHop"
    };

    return ValueTask.FromResult<SecurityExtraction?>(extraction);
  }

  /// <summary>
  /// Merges ScopeDelta from all Current hops using <see cref="ScopeDelta.ApplyTo"/>.
  /// This produces the full <see cref="ScopeContext"/> including roles, permissions,
  /// principals, claims, and context type from the hop chain.
  /// </summary>
  private static ScopeContext? _mergeScopeDeltas(
      List<MessageHop>? hops,
      ILogger<MessageHopSecurityExtractor>? logger,
      ValueObjects.MessageId messageId) {
    // Defensive: Handle null or empty hops gracefully
    if (hops == null || hops.Count == 0) {
      Log.HopsNullOrEmpty(logger, messageId, hops == null);
      return null;
    }

    ScopeContext? result = null;
    var currentHops = hops.Where(h => h.Type == HopType.Current).ToList();

    Log.ProcessingHops(logger, messageId, hops.Count, currentHops.Count);

    foreach (var hop in currentHops) {
      if (hop.Scope == null) {
        Log.HopScopeNull(logger, messageId);
        continue;
      }

      if (!hop.Scope.HasChanges) {
        Log.HopScopeValuesNull(logger, messageId);
        continue;
      }

      result = hop.Scope.ApplyTo(result);
      Log.ScopeExtracted(logger, messageId, result.Scope.UserId, result.Scope.TenantId);
    }

    return result;
  }

  /// <summary>
  /// AOT-compatible logging for MessageHopSecurityExtractor diagnostics.
  /// Uses compile-time LoggerMessage source generator.
  /// </summary>
  private static partial class Log {
    [LoggerMessage(
      EventId = 1,
      Level = LogLevel.Debug,
      Message = "MessageHopSecurityExtractor: No scope found in hop chain. MessageId={MessageId}, HopCount={HopCount}")]
    private static partial void NoScopeFoundInternal(ILogger logger, ValueObjects.MessageId messageId, int hopCount);

    public static void NoScopeFound(ILogger? logger, ValueObjects.MessageId messageId, int hopCount) {
      if (logger != null) {
        NoScopeFoundInternal(logger, messageId, hopCount);
      }
    }

    [LoggerMessage(
      EventId = 2,
      Level = LogLevel.Debug,
      Message = "MessageHopSecurityExtractor: Hops null or empty. MessageId={MessageId}, IsNull={IsNull}")]
    private static partial void HopsNullOrEmptyInternal(ILogger logger, ValueObjects.MessageId messageId, bool isNull);

    public static void HopsNullOrEmpty(ILogger? logger, ValueObjects.MessageId messageId, bool isNull) {
      if (logger != null) {
        HopsNullOrEmptyInternal(logger, messageId, isNull);
      }
    }

    [LoggerMessage(
      EventId = 3,
      Level = LogLevel.Debug,
      Message = "MessageHopSecurityExtractor: Processing hops. MessageId={MessageId}, TotalHops={TotalHops}, CurrentHops={CurrentHops}")]
    private static partial void ProcessingHopsInternal(ILogger logger, ValueObjects.MessageId messageId, int totalHops, int currentHops);

    public static void ProcessingHops(ILogger? logger, ValueObjects.MessageId messageId, int totalHops, int currentHops) {
      if (logger != null) {
        ProcessingHopsInternal(logger, messageId, totalHops, currentHops);
      }
    }

    [LoggerMessage(
      EventId = 4,
      Level = LogLevel.Debug,
      Message = "MessageHopSecurityExtractor: Hop.Scope is null. MessageId={MessageId}")]
    private static partial void HopScopeNullInternal(ILogger logger, ValueObjects.MessageId messageId);

    public static void HopScopeNull(ILogger? logger, ValueObjects.MessageId messageId) {
      if (logger != null) {
        HopScopeNullInternal(logger, messageId);
      }
    }

    [LoggerMessage(
      EventId = 5,
      Level = LogLevel.Debug,
      Message = "MessageHopSecurityExtractor: Hop.Scope.Values is null. MessageId={MessageId}")]
    private static partial void HopScopeValuesNullInternal(ILogger logger, ValueObjects.MessageId messageId);

    public static void HopScopeValuesNull(ILogger? logger, ValueObjects.MessageId messageId) {
      if (logger != null) {
        HopScopeValuesNullInternal(logger, messageId);
      }
    }

    [LoggerMessage(
      EventId = 7,
      Level = LogLevel.Debug,
      Message = "MessageHopSecurityExtractor: Scope extracted successfully. MessageId={MessageId}, UserId={UserId}, TenantId={TenantId}")]
    private static partial void ScopeExtractedInternal(ILogger logger, ValueObjects.MessageId messageId, string? userId, string? tenantId);

    public static void ScopeExtracted(ILogger? logger, ValueObjects.MessageId messageId, string? userId, string? tenantId) {
      if (logger != null) {
        ScopeExtractedInternal(logger, messageId, userId, tenantId);
      }
    }
  }

}
