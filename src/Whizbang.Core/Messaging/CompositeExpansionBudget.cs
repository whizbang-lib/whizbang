namespace Whizbang.Core.Messaging;

/// <summary>
/// Bounds how many child rows one composite expansion may add to a consumer in a single step.
/// </summary>
/// <remarks>
/// <para>
/// Composite fan-out expands a received composite into child inbox rows. That expansion happens
/// INSIDE the consumer, after admission control has already accepted the message, so one accepted
/// message can add an unbounded number of rows to the inbox.
/// </para>
/// <para>
/// Observed in production: single composites expanding into tens of thousands of children, the
/// largest past two hundred thousand. A consumer's inbox climbed for hours against an empty broker
/// with no upstream producer, because the growth was local expansion of work already held. The
/// producers in the same deployment emitted composites of at most sixteen rows, so composites
/// inflate in transit rather than at emission.
/// </para>
/// <para>
/// The consequence is not only depth. Claim windows, outstanding-row budgets and lease sizing are
/// all calibrated in inbox rows, so a single expansion can invalidate every downstream bound at
/// once.
/// </para>
/// <para>
/// This budget is about CONSUMER CAPACITY, not message validity. A composite can be perfectly
/// well-formed and still be more than one consumer should absorb in a single step. Oversized
/// composites are therefore chunked rather than rejected — the inner events are real work, and
/// refusing them would convert an absorption problem into data loss.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeExpansionBudgetTests.cs</tests>
public sealed class CompositeExpansionBudget {

  /// <summary>How an expansion should be carried out.</summary>
  /// <param name="InnerEventCount">Inner events the composite carries.</param>
  /// <param name="Chunks">Expansion steps required; zero when there is nothing to expand.</param>
  /// <param name="ChunkSize">Children per step; the final step may carry fewer.</param>
  /// <param name="ExceedsBudget">
  /// True when the composite needed splitting. Worth logging: a consumer whose inbox grows by tens
  /// of thousands of rows from one message otherwise has nothing attributing the growth.
  /// </param>
  public readonly record struct ExpansionPlan(
    int InnerEventCount, int Chunks, int ChunkSize, bool ExceedsBudget);

  private readonly int _maxChildren;

  /// <summary>Children one expansion step may produce.</summary>
  public int MaxChildrenPerExpansion => _maxChildren;

  /// <summary>Initializes a new instance of the <see cref="CompositeExpansionBudget"/> class.</summary>
  /// <param name="maxChildrenPerExpansion">
  /// Children per expansion step. Must be positive; zero would chunk every composite into
  /// infinitely many empty pieces.
  /// </param>
  public CompositeExpansionBudget(int maxChildrenPerExpansion) {
    ArgumentOutOfRangeException.ThrowIfLessThan(maxChildrenPerExpansion, 1);
    _maxChildren = maxChildrenPerExpansion;
  }

  /// <summary>
  /// Plans an expansion for a composite carrying <paramref name="innerEventCount"/> events.
  /// </summary>
  /// <param name="innerEventCount">Inner events the composite carries.</param>
  /// <returns>The plan, chunked when the composite exceeds the budget.</returns>
  public ExpansionPlan Plan(int innerEventCount) {
    // Negative means the caller miscounted. Treating it as empty would silently discard a composite
    // rather than surfacing the bug.
    ArgumentOutOfRangeException.ThrowIfLessThan(innerEventCount, 0);

    if (innerEventCount == 0) {
      return new ExpansionPlan(0, 0, 0, ExceedsBudget: false);
    }

    if (innerEventCount <= _maxChildren) {
      // Exactly at the budget is within it. An off-by-one here would double the round trips for
      // every composite sized to the documented limit.
      return new ExpansionPlan(innerEventCount, Chunks: 1, ChunkSize: innerEventCount, ExceedsBudget: false);
    }

    // Ceiling division: a floor would drop the final partial chunk and lose those events.
    var chunks = (innerEventCount + _maxChildren - 1) / _maxChildren;
    return new ExpansionPlan(innerEventCount, chunks, _maxChildren, ExceedsBudget: true);
  }
}
