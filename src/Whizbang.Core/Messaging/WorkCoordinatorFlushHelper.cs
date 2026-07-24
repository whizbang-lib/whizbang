using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Observability;
using Whizbang.Core.Tracing;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Groups the parameters for <see cref="WorkCoordinatorFlushHelper.ExecuteFlushAsync"/>.
/// Most fields are kept for source compatibility with strategy call sites; the new-path
/// helper only consumes the ones documented below.
/// </summary>
internal readonly record struct FlushContext(
  IWorkCoordinator? Coordinator,
  IServiceScopeFactory? ScopeFactory,
  IServiceInstanceProvider InstanceProvider,
  WorkCoordinatorOptions Options,
  string StrategyName,
  OutboxMessage[] OutboxMessages,
  InboxMessage[] InboxMessages,
  MessageCompletion[] OutboxCompletions,
  MessageCompletion[] InboxCompletions,
  MessageFailure[] OutboxFailures,
  MessageFailure[] InboxFailures,
  WorkBatchOptions Flags,
  ILifecycleMessageDeserializer? LifecycleMessageDeserializer,
  ILogger? Logger,
  IOptionsMonitor<TracingOptions>? TracingOptions,
  WorkCoordinatorMetrics? Metrics,
  LifecycleMetrics? LifecycleMetrics,
  IWorkChannelWriter? WorkChannelWriter,
  OutboxMessage[]? PendingAuditMessages,
  bool SkipLifecycle = false);

