using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>What the publish worker should do with a message after the gate has seen it.</summary>
/// <docs>fundamentals/temporal/pre-fire-hook</docs>
public enum OccurrencePublishDecision {
  /// <summary>Publish normally (the default for every message the gate does not claim).</summary>
  Proceed = 0,

  /// <summary>Do not publish — complete the message as handled (it is dropped, not delivered).</summary>
  Drop = 1,

  /// <summary>Do not publish — the gate has already rescheduled the message for later.</summary>
  Deferred = 2,
}

/// <summary>
/// A gate consulted immediately before a message is published. It exists so the temporal engine can run a
/// developer <c>IScheduleFireHook</c> <em>before a scheduled job actually runs</em> (check security,
/// refresh the run-as authority, skip/cancel/defer), without <see cref="OutboxPublishWorker"/> knowing
/// anything about schedules. The default implementation proceeds for everything; the temporal
/// implementation claims only messages whose metadata marks them as schedule occurrences.
/// </summary>
/// <docs>fundamentals/temporal/pre-fire-hook</docs>
public interface IOccurrencePublishGate {
  /// <summary>Decide what to do with <paramref name="work"/> before it is published.</summary>
  ValueTask<OccurrencePublishDecision> EvaluateAsync(OutboxWork work, CancellationToken cancellationToken = default);
}

/// <summary>The default gate: never claims a message, so publishing is unchanged.</summary>
/// <docs>fundamentals/temporal/pre-fire-hook</docs>
public sealed class NoOpOccurrencePublishGate : IOccurrencePublishGate {
  /// <inheritdoc />
  public ValueTask<OccurrencePublishDecision> EvaluateAsync(OutboxWork work, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(OccurrencePublishDecision.Proceed);
}
