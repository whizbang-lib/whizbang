namespace Whizbang.Data.EFCore.Postgres.Entities;

/// <summary>
/// Permanent deduplication tracking for idempotent delivery guarantees.
/// Records are never deleted - this table grows forever.
/// Maps to wh_message_deduplication table.
/// </summary>
public class MessageDeduplicationRecord {
  /// <summary>
  /// Message ID (UUIDv7).
  /// Primary key.
  /// </summary>
  public required Guid MessageId { get; set; }

  /// <summary>
  /// UTC timestamp when this message was first received.
  /// </summary>
  public required DateTimeOffset FirstSeenAt { get; set; }
}
