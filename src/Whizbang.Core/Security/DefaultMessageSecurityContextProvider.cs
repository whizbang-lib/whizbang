using Whizbang.Core.Observability;
using Whizbang.Core.Security.Exceptions;
using Whizbang.Core.SystemEvents.Security;

namespace Whizbang.Core.Security;

/// <summary>
/// Default implementation of IMessageSecurityContextProvider.
/// Orchestrates extractors and callbacks to establish security context from messages.
/// </summary>
/// <remarks>
/// This implementation:
/// 1. Checks if the message type is exempt from security requirements
/// 2. Iterates through extractors in priority order (lower priority = earlier)
/// 3. Stops at the first successful extraction
/// 4. Wraps the result in ImmutableScopeContext
/// 5. Calls all callbacks with the established context
/// 6. Optionally emits audit events
///
/// All operations are AOT-compatible with no reflection.
/// </remarks>
/// <docs>core-concepts/message-security#default-provider</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/MessageSecurityContextProviderTests.cs</tests>
public sealed class DefaultMessageSecurityContextProvider : IMessageSecurityContextProvider {
  private readonly IReadOnlyList<ISecurityContextExtractor> _extractors;
  private readonly IReadOnlyList<ISecurityContextCallback> _callbacks;
  private readonly MessageSecurityOptions _options;
  private readonly Action<ScopeContextEstablished>? _onAuditEvent;

  /// <summary>
  /// Creates a new DefaultMessageSecurityContextProvider.
  /// </summary>
  /// <param name="extractors">Security context extractors (will be sorted by priority)</param>
  /// <param name="callbacks">Callbacks to invoke after context establishment</param>
  /// <param name="options">Security options</param>
  /// <param name="onAuditEvent">Optional callback for audit events (for testing/custom audit)</param>
  public DefaultMessageSecurityContextProvider(
    IEnumerable<ISecurityContextExtractor> extractors,
    IEnumerable<ISecurityContextCallback> callbacks,
    MessageSecurityOptions options,
    Action<ScopeContextEstablished>? onAuditEvent = null) {
    _extractors = extractors.OrderBy(e => e.Priority).ToList();
    _callbacks = callbacks.ToList();
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _onAuditEvent = onAuditEvent;
  }

  /// <inheritdoc />
  public async ValueTask<IScopeContext?> EstablishContextAsync(
    IMessageEnvelope envelope,
    IServiceProvider scopedProvider,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(scopedProvider);

    // Defensive: Verify internal state is valid (should never be null after constructor)
    if (_options is null) {
      throw new InvalidOperationException("MessageSecurityOptions is null - provider not properly initialized");
    }

    if (_extractors is null) {
      throw new InvalidOperationException("Extractors list is null - provider not properly initialized");
    }

    // Check for cancellation first
    cancellationToken.ThrowIfCancellationRequested();

    // Defensive: Handle null Payload gracefully
    if (envelope.Payload is null) {
      // No payload means no type to check - return null for anonymous processing
      if (_options.AllowAnonymous) {
        return null;
      }
      throw new ArgumentNullException(nameof(envelope), "Message envelope has null Payload");
    }

    // Check if message type is exempt
    var payloadType = envelope.Payload.GetType();
    if (_options.ExemptMessageTypes?.Contains(payloadType) == true) {
      return null;
    }

    // Track if payload is JsonElement - an intermediate representation from outbox
    // before deserialization. For JsonElement, we try extractors but don't REQUIRE
    // security (it will be checked again after deserialization with the real type).
    var isJsonElement = payloadType == typeof(System.Text.Json.JsonElement);

    // Try extractors in priority order with timeout
    SecurityExtraction? extraction = null;

    using var timeoutCts = new CancellationTokenSource(_options.Timeout);
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

    try {
      foreach (var extractor in _extractors) {
        // Defensive: Skip null extractors
        if (extractor is null) {
          continue;
        }

        linkedCts.Token.ThrowIfCancellationRequested();

        extraction = await extractor.ExtractAsync(envelope, _options, linkedCts.Token);

        if (extraction is not null) {
          break;
        }
      }
    } catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested) {
      throw new TimeoutException(
        $"Security context establishment timed out after {_options.Timeout.TotalSeconds:F1} seconds.");
    }

    // No extraction and anonymous not allowed
    if (extraction is null) {
      // JsonElement is an intermediate representation (outbox) - don't require security
      // The real message type will be checked after deserialization
      if (!_options.AllowAnonymous && !isJsonElement) {
        // Check if envelope already carries scope from an upstream security check.
        // After outbox/transport serialization, the typed message may have no extractor,
        // but the envelope's hops contain the ScopeDelta from the original authentication.
        // If scope data exists on the envelope, the message was already authenticated —
        // return null so callers can use envelope.GetCurrentScope() as fallback.
        var envelopeScope = envelope.GetCurrentScope();
        if (envelopeScope is null) {
          throw new SecurityContextRequiredException(payloadType);
        }
      }

      return null;
    }

    // Wrap in immutable context
    var context = new ImmutableScopeContext(extraction, _options.PropagateToOutgoingMessages);

    // Emit audit event if enabled
    if (_options.EnableAuditLogging && _onAuditEvent is not null) {
      _onAuditEvent(new ScopeContextEstablished {
        Scope = extraction.Scope,
        Roles = extraction.Roles,
        Permissions = extraction.Permissions,
        Source = extraction.Source,
        Timestamp = DateTimeOffset.UtcNow
      });
    }

    // Call all callbacks
    foreach (var callback in _callbacks) {
      cancellationToken.ThrowIfCancellationRequested();
      await callback.OnContextEstablishedAsync(context, envelope, scopedProvider, cancellationToken);
    }

    return context;
  }
}
