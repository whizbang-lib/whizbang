namespace Whizbang.Migrate.Transformers;

/// <summary>
/// Interface for code transformers that convert migration patterns.
/// </summary>
public interface ICodeTransformer {
  /// <summary>
  /// Transforms source code from one pattern to another.
  /// </summary>
  /// <param name="sourceCode">The source code to transform.</param>
  /// <param name="filePath">File path for context.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>The transformation result.</returns>
  Task<TransformationResult> TransformAsync(
      string sourceCode,
      string filePath,
      CancellationToken ct = default);
}
