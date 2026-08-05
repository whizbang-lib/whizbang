using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Worker-facing seam for applying a single
/// <see cref="ICollectiveEvent"/>. Composes the four moving parts from
/// Slices 4–7a + 7b's executor layer into one call: scope resolver
/// lookup, entry fan-out, per-<c>TModel</c> executor dispatch, affected-row
/// aggregation.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerCollectiveSinkTests.cs:CollectiveSink_DispatchesEventOnceAndSkipsRunner_Async</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerCollectiveSinkTests.cs:CollectiveSink_LongApply_RenewsWorkLeasePerReportedBatchAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/EFCoreCollectiveDiUnitTests.cs:AddCollectiveEventsEFCore_RegistersDispatcherResolverAccessorAsync</tests>
public interface ICollectiveDispatcher {
  /// <summary>
  /// Dispatch a collective event to every matching perspective handler.
  /// </summary>
  /// <param name="evt">The collective event being applied. The
  /// dispatcher uses <see cref="ICollectiveEvent.Scope"/> to find the
  /// right resolver and the event's runtime type to find every
  /// matching <see cref="CollectiveApplyEntry"/>.</param>
  /// <param name="collectiveEventId">Unique id of this collective event.
  /// Written by the executor to the audit-pointer column on every
  /// affected row in the same SQL UPDATE that performs the mutation.</param>
  /// <param name="dbContextOrSession">Driver-specific session
  /// (<c>DbContext</c> for EF Core, <c>NpgsqlConnection</c> for
  /// Dapper). Forwarded as <see cref="object"/> to each executor —
  /// each driver casts to its expected type.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <param name="onBatchApplied">Optional per-batch progress callback, invoked by the driver after
  /// each batched UPDATE commits. The worker that owns the leased work item uses it to renew the
  /// lease DURING a long multi-batch apply — without it, an apply spanning many batches outlives
  /// its lease and the work is redelivered (idempotently, but wastefully). Null = no reporting.</param>
  Task<CollectiveDispatchResult> DispatchAsync(
    ICollectiveEvent evt,
    Guid collectiveEventId,
    object dbContextOrSession,
    Func<CancellationToken, ValueTask>? onBatchApplied = null,
    CancellationToken cancellationToken = default);
}

/// <summary>
/// Aggregate outcome of a single
/// <see cref="ICollectiveDispatcher.DispatchAsync"/> call.
/// </summary>
/// <param name="HandlerCount">How many <see cref="CollectiveApplyEntry"/>
/// instances matched the event and were invoked. Zero means no
/// perspective was subscribed to the event type — not an error
/// (producers may emit events before a consumer perspective is
/// deployed).</param>
/// <param name="AffectedRowCount">Sum of the affected-row counts each
/// executor reported. Useful for logging / metrics; not a correctness
/// signal (zero affected rows is a valid outcome when the matched-set
/// was empty at write time or when every row was already at the target
/// state).</param>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveDispatcherTests.cs:DispatchAsync_OneEntry_InvokesExecutorOnceAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveDispatcherTests.cs:DispatchAsync_TwoEntriesSameEventDifferentModels_FansOutAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveDispatcherTests.cs:DispatchAsync_NoMatchingEntry_ReturnsZeroAsync</tests>
public sealed record CollectiveDispatchResult(int HandlerCount, int AffectedRowCount);
