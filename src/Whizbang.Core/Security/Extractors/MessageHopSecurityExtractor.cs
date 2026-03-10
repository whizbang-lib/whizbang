using System.Text.Json;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Security.Extractors;

/// <summary>
/// Extracts security context from the message envelope's hop chain.
/// Walks backwards through HopType.Current hops to find the most recent scope.
/// </summary>
/// <remarks>
/// This is the default extractor for distributed message security propagation.
/// When a message flows between services, the scope delta is preserved
/// in the MessageHop.Scope property and can be extracted here.
///
/// Priority: 100 (runs first among default extractors)
///
/// The extractor maps the scope (TenantId, UserId) to the
/// full SecurityExtraction, leaving roles, permissions, and claims empty
/// (unless the delta contains them).
/// </remarks>
/// <docs>core-concepts/message-security#message-hop-extractor</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/MessageHopSecurityExtractorTests.cs</tests>
public sealed partial class MessageHopSecurityExtractor : ISecurityContextExtractor {
  private static readonly HashSet<string> _emptyRoles = [];
  private static readonly HashSet<Permission> _emptyPermissions = [];
  private static readonly HashSet<SecurityPrincipalId> _emptyPrincipals = [];
  private static readonly Dictionary<string, string> _emptyClaims = [];

  private readonly ILogger<MessageHopSecurityExtractor>? _logger;

  /// <summary>
  /// Creates a new instance of MessageHopSecurityExtractor.
  /// </summary>
  /// <param name="logger">Optional logger for diagnostics.</param>
  public MessageHopSecurityExtractor(ILogger<MessageHopSecurityExtractor>? logger = null) {
    _logger = logger;
  }

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

    // Walk backwards through current hops to find the most recent scope
    var scope = _getCurrentScope(envelope.Hops, _logger, envelope.MessageId);

    // No scope in hop chain
    if (scope is null) {
      Log.NoScopeFound(_logger, envelope.MessageId, envelope.Hops?.Count ?? 0);
      return ValueTask.FromResult<SecurityExtraction?>(null);
    }

    // Empty scope (no TenantId or UserId)
    if (string.IsNullOrEmpty(scope.TenantId) && string.IsNullOrEmpty(scope.UserId)) {
      return ValueTask.FromResult<SecurityExtraction?>(null);
    }

    // Map to SecurityExtraction
    var extraction = new SecurityExtraction {
      Scope = scope,
      Roles = _emptyRoles,
      Permissions = _emptyPermissions,
      SecurityPrincipals = _emptyPrincipals,
      Claims = _emptyClaims,
      Source = "MessageHop"
    };

    return ValueTask.FromResult<SecurityExtraction?>(extraction);
  }

  /// <summary>
  /// Gets the most recent scope from current hops by merging deltas.
  /// Walks forward through HopType.Current hops only (ignores causation hops).
  /// </summary>
  private static PerspectiveScope? _getCurrentScope(
      List<MessageHop>? hops,
      ILogger<MessageHopSecurityExtractor>? logger,
      ValueObjects.MessageId messageId) {
    // Defensive: Handle null or empty hops gracefully
    if (hops == null || hops.Count == 0) {
      Log.HopsNullOrEmpty(logger, messageId, hops == null);
      return null;
    }

    PerspectiveScope? result = null;
    var currentHops = hops.Where(h => h.Type == HopType.Current).ToList();

    Log.ProcessingHops(logger, messageId, hops.Count, currentHops.Count);

    foreach (var hop in currentHops) {
      if (hop.Scope == null) {
        Log.HopScopeNull(logger, messageId);
        continue;
      }

      if (hop.Scope.Values == null) {
        Log.HopScopeValuesNull(logger, messageId);
        continue;
      }

      if (!hop.Scope.Values.TryGetValue(ScopeProp.Scope, out var scopeElement)) {
        // Log available keys for debugging
        var availableKeys = string.Join(", ", hop.Scope.Values.Keys.Select(k => k.ToString()));
        Log.ScopePropNotFound(logger, messageId, availableKeys);
        continue;
      }

      result = _deserializeScope(scopeElement);
      Log.ScopeExtracted(logger, messageId, result.UserId, result.TenantId);
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
      EventId = 6,
      Level = LogLevel.Warning,
      Message = "MessageHopSecurityExtractor: ScopeProp.Scope not found in Values. MessageId={MessageId}, AvailableKeys=[{AvailableKeys}]")]
    private static partial void ScopePropNotFoundInternal(ILogger logger, ValueObjects.MessageId messageId, string availableKeys);

    public static void ScopePropNotFound(ILogger? logger, ValueObjects.MessageId messageId, string availableKeys) {
      if (logger != null) {
        ScopePropNotFoundInternal(logger, messageId, availableKeys);
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

  private static PerspectiveScope _deserializeScope(JsonElement element) {
    var scope = new PerspectiveScope();

    if (element.TryGetProperty("t", out var t) && t.ValueKind != JsonValueKind.Null) {
      scope.TenantId = t.GetString();
    }
    if (element.TryGetProperty("u", out var u) && u.ValueKind != JsonValueKind.Null) {
      scope.UserId = u.GetString();
    }
    if (element.TryGetProperty("c", out var c) && c.ValueKind != JsonValueKind.Null) {
      scope.CustomerId = c.GetString();
    }
    if (element.TryGetProperty("o", out var o) && o.ValueKind != JsonValueKind.Null) {
      scope.OrganizationId = o.GetString();
    }
    if (element.TryGetProperty("ap", out var ap) && ap.ValueKind == JsonValueKind.Array) {
      foreach (var item in ap.EnumerateArray()) {
        var val = item.GetString();
        if (val != null) {
          scope.AllowedPrincipals.Add(val);
        }
      }
    }

    return scope;
  }
}
