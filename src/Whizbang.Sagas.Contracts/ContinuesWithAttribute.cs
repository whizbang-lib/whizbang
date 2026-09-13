namespace Whizbang.Sagas;

/// <summary>
/// Declares that another saga starts when the saga carrying this attribute finishes.
/// </summary>
/// <remarks>
/// <para>
/// Repeatable: a saga can be followed by several, each with its own trigger. The generator reads the
/// declarations and emits the registration, so nothing is discovered by reflection at runtime.
/// </para>
/// <para>
/// The framework asks for the continuation rather than initiating it. It does not know the follow-on
/// saga's item set, and a saga's items are the consumer's domain, so it publishes
/// <c>SagaContinuationRequestedEvent</c> and the consumer's receptor decides what the run contains.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/continuations</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaContinuationTests.cs</tests>
/// <example>
/// <code>
/// // Enrichment over what the import wrote, which has no reason to run while the import is running.
/// [Saga("BulkImport")]
/// [ContinuesWith("DerivedEnrichment")]
/// [ContinuesWith("ImportCleanup", SagaContinuationTriggers.Failed)]
/// public partial class BulkImportSaga;
/// </code>
/// </example>
/// <param name="sagaName">The name of the saga to start, as passed to its <c>[Saga("Name")]</c>.</param>
/// <param name="trigger">
/// Which terminal statuses start it. Defaults to <see cref="SagaContinuationTriggers.RanToTheEnd"/>,
/// because a partially failed run still produced state worth acting on.
/// </param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ContinuesWithAttribute(
    string sagaName,
    SagaContinuationTriggers trigger = SagaContinuationTriggers.RanToTheEnd) : Attribute {
  /// <summary>The name of the saga to start.</summary>
  public string SagaName { get; } = sagaName;

  /// <summary>Which terminal statuses of the declaring saga start it.</summary>
  public SagaContinuationTriggers Trigger { get; } = trigger;
}
