using Whizbang.Core;

namespace Whizbang.Sagas;

/// <summary>
/// Marker interface for events that compensate for earlier saga work
/// (refund a payment, un-archive a job, restore a soft-delete). The
/// framework does not automatically execute compensation — consumers
/// wire their own <c>IReceptor&lt;TCompensating&gt;</c> chains and decide
/// the ordering. The marker exists so saga visualization tooling and
/// audit pipelines can group compensation activity separately from
/// forward-path activity.
/// </summary>
/// <remarks>
/// <para>
/// Whizbang.Sagas does not ship an automated compensation engine
/// because the "right" compensation order is irreducibly
/// domain-specific — refunding a payment must happen before un-archiving
/// the associated order in one workflow, but the reverse in another.
/// Engines that try to encode this generically (Sagas-as-state-machine
/// frameworks) consistently end up forcing one model on every
/// consumer.
/// </para>
/// <para>
/// What the marker buys you: a stable, framework-recognized "this is a
/// compensating action" signal that tooling can filter on without
/// every consumer inventing its own convention.
/// </para>
/// </remarks>
public interface ISagaCompensatingEvent : IEvent {

  /// <summary>Stream id of the saga whose forward work this event compensates.</summary>
  Guid CompensatingForSagaId { get; }
}
