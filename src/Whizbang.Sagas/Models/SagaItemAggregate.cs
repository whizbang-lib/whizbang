namespace Whizbang.Sagas.Models;

/// <summary>
/// Snapshot of a saga's item states, computed by a single aggregate
/// query over the items projection. Drives saga-side completion
/// detection — replaces in-memory counters with a queryable shape that
/// stays correct under cross-pod restarts and rebalances.
/// </summary>
/// <param name="Total">Total item rows for the saga.</param>
/// <param name="Completed">Items in <see cref="SagaItemState.Completed"/>.</param>
/// <param name="Failed">Items in <see cref="SagaItemState.Failed"/>.</param>
/// <param name="InProgress">Items still in <see cref="SagaItemState.Pending"/> or <see cref="SagaItemState.Running"/>.</param>
public sealed record SagaItemAggregate(int Total, int Completed, int Failed, int InProgress);
