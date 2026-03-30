namespace Whizbang.Core.Observability;

/// <summary>
/// Enriches cascade context with additional data before propagation.
/// Implementations are invoked in registration order during context creation via CascadeContextFactory.
/// </summary>
/// <remarks>
/// Use this interface to inject custom data into the cascade context.
/// Each enricher receives the context and returns a (possibly modified) context.
/// Enrichers should be stateless and idempotent.
///
/// Example use cases:
/// - Adding correlation metadata from custom headers
/// - Injecting feature flags or experiment IDs
/// - Adding custom tenant-specific context
///
/// Enrichers are registered via DI and automatically collected by CascadeContextFactory:
/// <code>
/// services.AddSingleton&lt;ICascadeContextEnricher, MyCustomEnricher&gt;();
/// </code>
/// </remarks>
/// <docs>fundamentals/messages/cascade-context#enrichers</docs>
/// <tests>tests/Whizbang.Observability.Tests/CascadeContextFactoryTests.cs</tests>
public interface ICascadeContextEnricher {
  /// <summary>
  /// Enriches the cascade context with additional data.
  /// </summary>
  /// <param name="context">The context to enrich</param>
  /// <param name="sourceEnvelope">The source envelope (may be null for root contexts)</param>
  /// <returns>The enriched context (return same instance if no changes needed)</returns>
  /// <remarks>
  /// Implementations should:
  /// - Return the same context if no enrichment is needed
  /// - Use the `with` expression or `WithMetadata()` to create modified copies
  /// - Not throw exceptions (log and return original context if enrichment fails)
  /// - Be thread-safe and stateless
  /// </remarks>
  CascadeContext Enrich(CascadeContext context, IMessageEnvelope? sourceEnvelope);
}
