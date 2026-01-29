namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Constants for the dedicated system event stream.
/// System events are stored separately from domain events for clean separation.
/// </summary>
/// <remarks>
/// <para>
/// The system stream uses a <c>$</c> prefix following the convention for system/internal streams
/// (similar to EventStoreDB's <c>$all</c>, <c>$ce-</c> streams).
/// </para>
/// <para>
/// All system events share this single stream, partitioned by event type.
/// This keeps system events isolated from domain event streams.
/// </para>
/// </remarks>
/// <docs>core-concepts/system-events#stream</docs>
public static class SystemEventStreams {
  /// <summary>
  /// The name of the dedicated system event stream.
  /// </summary>
  public static string Name => "$wb-system";

  /// <summary>
  /// Stream prefix for system events (can be used for filtering/subscriptions).
  /// </summary>
  public static string Prefix => "$wb-";

  /// <summary>
  /// The well-known GUID for the system event stream.
  /// Uses a namespace-based UUID (UUIDv5) derived from the stream name for determinism.
  /// </summary>
  /// <remarks>
  /// This is a fixed GUID: 00000000-0000-0000-0000-000000000001 (System stream)
  /// Using a known GUID ensures all system events go to the same stream.
  /// </remarks>
  public static Guid StreamId { get; } = new Guid("00000000-0000-0000-0000-000000000001");
}
