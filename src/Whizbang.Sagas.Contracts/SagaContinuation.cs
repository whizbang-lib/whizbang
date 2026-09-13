namespace Whizbang.Sagas;

/// <summary>
/// A saga that should start when another saga finishes.
/// </summary>
/// <remarks>
/// <para>
/// Sequencing, not prioritizing. Two workloads belonging to one operation contend for claim capacity
/// when nothing orders them, and declaring the second one less urgent does not order it: inside a
/// bucket the claim selects by arrival, the background bucket holds a floor of every batch, and
/// background work that has waited past the wait target competes as standard. So the second workload
/// keeps taking capacity from the first and becomes more urgent the longer the first runs. What
/// removes the contention is not queuing the second workload until the first is done, which is what a
/// continuation does.
/// </para>
/// <para>
/// Once sequenced the priority question largely answers itself, since the follow-on runs when nothing
/// it would have competed with is left.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/continuations</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaContinuationTests.cs</tests>
/// <example>
/// <code>
/// [Saga("BulkImport")]
/// [ContinuesWith("DerivedEnrichment")]
/// public partial class BulkImportSaga;
/// </code>
/// </example>
public sealed class SagaContinuation {
  /// <summary>Declares a saga to start when the one carrying the declaration finishes.</summary>
  /// <param name="sagaName">
  /// The name of the saga to start, as passed to its <c>[Saga("Name")]</c>.
  /// </param>
  /// <param name="trigger">
  /// Which terminal statuses start it. Defaults to
  /// <see cref="SagaContinuationTriggers.RanToTheEnd"/>.
  /// </param>
  /// <exception cref="ArgumentException"><paramref name="sagaName"/> is blank.</exception>
  public SagaContinuation(
      string sagaName, SagaContinuationTriggers trigger = SagaContinuationTriggers.RanToTheEnd) {
    ArgumentException.ThrowIfNullOrWhiteSpace(sagaName);

    SagaName = sagaName;
    Trigger = trigger;
  }

  /// <summary>The name of the saga to start.</summary>
  public string SagaName { get; }

  /// <summary>Which terminal statuses of the declaring saga start this one.</summary>
  public SagaContinuationTriggers Trigger { get; }

  /// <summary>Whether this continuation starts after a saga that ended in the given status.</summary>
  /// <param name="finalStatus">The declaring saga's final status.</param>
  /// <returns><c>true</c> when the status is terminal and the trigger names it.</returns>
  /// <remarks>
  /// A non-terminal status answers <c>false</c> whatever the trigger says. The saga is either still
  /// writing or passing through a transition marker, and a follow-on started then would read a set
  /// that is still changing.
  /// </remarks>
  public bool StartsAfter(SagaStatus finalStatus) => (Trigger & _triggerFor(finalStatus)) != 0;

  /// <summary>The trigger a final status corresponds to; none when the status is not terminal.</summary>
  private static SagaContinuationTriggers _triggerFor(SagaStatus finalStatus) => finalStatus switch {
    SagaStatus.Completed => SagaContinuationTriggers.Completed,
    SagaStatus.CompletedWithFailures => SagaContinuationTriggers.CompletedWithFailures,
    SagaStatus.Failed => SagaContinuationTriggers.Failed,
    _ => SagaContinuationTriggers.None,
  };
}