/// <summary>
/// Shim used by the four <see cref="IWorkCoordinatorStrategy"/> implementations to flush
/// their queued operations through the new (post-Phase-H) work-pump path.
/// </summary>
/// <remarks>
/// The legacy implementation routed every flush through <c>process_work_batch</c>, which
/// inserted messages, recorded completions/failures, and claimed work in one trip.
/// The new path decomposes those responsibilities:
///  - <c>store_outbox_messages</c> / <c>store_inbox_messages</c> insert new rows
///  - <see cref="IOutboxCompletionChannel"/> + <see cref="IFailureChannel"/> handle completions/failures
///  - <c>claim_work</c> is owned by <c>ClaimWorker</c>; nothing is claimed during a flush
///
/// Lifecycle stages, tracing, and audit-message expansion that this helper used to drive
/// during a flush are now driven by <c>OutboxPublishWorker</c> and <c>InboxDispatchWorker</c>
/// when they pick up the inserted rows. The strategy flush path therefore only needs to
/// persist the queued state and signal the publisher to wake.
/// </remarks>
internal static class WorkCoordinatorFlushHelper {
  internal static async Task<WorkBatch> ExecuteFlushAsync(
    FlushContext ctx,
    CancellationToken ct
  ) {
    if (ctx.OutboxMessages.Length == 0 &&
        ctx.InboxMessages.Length == 0 &&
        ctx.OutboxCompletions.Length == 0 &&
        ctx.InboxCompletions.Length == 0 &&
        ctx.OutboxFailures.Length == 0 &&
        ctx.InboxFailures.Length == 0) {
      return _empty;
    }

    IServiceScope? flushScope = null;
    IWorkCoordinator coordinator;
    IServiceProvider? scopedProvider;

    if (ctx.Coordinator is not null) {
      coordinator = ctx.Coordinator;
      scopedProvider = null;
    } else {
      if (ctx.ScopeFactory is null) {
        throw new InvalidOperationException(
          "FlushContext must supply either Coordinator or ScopeFactory.");
      }
      flushScope = ctx.ScopeFactory.CreateScope();
      coordinator = flushScope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
      scopedProvider = flushScope.ServiceProvider;
    }

    try {
      var partitionCount = _resolvePartitionCount(scopedProvider, ctx.Options);

      var enableLifecycleTracing = ctx.TracingOptions?.CurrentValue.IsEnabled(TraceComponents.Lifecycle) ?? false;
      var lifecycleScopeFactory = ctx.ScopeFactory ?? scopedProvider?.GetService<IServiceScopeFactory>();

      var distributeContext = new DistributeLifecycleContext(
        ctx.OutboxMessages,
        ctx.InboxMessages,
        lifecycleScopeFactory,
        ctx.LifecycleMessageDeserializer,
        ctx.Logger,
        enableLifecycleTracing,
        ctx.LifecycleMetrics);

      if (!ctx.SkipLifecycle) {
        await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
          LifecycleStage.PreDistributeDetached,
          LifecycleStage.PreDistributeInline,
          distributeContext,
          ct).ConfigureAwait(false);

        LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(
          LifecycleStage.DistributeDetached,
          distributeContext,
          ct);
      }

      var outboxToStore = ctx.OutboxMessages;
      if (ctx.PendingAuditMessages is { Length: > 0 }) {
        outboxToStore = [.. ctx.OutboxMessages, .. ctx.PendingAuditMessages];
      }

      if (outboxToStore.Length > 0) {
        await coordinator.StoreOutboxMessagesAsync(outboxToStore, partitionCount, ct).ConfigureAwait(false);
      }

      if (ctx.InboxMessages.Length > 0) {
        await coordinator.StoreInboxMessagesAsync(ctx.InboxMessages, partitionCount, ct).ConfigureAwait(false);
      }

      var completionChannel = scopedProvider?.GetService<IOutboxCompletionChannel>();
      if (completionChannel is not null) {
        foreach (var c in ctx.OutboxCompletions) {
          await completionChannel.EnqueueAsync(c.MessageId, ct).ConfigureAwait(false);
        }
      }

      var failureChannel = scopedProvider?.GetService<IFailureChannel>();
      if (failureChannel is not null) {
        foreach (var f in ctx.OutboxFailures) {
          await failureChannel.EnqueueAsync(WorkCategory.Outbox, f, ct).ConfigureAwait(false);
        }
        foreach (var f in ctx.InboxFailures) {
          await failureChannel.EnqueueAsync(WorkCategory.Inbox, f, ct).ConfigureAwait(false);
        }
      }

      // Wake ClaimWorker immediately so freshly-stored outbox/inbox rows are claimed
      // on this tick instead of after the next 250 ms poll. ClaimWorker subscribes to
      // OnNewWorkAvailable / OnNewInboxWorkAvailable to translate this signal into
      // an immediate poll. Without this, dispatch-then-immediately-read flows see a
      // sub-second perspective lag that reads as "no activity" in the UI.
      if (outboxToStore.Length > 0) {
        ctx.WorkChannelWriter?.SignalNewWorkAvailable();
      }
      if (ctx.InboxMessages.Length > 0) {
        var inboxChannelWriter = scopedProvider?.GetService<IInboxChannelWriter>();
        inboxChannelWriter?.SignalNewInboxWorkAvailable();
      }

      if (!ctx.SkipLifecycle) {
        await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
          LifecycleStage.PostDistributeDetached,
          LifecycleStage.PostDistributeInline,
          distributeContext,
          ct).ConfigureAwait(false);
      }
    } finally {
      flushScope?.Dispose();
    }

    return _empty;
  }

  private static int _resolvePartitionCount(IServiceProvider? scopedProvider, WorkCoordinatorOptions fallback) {
    if (scopedProvider is null) {
      return fallback.PartitionCount > 0 ? fallback.PartitionCount : 10000;
    }
    var claimOptions = scopedProvider.GetService<IOptions<ClaimWorkerOptions>>()?.Value;
    if (claimOptions is not null && claimOptions.PartitionCount > 0) {
      return claimOptions.PartitionCount;
    }
    return fallback.PartitionCount > 0 ? fallback.PartitionCount : 10000;
  }

  private static readonly WorkBatch _empty = new() {
    OutboxWork = [],
    InboxWork = [],
    PerspectiveWork = []
  };
}
