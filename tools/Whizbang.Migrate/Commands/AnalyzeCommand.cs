using Whizbang.Migrate.Analysis;

namespace Whizbang.Migrate.Commands;

/// <summary>
/// Command that analyzes a project for migration patterns.
/// </summary>
/// <docs>migration-guide/automated-migration</docs>
public sealed class AnalyzeCommand {
  private readonly WolverineAnalyzer _wolverineAnalyzer = new();
  private readonly MartenAnalyzer _martenAnalyzer = new();

  /// <summary>
  /// Executes the analyze command on the specified directory.
  /// </summary>
  /// <param name="projectPath">Path to the project directory.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>The analysis result.</returns>
  public async Task<AnalyzeResult> ExecuteAsync(
      string projectPath,
      CancellationToken ct = default) {
    if (!Directory.Exists(projectPath)) {
      return new AnalyzeResult(false, ErrorMessage: $"Directory not found: {projectPath}");
    }

    var wolverineCount = 0;
    var martenCount = 0;

    // Find all C# files recursively
    var csFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories);

    foreach (var file in csFiles) {
      ct.ThrowIfCancellationRequested();

      var sourceCode = await File.ReadAllTextAsync(file, ct);

      // Analyze with Wolverine analyzer
      var wolverineResult = await _wolverineAnalyzer.AnalyzeAsync(sourceCode, file, ct);
      wolverineCount += wolverineResult.Handlers.Count;

      // Analyze with Marten analyzer
      var martenResult = await _martenAnalyzer.AnalyzeAsync(sourceCode, file, ct);
      martenCount += martenResult.Projections.Count;
    }

    return new AnalyzeResult(
        true,
        wolverineCount,
        martenCount);
  }
}

/// <summary>
/// Result of the analyze command.
/// </summary>
/// <param name="Success">Whether the analysis completed successfully.</param>
/// <param name="WolverineHandlerCount">Number of Wolverine handlers found.</param>
/// <param name="MartenProjectionCount">Number of Marten projections found.</param>
/// <param name="ErrorMessage">Error message if the analysis failed.</param>
public sealed record AnalyzeResult(
    bool Success,
    int WolverineHandlerCount = 0,
    int MartenProjectionCount = 0,
    string? ErrorMessage = null) {
  /// <summary>
  /// Gets the total number of migration items found.
  /// </summary>
  public int TotalMigrationItems => WolverineHandlerCount + MartenProjectionCount;
}
