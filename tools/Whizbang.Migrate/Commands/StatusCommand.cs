using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Migrate.Core;

namespace Whizbang.Migrate.Commands;

/// <summary>
/// Command that reports migration status.
/// </summary>
/// <docs>migration-guide/automated-migration</docs>
public sealed class StatusCommand {
  private const string JOURNAL_FILE_NAME = ".whizbang-migrate.journal.json";
  private readonly string _journalFileName = JOURNAL_FILE_NAME;

  /// <summary>
  /// Executes the status command on the specified directory.
  /// </summary>
  /// <param name="projectPath">Path to the project directory.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>The status result.</returns>
  public async Task<StatusResult> ExecuteAsync(
      string projectPath,
      CancellationToken ct = default) {
    if (!Directory.Exists(projectPath)) {
      return new StatusResult(false, ErrorMessage: $"Directory not found: {projectPath}");
    }

    var journalPath = Path.Combine(projectPath, _journalFileName);

    if (!File.Exists(journalPath)) {
      return new StatusResult(
          true,
          HasActiveMigration: false,
          Status: JournalStatus.NotStarted);
    }

    try {
      await using var stream = File.OpenRead(journalPath);
      var journalData = await JsonSerializer.DeserializeAsync(
          stream, StatusCommandJsonContext.Default.JournalData, ct);

      if (journalData is null) {
        return new StatusResult(false, ErrorMessage: "Failed to parse journal file");
      }

      var status = journalData.Status.ToLower(System.Globalization.CultureInfo.InvariantCulture) switch {
        "not_started" or "notstarted" => JournalStatus.NotStarted,
        "in_progress" or "inprogress" => JournalStatus.InProgress,
        "completed" => JournalStatus.Completed,
        "failed" => JournalStatus.Failed,
        _ => JournalStatus.NotStarted
      };

      var completedCount = journalData.Transformations?
          .Count(t => t.Status?.Equals("completed", StringComparison.OrdinalIgnoreCase) == true) ?? 0;
      var pendingCount = journalData.Transformations?
          .Count(t => t.Status?.Equals("pending", StringComparison.OrdinalIgnoreCase) == true) ?? 0;
      var totalFiles = journalData.Transformations?
          .Sum(t => t.FilesTransformed) ?? 0;

      return new StatusResult(
          true,
          HasActiveMigration: status == JournalStatus.InProgress,
          Status: status,
          CheckpointCount: journalData.Checkpoints?.Count ?? 0,
          CompletedTransformerCount: completedCount,
          PendingTransformerCount: pendingCount,
          TotalFilesTransformed: totalFiles);
    } catch (JsonException ex) {
      return new StatusResult(false, ErrorMessage: $"Invalid journal file format: {ex.Message}");
    }
  }
}

/// <summary>
/// Internal data model for parsing journal JSON.
/// </summary>
internal sealed class JournalData {
  public string? Version { get; set; }
  public string Status { get; set; } = "not_started";
  public List<CheckpointData>? Checkpoints { get; set; }
  public List<TransformationData>? Transformations { get; set; }
}

internal sealed class CheckpointData {
  public string? Id { get; set; }
  public string? CommitSha { get; set; }
  public string? Description { get; set; }
}

internal sealed class TransformationData {
  public string? TransformerName { get; set; }
  public string? Status { get; set; }
  public int FilesTransformed { get; set; }
}

/// <summary>
/// Source-generated JSON serializer context for AOT compatibility.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(JournalData))]
[JsonSerializable(typeof(CheckpointData))]
[JsonSerializable(typeof(TransformationData))]
internal sealed partial class StatusCommandJsonContext : JsonSerializerContext;

/// <summary>
/// Result of the status command.
/// </summary>
/// <param name="Success">Whether the status check completed successfully.</param>
/// <param name="HasActiveMigration">Whether there is an active migration in progress.</param>
/// <param name="Status">The current migration status.</param>
/// <param name="CheckpointCount">Number of checkpoints created.</param>
/// <param name="CompletedTransformerCount">Number of completed transformers.</param>
/// <param name="PendingTransformerCount">Number of pending transformers.</param>
/// <param name="TotalFilesTransformed">Total files transformed so far.</param>
/// <param name="ErrorMessage">Error message if the status check failed.</param>
public sealed record StatusResult(
    bool Success,
    bool HasActiveMigration = false,
    JournalStatus Status = JournalStatus.NotStarted,
    int CheckpointCount = 0,
    int CompletedTransformerCount = 0,
    int PendingTransformerCount = 0,
    int TotalFilesTransformed = 0,
    string? ErrorMessage = null);
