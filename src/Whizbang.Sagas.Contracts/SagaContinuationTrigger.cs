namespace Whizbang.Sagas;

/// <summary>
/// Which terminal statuses of one saga start the saga declared to follow it.
/// </summary>
/// <remarks>
/// <para>
/// A flag rather than a single value because the useful answers are combinations. Follow-on work over
/// what a run produced applies whether or not every item succeeded, while compensating work applies
/// only when the run was abandoned, and both shapes are ordinary.
/// </para>
/// <para>
/// Only terminal statuses appear here. <see cref="SagaStatus.Running"/> means the saga is still
/// writing, and <see cref="SagaStatus.Reset"/> is a transition marker rather than a resting state, so
/// starting a follow-on from either would run it against a half-written set.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/continuations</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaContinuationTests.cs</tests>
[Flags]
public enum SagaContinuationTrigger {
  /// <summary>Never start the continuation. A declaration nobody wants active.</summary>
  None = 0,

  /// <summary>Start when every item succeeded (<see cref="SagaStatus.Completed"/>).</summary>
  Completed = 1,

  /// <summary>
  /// Start when the saga ran to the end but some items failed
  /// (<see cref="SagaStatus.CompletedWithFailures"/>).
  /// </summary>
  CompletedWithFailures = 2,

  /// <summary>
  /// Start when the saga was abandoned before all items finished
  /// (<see cref="SagaStatus.Failed"/>). The compensating case.
  /// </summary>
  Failed = 4,

  /// <summary>
  /// Start whenever the saga ran to the end, succeeded outright or not. The default, because a
  /// partially failed run still produced the state the follow-on work applies to, while an abandoned
  /// one did not.
  /// </summary>
  RanToTheEnd = Completed | CompletedWithFailures,
}
