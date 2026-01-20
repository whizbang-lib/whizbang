namespace Whizbang.Migrate.Analysis;

/// <summary>
/// Interface for code analyzers that detect migration patterns.
/// </summary>
public interface ICodeAnalyzer {
  /// <summary>
  /// Analyzes source code for patterns that need migration.
  /// </summary>
  /// <param name="sourceCode">The C# source code to analyze.</param>
  /// <param name="filePath">The file path (for reporting).</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>Analysis result for the source code.</returns>
  Task<AnalysisResult> AnalyzeAsync(string sourceCode, string filePath, CancellationToken ct = default);

  /// <summary>
  /// Analyzes a project for patterns that need migration.
  /// </summary>
  /// <param name="projectPath">Path to the .csproj file.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>Aggregated analysis result for the project.</returns>
  Task<AnalysisResult> AnalyzeProjectAsync(string projectPath, CancellationToken ct = default);
}
