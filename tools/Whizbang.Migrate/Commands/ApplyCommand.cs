using Microsoft.Extensions.FileSystemGlobbing;
using Whizbang.Migrate.Analysis;
using Whizbang.Migrate.Transformers;
using Whizbang.Migrate.Wizard;

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
  private readonly EventStoreTransformer _eventStoreTransformer = new();
  private readonly GuidToIdProviderTransformer _guidTransformer = new();
  private readonly DIRegistrationTransformer _diTransformer = new();
  private readonly MarkerInterfaceTransformer _markerInterfaceTransformer = new();

  /// <summary>
  /// Executes the apply command on the specified directory.
  /// </summary>
  /// <param name="projectPath">Path to the project directory.</param>
  /// <param name="dryRun">If true, reports changes without modifying files.</param>
  /// <param name="includePatterns">Glob patterns for files to include.</param>
  /// <param name="excludePatterns">Glob patterns for files to exclude.</param>
  /// <param name="decisionFile">Optional decision file for controlling migration behavior.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>The apply result.</returns>
  public async Task<ApplyResult> ExecuteAsync(
      string projectPath,
      bool dryRun = false,
      string[]? includePatterns = null,
      string[]? excludePatterns = null,
      DecisionFile? decisionFile = null,
      CancellationToken ct = default) {
    if (!Directory.Exists(projectPath)) {
      return new ApplyResult(false, ErrorMessage: $"Directory not found: {projectPath}");
    }

    var fileChanges = new List<FileChange>();
    var transformedCount = 0;
    var skippedCount = 0;

    // Find all C# files recursively
    var csFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories);

    // Apply include/exclude filters
    var filesToProcess = _filterFiles(csFiles, projectPath, includePatterns, excludePatterns);

    foreach (var file in filesToProcess) {
      ct.ThrowIfCancellationRequested();

      // Check decision file for skip decisions
      if (decisionFile != null) {
        var handlerDecision = decisionFile.GetHandlerDecision(file);
        var projectionDecision = decisionFile.GetProjectionDecision(file);

        if (handlerDecision == DecisionChoice.Skip && projectionDecision == DecisionChoice.Skip) {
          skippedCount++;
          continue;
        }
      }

      var sourceCode = await File.ReadAllTextAsync(file, ct);
      var allChanges = new List<CodeChange>();
      var transformedCode = sourceCode;

      // Check if file has Wolverine handlers
      var wolverineResult = await _wolverineAnalyzer.AnalyzeAsync(sourceCode, file, ct);
      if (wolverineResult.Handlers.Count > 0) {
        var shouldTransform = decisionFile == null ||
            decisionFile.GetHandlerDecision(file) != DecisionChoice.Skip;

        if (shouldTransform) {
          var handlerResult = await _handlerTransformer.TransformAsync(transformedCode, file, ct);
          transformedCode = handlerResult.TransformedCode;
          allChanges.AddRange(handlerResult.Changes);
        }
      }

      // Check if file has Marten projections
      var martenResult = await _martenAnalyzer.AnalyzeAsync(transformedCode, file, ct);
      if (martenResult.Projections.Count > 0) {
        var shouldTransform = decisionFile == null ||
            decisionFile.GetProjectionDecision(file) != DecisionChoice.Skip;

        if (shouldTransform) {
          var projectionResult = await _projectionTransformer.TransformAsync(transformedCode, file, ct);
          transformedCode = projectionResult.TransformedCode;
          allChanges.AddRange(projectionResult.Changes);
        }
      }

      // Apply EventStore transformations (IDocumentStore → IEventStore, session patterns)
      var eventStoreResult = await _eventStoreTransformer.TransformAsync(transformedCode, file, ct);
      if (eventStoreResult.Changes.Count > 0) {
        transformedCode = eventStoreResult.TransformedCode;
        allChanges.AddRange(eventStoreResult.Changes);
      }

      // Apply Guid → IWhizbangIdProvider transformations
      if (decisionFile == null ||
          decisionFile.Decisions.IdGeneration.GuidNewGuid != DecisionChoice.Skip) {
        var guidResult = await _guidTransformer.TransformAsync(transformedCode, file, ct);
        if (guidResult.Changes.Count > 0) {
          transformedCode = guidResult.TransformedCode;
          allChanges.AddRange(guidResult.Changes);
        }
      }

      // Apply DI registration transformations (for Program.cs and startup files)
      var diResult = await _diTransformer.TransformAsync(transformedCode, file, ct);
      if (diResult.Changes.Count > 0) {
        transformedCode = diResult.TransformedCode;
        allChanges.AddRange(diResult.Changes);
      }

      // Apply marker interface transformations (IEvent, ICommand from Wolverine → Whizbang.Core)
      // This catches files that only have marker interface usage without other Wolverine patterns
      var markerResult = await _markerInterfaceTransformer.TransformAsync(transformedCode, file, ct);
      if (markerResult.Changes.Count > 0) {
        transformedCode = markerResult.TransformedCode;
        allChanges.AddRange(markerResult.Changes);
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

    // Calculate skipped from filtering
    skippedCount += csFiles.Length - filesToProcess.Count;

    return new ApplyResult(
        true,
        transformedCount,
        fileChanges,
        SkippedFileCount: skippedCount);
  }

  private static List<string> _filterFiles(
      string[] allFiles,
      string basePath,
      string[]? includePatterns,
      string[]? excludePatterns) {
    // If no patterns specified, return all files (except obj/bin by default)
    if ((includePatterns == null || includePatterns.Length == 0) &&
        (excludePatterns == null || excludePatterns.Length == 0)) {
      return allFiles
          .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                      !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
          .ToList();
    }

    var matcher = new Matcher();

    // Add include patterns (default to **/*.cs if none specified)
    if (includePatterns == null || includePatterns.Length == 0) {
      matcher.AddInclude("**/*.cs");
    } else {
      foreach (var pattern in includePatterns) {
        matcher.AddInclude(pattern);
      }
    }

    // Add exclude patterns (always exclude obj/bin)
    matcher.AddExclude("**/obj/**");
    matcher.AddExclude("**/bin/**");
    if (excludePatterns != null) {
      foreach (var pattern in excludePatterns) {
        matcher.AddExclude(pattern);
      }
    }

    var result = matcher.Execute(new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(
        new DirectoryInfo(basePath)));

    return result.Files
        .Select(f => Path.Combine(basePath, f.Path))
        .ToList();
  }
}

/// <summary>
/// Result of the apply command.
/// </summary>
/// <param name="Success">Whether the apply completed successfully.</param>
/// <param name="TransformedFileCount">Number of files transformed.</param>
/// <param name="Changes">List of changes made.</param>
/// <param name="SkippedFileCount">Number of files skipped due to filters or decisions.</param>
/// <param name="ErrorMessage">Error message if the apply failed.</param>
public sealed record ApplyResult(
    bool Success,
    int TransformedFileCount = 0,
    IReadOnlyList<FileChange>? Changes = null,
    int SkippedFileCount = 0,
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
