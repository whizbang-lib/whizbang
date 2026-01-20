using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Migrate.Core;

namespace Whizbang.Migrate.Journal;

/// <summary>
/// JSON-based implementation of <see cref="IMigrationJournal"/> that persists
/// migration state to a JSON file for idempotent resumption.
/// </summary>
/// <docs>migration-guide/automated-migration</docs>
public sealed class JsonMigrationJournal : IMigrationJournal {
  private const string JOURNAL_VERSION = "1.0.0";

  private readonly string _filePath;
  private JournalData _data = new();

  /// <summary>
  /// Creates a new instance of <see cref="JsonMigrationJournal"/>.
  /// </summary>
  /// <param name="filePath">Path to the journal JSON file.</param>
  public JsonMigrationJournal(string filePath) {
    _filePath = filePath;
  }

  /// <inheritdoc />
  public JournalStatus Status => _data.Status;

  /// <inheritdoc />
  public WorktreeInfo? Worktree => _data.Worktree;

  /// <inheritdoc />
  public IReadOnlyList<Checkpoint> Checkpoints => _data.Checkpoints;

  /// <inheritdoc />
  public IReadOnlyList<TransformationRecord> Transformations => _data.Transformations;

  /// <inheritdoc />
  public async Task LoadAsync(CancellationToken ct = default) {
    if (!File.Exists(_filePath)) {
      return;
    }

    await using var stream = File.OpenRead(_filePath);
    var loaded = await JsonSerializer.DeserializeAsync(stream, JournalJsonContext.Default.JournalData, ct);

    if (loaded is not null) {
      _data = loaded;
    }
  }

  /// <inheritdoc />
  public async Task SaveAsync(CancellationToken ct = default) {
    var directory = Path.GetDirectoryName(_filePath);
    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) {
      Directory.CreateDirectory(directory);
    }

    _data.Version = JOURNAL_VERSION;

    await using var stream = File.Create(_filePath);
    await JsonSerializer.SerializeAsync(stream, _data, JournalJsonContext.Default.JournalData, ct);
  }

  /// <inheritdoc />
  public void SetWorktree(WorktreeInfo worktree) {
    _data.Worktree = worktree;
    _data.Status = JournalStatus.InProgress;
  }

  /// <inheritdoc />
  public void AddCheckpoint(Checkpoint checkpoint) {
    _data.Checkpoints.Add(checkpoint);
  }

  /// <inheritdoc />
  public void RecordTransformation(TransformationRecord transformation) {
    _data.Transformations.Add(transformation);
  }

  /// <inheritdoc />
  public void UpdateTransformationStatus(string transformerName, TransformationStatus status) {
    var index = _data.Transformations.FindIndex(t => t.TransformerName == transformerName);
    if (index < 0) {
      throw new InvalidOperationException($"Transformation '{transformerName}' not found in journal.");
    }

    var existing = _data.Transformations[index];
    _data.Transformations[index] = existing with {
      Status = status,
      CompletedAt = status == TransformationStatus.Completed ? DateTimeOffset.UtcNow : existing.CompletedAt
    };
  }

  /// <inheritdoc />
  public void MarkComplete() {
    _data.Status = JournalStatus.Completed;
  }

  /// <summary>
  /// Internal data structure for JSON serialization.
  /// </summary>
  internal sealed class JournalData {
    public string Version { get; set; } = JOURNAL_VERSION;
    public JournalStatus Status { get; set; } = JournalStatus.NotStarted;
    public WorktreeInfo? Worktree { get; set; }
    public List<Checkpoint> Checkpoints { get; set; } = [];
    public List<TransformationRecord> Transformations { get; set; } = [];
  }
}

/// <summary>
/// Source-generated JSON serializer context for AOT compatibility.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(JsonMigrationJournal.JournalData))]
[JsonSerializable(typeof(WorktreeInfo))]
[JsonSerializable(typeof(Checkpoint))]
[JsonSerializable(typeof(TransformationRecord))]
[JsonSerializable(typeof(JournalStatus))]
[JsonSerializable(typeof(TransformationStatus))]
internal sealed partial class JournalJsonContext : JsonSerializerContext;
