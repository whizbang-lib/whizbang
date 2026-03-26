namespace Whizbang.Core.Registry;

/// <summary>
/// Registry for IStreamIdExtractor contributions from multiple assemblies.
/// Each assembly registers its generated extractor via [ModuleInitializer].
/// </summary>
/// <remarks>
/// <para>
/// This is a convenience wrapper around <see cref="AssemblyRegistry{T}"/> for <see cref="IStreamIdExtractor"/>
/// with a composite that tries each extractor until one returns non-null.
/// </para>
/// <para>
/// <strong>How it works:</strong>
/// <list type="number">
/// <item>Contracts assembly loads → [ModuleInitializer] runs → registers extractors (priority 100)</item>
/// <item>Service assembly loads → [ModuleInitializer] runs → no extractors, no registration</item>
/// <item>AddWhizbangDispatcher() → AddWhizbangStreamIdExtractor() → registers composite</item>
/// <item>Composite tries all registered extractors → Contracts extractor succeeds</item>
/// </list>
/// </para>
/// </remarks>
/// <docs>fundamentals/messages/delivery-receipts</docs>
/// <tests>tests/Whizbang.Core.Tests/Registry/StreamIdExtractorRegistryTests.cs</tests>
public static class StreamIdExtractorRegistry {
  /// <summary>
  /// Register an extractor. Called from [ModuleInitializer] in generated code.
  /// </summary>
  /// <param name="extractor">The extractor to register</param>
  /// <param name="priority">Lower = tried first. Use 100 for contracts, 1000 for services.</param>
  public static void Register(IStreamIdExtractor extractor, int priority = 1000) {
    AssemblyRegistry<IStreamIdExtractor>.Register(extractor, priority);
  }

  /// <summary>
  /// Extract stream ID by trying all registered extractors in priority order.
  /// Returns the first non-null result, or null if all extractors return null.
  /// </summary>
  /// <param name="message">The message to extract from</param>
  /// <param name="messageType">The type of the message</param>
  /// <returns>The stream ID if found, otherwise null</returns>
  public static Guid? ExtractStreamId(object message, Type messageType) {
    foreach (var extractor in AssemblyRegistry<IStreamIdExtractor>.GetOrderedContributions()) {
      var result = extractor.ExtractStreamId(message, messageType);
      if (result.HasValue) {
        return result;
      }
    }
    return null;
  }

  /// <summary>
  /// Get a singleton IStreamIdExtractor that delegates to the registry.
  /// Use this for DI registration.
  /// </summary>
  /// <returns>A composite extractor that tries all registered extractors</returns>
  public static IStreamIdExtractor GetComposite() => _compositeInstance.Value;

  private static readonly Lazy<IStreamIdExtractor> _compositeInstance = new(
      () => new CompositeStreamIdExtractor());

  /// <summary>
  /// Count of registered extractors (for diagnostics/testing).
  /// </summary>
  public static int Count => AssemblyRegistry<IStreamIdExtractor>.Count;

  /// <summary>
  /// Composite IStreamIdExtractor that delegates to the registry.
  /// </summary>
  /// <summary>
  /// Get generation policy by trying all registered extractors in priority order.
  /// Returns the first (ShouldGenerate=true) result, or (false, false) if none match.
  /// </summary>
  public static (bool ShouldGenerate, bool OnlyIfEmpty) GetGenerationPolicy(object message) {
    foreach (var extractor in AssemblyRegistry<IStreamIdExtractor>.GetOrderedContributions()) {
      var result = extractor.GetGenerationPolicy(message);
      if (result.ShouldGenerate) {
        return result;
      }
    }
    return (false, false);
  }

  /// <summary>
  /// Set stream ID by trying all registered extractors in priority order.
  /// Returns true if an extractor successfully set the value.
  /// </summary>
  public static bool SetStreamId(object message, Guid streamId) {
    return AssemblyRegistry<IStreamIdExtractor>.GetOrderedContributions()
        .Any(extractor => extractor.SetStreamId(message, streamId));
  }

  private sealed class CompositeStreamIdExtractor : IStreamIdExtractor {
    Guid? IStreamIdExtractor.ExtractStreamId(object message, Type messageType) =>
        StreamIdExtractorRegistry.ExtractStreamId(message, messageType);

    (bool ShouldGenerate, bool OnlyIfEmpty) IStreamIdExtractor.GetGenerationPolicy(object message) =>
        StreamIdExtractorRegistry.GetGenerationPolicy(message);

    bool IStreamIdExtractor.SetStreamId(object message, Guid streamId) =>
        StreamIdExtractorRegistry.SetStreamId(message, streamId);
  }
}
