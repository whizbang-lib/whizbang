namespace Whizbang.Core;

/// <summary>
/// Marks an event as <strong>compacted</strong> — a <em>permanent</em> StateBased carry-forward origin (E3
/// Tier-2). Like <see cref="IEphemeralEvent"/> it is StateBased (its current state, not the log, is the source
/// of truth, so it is never replayed / <c>RebuildFromEvents</c>) — but <strong>unlike</strong> it, it is
/// <em>permanent</em>: the authoritative frozen model at a state-based stream's head, never reaped. The flag
/// deriver stamps <see cref="Messaging.EventFlags.Compacted"/> for it, so the reaper (keyed on
/// <see cref="Messaging.EventFlags.Ephemeral"/> = self-destruct) never touches it, while the rebuild/rewind
/// guards (keyed on StateBased) still refuse to replay it. The framework's <see cref="Perspectives.Compacted"/>
/// carry-forward event implements this; a compaction produces it, not a developer.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public interface ICompactedEvent : IEvent;
