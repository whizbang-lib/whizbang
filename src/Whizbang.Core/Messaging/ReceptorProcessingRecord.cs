namespace Whizbang.Core.Messaging;

/// <summary>
/// Log-style tracking of event processing by receptors.
/// Stored in wh_receptor_processing table.
/// Each event can be processed by multiple receptors independently.
/// </summary>
public sealed class ReceptorProcessingRecord {
  /// <summary>
  /// Unique identifier for this processing record.
  /// </summary>
  public Guid Id { get; set; }

  /// <summary>
  /// The event being processed (foreign key to wh_event_store).
  /// </summary>
  public required Guid EventId { get; set; }

  /// <summary>
  /// Name of the receptor processing this event.
  /// </summary>
  public required string ReceptorName { get; set; }

  /// <summary>
  /// Current processing status flags.
  /// </summary>
  public required ReceptorProcessingStatus Status { get; set; }

  /// <summary>
  /// Number of processing attempts.
  /// </summary>
  public int Attempts { get; set; }

  /// <summary>
  /// Error message if processing failed.
  /// </summary>
  public string? Error { get; set; }

  /// <summary>
  /// When processing started.
  /// </summary>
  public DateTime StartedAt { get; set; }

  /// <summary>
  /// When processing completed (successfully or failed).
  /// </summary>
  public DateTime? ProcessedAt { get; set; }
}
