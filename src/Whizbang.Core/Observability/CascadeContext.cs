using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Observability;

/// <summary>
/// Captures context data that should propagate from a parent message to child messages.
/// Lightweight record containing only what needs to cascade - NOT the full hop history.
/// </summary>
/// <remarks>
/// <para>CascadeContext is the single source of truth for context propagation. It encapsulates:
/// - CorrelationId: Links all messages in a workflow
/// - CausationId: Parent message's MessageId (for causation chain)
/// - SecurityContext: UserId and TenantId for multi-tenant security
/// - Metadata: Extensible key-value store for enrichers</para>
///
/// <para>Use this instead of manually extracting and passing individual properties.</para>
/// </remarks>
/// <docs>fundamentals/messages/cascade-context</docs>
/// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs</tests>
public sealed record CascadeContext {
  /// <summary>
  /// The correlation ID to propagate. Links all messages in a workflow.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:Constructor_WithRequiredProperties_InitializesCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:NewRoot_GeneratesNewCorrelationIdAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:RecordEquality_SameValues_AreEqualAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:RecordEquality_DifferentCorrelationId_AreNotEqualAsync</tests>
  public required CorrelationId CorrelationId { get; init; }

  /// <summary>
  /// The causation ID for the child message.
  /// Typically the parent message's MessageId.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:Constructor_WithRequiredProperties_InitializesCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:NewRoot_GeneratesNewCorrelationIdAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:RecordEquality_SameValues_AreEqualAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:RecordEquality_DifferentCausationId_AreNotEqualAsync</tests>
  public required MessageId CausationId { get; init; }

  /// <summary>
  /// Security context to propagate (UserId, TenantId).
  /// May be null if no security context is available.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:Constructor_WithRequiredProperties_InitializesCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:Constructor_WithNullSecurityContext_AllowsNullAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:NewRootWithAmbientSecurity_WithAmbientContext_InheritsSecurityAsync</tests>
  public SecurityContext? SecurityContext { get; init; }

  /// <summary>
  /// Extensible metadata for enrichers to add custom context.
  /// Immutable dictionary - enrichers return new CascadeContext with updated metadata.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:Constructor_WithMetadata_SetsMetadataAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:WithMetadata_SingleKey_AddsMetadataAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:WithMetadata_ExistingMetadata_MergesCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:WithMetadata_Dictionary_MergesAllEntriesAsync</tests>
  public IReadOnlyDictionary<string, object>? Metadata { get; init; }

