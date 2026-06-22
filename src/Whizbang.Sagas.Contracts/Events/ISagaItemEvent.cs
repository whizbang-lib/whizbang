using Whizbang.Core;

namespace Whizbang.Sagas;

/// <summary>
/// Base interface for events that target a single saga item rather than
/// the saga as a whole. Item events ride per-item streams (see
/// <c>SagaItemStreams.Of</c>) so concurrent items don't contend for the
/// saga's stream lease.
/// </summary>
public interface ISagaItemEvent : IEvent {

  /// <summary>Saga name (same as on parent saga events).</summary>
  string SagaName { get; }

  /// <summary>Consumer-domain entity id carried for routing and filtering.</summary>
  Guid EntityId { get; }

  /// <summary>Owning saga's stream id — the link from per-item stream back to the saga aggregate.</summary>
  Guid SagaId { get; }

  /// <summary>Caller-supplied stable identifier for this item.</summary>
  string ItemIdentifier { get; }

  /// <summary>Optional human-readable name for UI / logs.</summary>
  string? DisplayName { get; }
}
