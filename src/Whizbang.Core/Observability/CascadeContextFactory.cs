using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Observability;

/// <summary>
/// Factory for creating CascadeContext from various sources.
/// Centralizes context extraction and enrichment logic.
/// </summary>
/// <remarks>
/// This factory provides a consistent way to create cascade contexts:
/// - NewRoot(): For entry points starting new message flows
/// - FromEnvelope(): For extracting context from incoming messages
/// - FromMessageContext(): For creating context from existing IMessageContext
///
/// All methods apply registered enrichers in order, allowing extensibility.
///
/// Register as a singleton in DI:
/// <code>
/// services.AddSingleton&lt;CascadeContextFactory&gt;();
/// services.AddSingleton&lt;ICascadeContextEnricher, MyEnricher&gt;();
/// </code>
/// </remarks>
/// <docs>core-concepts/cascade-context#factory</docs>
/// <tests>tests/Whizbang.Observability.Tests/CascadeContextFactoryTests.cs</tests>
public sealed class CascadeContextFactory {
  private readonly IEnumerable<ICascadeContextEnricher> _enrichers;

  /// <summary>
  /// Creates a new factory with the specified enrichers.
  /// </summary>
  /// <param name="enrichers">Enrichers to apply during context creation (can be null or empty)</param>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextFactoryTests.cs:Constructor_WithNullEnrichers_CreatesFactoryWithEmptyEnrichersAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextFactoryTests.cs:Constructor_WithEmptyEnrichers_CreatesFactoryAsync</tests>
  public CascadeContextFactory(IEnumerable<ICascadeContextEnricher>? enrichers) {
    _enrichers = enrichers ?? [];
  }

  /// <summary>
  /// Creates cascade context from a message envelope.
  /// Extracts CorrelationId from first hop, sets CausationId to envelope's MessageId,
  /// and inherits SecurityContext from ambient scope (preferred) or envelope.
  /// </summary>
  /// <param name="envelope">The source envelope</param>
  /// <returns>A new CascadeContext with extracted data</returns>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextFactoryTests.cs:FromEnvelope_ExtractsCorrelationIdFromFirstHopAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextFactoryTests.cs:FromEnvelope_SetsCausationIdToEnvelopeMessageIdAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextFactoryTests.cs:FromEnvelope_PrefersAmbientSecurityOverEnvelopeSecurityAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextFactoryTests.cs:FromEnvelope_FallsBackToEnvelopeSecurity_WhenNoAmbientAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextFactoryTests.cs:FromEnvelope_GeneratesCorrelationId_WhenEnvelopeHasNoneAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextFactoryTests.cs:FromEnvelope_ThrowsOnNullEnvelopeAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextFactoryTests.cs:FromEnvelope_AppliesEnrichersWithEnvelopeAsync</tests>
  public CascadeContext FromEnvelope(IMessageEnvelope envelope) {
    ArgumentNullException.ThrowIfNull(envelope);

    var correlationId = envelope.GetCorrelationId() ?? CorrelationId.New();
    var causationId = envelope.MessageId;

    // Prefer ambient security, fall back to envelope's security
    var securityContext = CascadeContext.GetSecurityFromAmbient()
      ?? envelope.GetCurrentSecurityContext();

    var context = new CascadeContext {
      CorrelationId = correlationId,
      CausationId = causationId,
      SecurityContext = securityContext
    };

    return _applyEnrichers(context, envelope);
  }

  /// <summary>
  /// Creates cascade context from an IMessageContext.
  /// </summary>
  /// <param name="messageContext">The source message context</param>
  /// <returns>A new CascadeContext with extracted data</returns>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextFactoryTests.cs:FromMessageContext_CopiesCorrelationIdAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextFactoryTests.cs:FromMessageContext_SetsCausationIdToMessageContextMessageIdAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextFactoryTests.cs:FromMessageContext_CopiesSecurityFromMessageContextAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextFactoryTests.cs:FromMessageContext_FallsBackToAmbientSecurity_WhenMessageContextHasNoSecurityAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextFactoryTests.cs:FromMessageContext_ThrowsOnNullMessageContextAsync</tests>
  public CascadeContext FromMessageContext(IMessageContext messageContext) {
    ArgumentNullException.ThrowIfNull(messageContext);

    // Extract security from message context, fall back to ambient
    var securityContext = (messageContext.UserId is not null || messageContext.TenantId is not null)
      ? new SecurityContext { UserId = messageContext.UserId, TenantId = messageContext.TenantId }
      : CascadeContext.GetSecurityFromAmbient();

    var context = new CascadeContext {
      CorrelationId = messageContext.CorrelationId,
      CausationId = messageContext.MessageId,
      SecurityContext = securityContext
    };

    return _applyEnrichers(context, sourceEnvelope: null);
  }

  /// <summary>
  /// Creates a new root cascade context with ambient security.
  /// Used when starting a new message flow.
  /// </summary>
  /// <returns>A new root CascadeContext</returns>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextFactoryTests.cs:NewRoot_GeneratesNewIdentifiersAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextFactoryTests.cs:NewRoot_WithAmbientSecurity_InheritsSecurityAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/CascadeContextFactoryTests.cs:NewRoot_AppliesEnrichersAsync</tests>
  public CascadeContext NewRoot() {
    var context = CascadeContext.NewRootWithAmbientSecurity();
    return _applyEnrichers(context, sourceEnvelope: null);
  }

  private CascadeContext _applyEnrichers(CascadeContext context, IMessageEnvelope? sourceEnvelope) {
    foreach (var enricher in _enrichers) {
      context = enricher.Enrich(context, sourceEnvelope);
    }
    return context;
  }
}