  /// <summary>
  /// Creates a new root cascade context with fresh identifiers.
  /// Used when starting a new message flow without a parent.
  /// Does not include any security context.
  /// </summary>
  /// <returns>A new CascadeContext with generated CorrelationId and CausationId</returns>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:NewRoot_GeneratesNewCorrelationIdAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:NewRoot_GeneratesUniqueIds_AcrossMultipleCallsAsync</tests>
  public static CascadeContext NewRoot() {
    return new CascadeContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      SecurityContext = null
    };
  }

  /// <summary>
  /// Creates a new root cascade context with security from ambient scope.
  /// Used when starting a new message flow from an authenticated context.
  /// </summary>
  /// <returns>A new CascadeContext with generated IDs and ambient security</returns>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:NewRootWithAmbientSecurity_NoAmbientContext_ReturnsNullSecurityAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:NewRootWithAmbientSecurity_WithAmbientContext_InheritsSecurityAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:NewRootWithAmbientSecurity_WithAmbientContextButPropagationDisabled_ReturnsNullSecurityAsync</tests>
  public static CascadeContext NewRootWithAmbientSecurity() {
    return new CascadeContext {
      // Adopt a correlation id captured at the inbound edge (e.g. an X-Correlation-ID header) so a
      // client-supplied token flows through the whole graph; otherwise mint one aligned to the ambient
      // W3C trace (so Whizbang correlation lines up with the OpenTelemetry trace).
      CorrelationId = InboundCorrelationAccessor.Current ?? CorrelationId.NewRootAligned(),
      CausationId = MessageId.New(),
      SecurityContext = GetSecurityFromAmbient()
    };
  }

  /// <summary>
  /// Extracts security context from the ambient AsyncLocal scope.
  /// Returns null if no context, context is not ImmutableScopeContext, or propagation is disabled.
  /// </summary>
  /// <returns>SecurityContext with UserId and TenantId, or null</returns>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:GetSecurityFromAmbient_NoAmbientContext_ReturnsNullAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:GetSecurityFromAmbient_WithNonImmutableContext_ReturnsNullAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:GetSecurityFromAmbient_WithImmutableContextAndPropagation_ReturnsSecurityAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:GetSecurityFromAmbient_WithImmutableContextButNoPropagation_ReturnsNullAsync</tests>
  public static SecurityContext? GetSecurityFromAmbient() {
    // Only propagate from ImmutableScopeContext with propagation enabled
    if (ScopeContextAccessor.CurrentContext is not ImmutableScopeContext ctx) {
      return null;
    }

    if (!ctx.ShouldPropagate) {
      return null;
    }

    return new SecurityContext {
      UserId = ctx.Scope.UserId,
      TenantId = ctx.Scope.TenantId
    };
  }

  /// <summary>
  /// Resolves the cascade identity (<see cref="CorrelationId"/> + causation <see cref="MessageId"/>) to stamp
  /// on a hop that is being published to the outbox or written to the event store — the mirror of
  /// <see cref="GetSecurityFromAmbient"/> for the tracing/lineage axis.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The "publish/store" hop builders re-derive <em>scope</em> from ambient context but historically dropped
  /// the correlation/causation pair, so a persisted hop carried <c>sc</c> (scope) yet no <c>co</c>/<c>ca</c> —
  /// the notification's correlationId came back null and downstream consumers lost the chain. This resolves the
  /// pair the same way scope is resolved, so identity flows wherever scope flows.
  /// </para>
  /// <para>Priority:
  /// <list type="number">
  /// <item><description>the ambient initiating context — the inbound message whose handler is emitting this event;</description></item>
  /// <item><description>the source envelope — a cascaded event inherits its parent's correlation from the source hop;</description></item>
  /// <item><description>a new id aligned to the ambient W3C trace (so Whizbang correlation lines up with the OTel trace).</description></item>
  /// </list>
  /// </para>
  /// </remarks>
  /// <param name="sourceEnvelope">The parent message's envelope when cascading, or <see langword="null"/> for a top-level publish/store.</param>
  /// <returns>The correlation id (never default) and the causation id (null when there is no parent).</returns>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:ResolveCascadeIdentity_WithAmbientInitiatingContext_UsesItsCorrelationAndCausationAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:ResolveCascadeIdentity_NoAmbient_FallsBackToSourceEnvelopeAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:ResolveCascadeIdentity_NoAmbientNoSource_MintsTraceAlignedRootAsync</tests>
  public static (CorrelationId Correlation, MessageId? Causation) ResolveCascadeIdentity(IMessageEnvelope? sourceEnvelope) {
    // 1. Ambient initiating context — the inbound message whose handler is emitting this event.
    if (ScopeContextAccessor.CurrentInitiatingContext is { } initiating && initiating.CorrelationId.Value != Guid.Empty) {
      return (initiating.CorrelationId, initiating.MessageId);
    }

    // 2. Source envelope — a cascaded event carries its parent's correlation on the source hop.
    if (sourceEnvelope?.GetCorrelationId() is { } sourceCorrelation && sourceCorrelation.Value != Guid.Empty) {
      return (sourceCorrelation, sourceEnvelope.GetCausationId());
    }

    // 3. New root — align to the ambient trace so Whizbang correlation == the OTel trace.
    return (CorrelationId.NewRootAligned(), null);
  }

  /// <summary>
  /// Creates a new CascadeContext with the specified metadata added/updated.
  /// </summary>
  /// <param name="key">The metadata key</param>
  /// <param name="value">The metadata value</param>
  /// <returns>A new CascadeContext with the metadata added</returns>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:WithMetadata_SingleKey_AddsMetadataAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:WithMetadata_ExistingMetadata_MergesCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:WithMetadata_OverwritesExistingKey_WhenSameKeyProvidedAsync</tests>
  public CascadeContext WithMetadata(string key, object value) {
    var newMetadata = Metadata is not null
      ? new Dictionary<string, object>(Metadata) { [key] = value }
      : new Dictionary<string, object> { [key] = value };

    return this with { Metadata = newMetadata };
  }

  /// <summary>
  /// Creates a new CascadeContext with the specified metadata merged.
  /// </summary>
  /// <param name="additional">Additional metadata to merge</param>
  /// <returns>A new CascadeContext with the metadata merged</returns>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextTests.cs:WithMetadata_Dictionary_MergesAllEntriesAsync</tests>
  public CascadeContext WithMetadata(IReadOnlyDictionary<string, object> additional) {
    if (additional is null || additional.Count == 0) {
      return this;
    }

    var newMetadata = Metadata is not null
      ? new Dictionary<string, object>(Metadata)
      : [];

    foreach (var kvp in additional) {
      newMetadata[kvp.Key] = kvp.Value;
    }

    return this with { Metadata = newMetadata };
  }
}
