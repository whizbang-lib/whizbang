namespace Whizbang.Core.Workers;

/// <summary>
/// Configuration for <see cref="InboxDeserializeCache"/>. Slice 15 of plans/pump-then-process.md.
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed class InboxDeserializeCacheOptions {
  /// <summary>
  /// Killswitch — set to <c>false</c> to disable the deserialize cache (every dispatch
  /// re-deserializes from JSON, including the four lifecycle stages of a single message).
  /// Default <c>true</c>.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Time-to-live for an entry. Default 2 minutes — covers transport redelivery and lease
  /// re-claim cycles for a typical busy service. Longer TTL = more memory, fewer re-parses;
  /// shorter TTL = the inverse.
  /// </summary>
  public int TtlMinutes { get; set; } = 2;

  /// <summary>
  /// Hard cap on resident entries. Default 10k — when exceeded, the oldest ~10% evict on
  /// the next insert. At a 2-min TTL the cap is generally moot unless sustained throughput
  /// exceeds ~80 messages/sec for the entire TTL window.
  /// </summary>
  public int MaxEntries { get; set; } = 10_000;
}
