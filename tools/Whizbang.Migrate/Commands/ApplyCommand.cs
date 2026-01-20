using Whizbang.Migrate.Analysis;
using Whizbang.Migrate.Transformers;

namespace Whizbang.Migrate.Commands;

/// <summary>
/// Command that applies migrations to a project.
/// </summary>
/// <docs>migration-guide/automated-migration</docs>
public sealed class ApplyCommand {
  private readonly WolverineAnalyzer _wolverineAnalyzer = new();
  private readonly MartenAnalyzer _martenAnalyzer = new();
  private readonly HandlerToReceptorTransformer _handlerTransformer = new();
  private readonly ProjectionToPerspectiveTransformer _projectionTransformer = new();
  private readonly DIRegistrationTransformer _diTransformer = new();

  /// <summary>
  /// Executes the apply command on the specified directory.
  /// </summary>
  /// <param name="projectPath">Path to the project directory.</param>
  /// <param name="dryRun">If true, reports changes without modifying files.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>The apply result.</returns>
  public async Task<ApplyResult> ExecuteAsync(
      string projectPath,
      bool dryRun = false,
      CancellationToken ct = default) {
    if (!Directory.Exists(projectPath)) {
      return new ApplyResult(false, ErrorMessage: $"Directory not found: {projectPath}");
    }

    var fileChanges = new List<FileChange>();
    var transformedCount = 0;

    // Find all C# files recursively
    var csFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories);

    foreach (var file in csFiles) {
      ct.ThrowIfCancellationRequested();

      var sourceCode = await File.ReadAllTextAsync(file, ct);
      var allChanges = new List<CodeChange>();
      var transformedCode = sourceCode;

      // Check if file has Wolverine handlers
      var wolverineResult = await _wolverineAnalyzer.AnalyzeAsync(sourceCode, file, ct);
      if (wolverineResult.Handlers.Count > 0) {
        var handlerResult = await _handlerTransformer.TransformAsync(transformedCode, file, ct);
        transformedCode = handlerResult.TransformedCode;
        allChanges.AddRange(handlerResult.Changes);
      }

      // Check if file has Marten projections
      var martenResult = await _martenAnalyzer.AnalyzeAsync(transformedCode, file, ct);
      if (martenResult.Projections.Count > 0) {
        var projectionResult = await _projectionTransformer.TransformAsync(transformedCode, file, ct);
        transformedCode = projectionResult.TransformedCode;
        allChanges.AddRange(projectionResult.Changes);
      }

      // Apply DI registration transformations (for Program.cs and startup files)
      var diResult = await _diTransformer.TransformAsync(transformedCode, file, ct);
      if (diResult.Changes.Count > 0) {
        transformedCode = diResult.TransformedCode;
        allChanges.AddRange(diResult.Changes);
      }

      // Track changes
      if (allChanges.Count > 0) {
        fileChanges.Add(new FileChange(file, allChanges.Count, allChanges));
        transformedCount++;

        // Write changes if not dry run
        if (!dryRun) {
          await File.WriteAllTextAsync(file, transformedCode, ct);
        }
      }
    }

    return new ApplyResult(
        true,
        transformedCount,
        fileChanges);
  }
}

/// <summary>
/// Result of the apply command.
/// </summary>
/// <param name="Success">Whether the apply completed successfully.</param>
/// <param name="TransformedFileCount">Number of files transformed.</param>
/// <param name="Changes">List of changes made.</param>
/// <param name="ErrorMessage">Error message if the apply failed.</param>
public sealed record ApplyResult(
    bool Success,
    int TransformedFileCount = 0,
    IReadOnlyList<FileChange>? Changes = null,
    string? ErrorMessage = null) {
  /// <summary>
  /// Gets the list of changes, never null.
  /// </summary>
  public IReadOnlyList<FileChange> Changes { get; } = Changes ?? [];
}

/// <summary>
/// Represents a change made to a file.
/// </summary>
/// <param name="FilePath">Path to the file that was changed.</param>
/// <param name="ChangeCount">Number of changes made to the file.</param>
/// <param name="Changes">Detailed changes made.</param>
public sealed record FileChange(
    string FilePath,
    int ChangeCount,
    IReadOnlyList<CodeChange> Changes);
