using System.Text.Json;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Database entity for the event-store BODY table (<c>wh_event_body</c>) — the payload half of the
/// pointer/body split (E1 #13b). The emit chain offloads an event's payload + envelope metadata here,
/// keyed by <c>event_id</c>, leaving the <see cref="EventStoreRecord"/> pointer narrow. An ephemeral
/// event's body is deleted by the tier-1 reaper once consumed and aged past its rewind grace window;
/// the pointer survives as the rebuild-guard signal. Readers resolve body-first with inline fallback.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public sealed class EventBodyRecord {
  /// <summary>
  /// The event this body belongs to (PK, FK-by-convention to <see cref="EventStoreRecord.Id"/>).
  /// </summary>
  public Guid EventId { get; set; }

  /// <summary>
  /// Event payload stored as JSON — same shape as <see cref="EventStoreRecord.EventData"/>.
  /// </summary>
  public required JsonElement EventData { get; set; }

  /// <summary>
  /// Envelope metadata stored as JSON — same shape as <see cref="EventStoreRecord.Metadata"/>.
  /// </summary>
  public required EnvelopeMetadata Metadata { get; set; }
}
