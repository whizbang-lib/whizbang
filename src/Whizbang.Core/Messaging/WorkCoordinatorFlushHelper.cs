using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Observability;
using Whizbang.Core.Tracing;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Groups the parameters for <see cref="WorkCoordinatorFlushHelper.ExecuteFlushAsync"/>.
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
/// Shared flush logic used by <see cref="IntervalWorkCoordinatorStrategy"/> and <see cref="BatchWorkCoordinatorStrategy"/>
/// to eliminate duplication of the core flush pipeline (coordinator resolution, lifecycle stages, ProcessWorkBatchAsync,
/// metrics recording, and scope cleanup).
/// </summary>
internal static class WorkCoordinatorFlushHelper {
  /// <summary>
  /// Executes the core flush pipeline: resolves the coordinator (direct or via scope factory),
  /// invokes lifecycle stages, calls ProcessWorkBatchAsync, records metrics, and disposes the scope.
  /// </summary>
  /// <remarks>
  /// Callers are responsible for concurrency guards (_flushing flag), queue snapshot/clear,
  /// and resetting _flushing in their own finally block. This method handles coordinator resolution
  /// through scope disposal.
  /// </remarks>
  internal static async Task<WorkBatch> ExecuteFlushAsync(
    FlushContext ctx,
    CancellationToken ct
  ) {
    // Resolve coordinator: use direct reference if available, otherwise create a scope
    IServiceScope? flushScope = null;
    IWorkCoordinator resolvedCoordinator;
    if (ctx.Coordinator != null) {
      resolvedCoordinator = ctx.Coordinator;
    } else {
      flushScope = ctx.ScopeFactory!.CreateScope();
      resolvedCoordinator = flushScope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    }

    try {
      if (!ctx.SkipLifecycle) {
        // Check if lifecycle tracing is enabled
        var enableLifecycleTracing = ctx.TracingOptions?.CurrentValue.IsEnabled(TraceComponents.Lifecycle) ?? false;

        var lifecycleContext = new DistributeLifecycleContext(
          ctx.OutboxMessages, ctx.InboxMessages, ctx.ScopeFactory, ctx.LifecycleMessageDeserializer,
          ctx.Logger, enableLifecycleTracing, ctx.LifecycleMetrics);

        // PreDistribute lifecycle stages (before ProcessWorkBatchAsync)
        await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
          LifecycleStage.PreDistributeDetached,
          LifecycleStage.PreDistributeInline,
          lifecycleContext,
          ct: ct
        );

        // DistributeDetached lifecycle stage (fire in parallel with ProcessWorkBatchAsync, non-blocking)
        LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(
          LifecycleStage.DistributeDetached,
          lifecycleContext,
          ct: ct
        );
      }

      // Merge pending audit messages (after lifecycle stages, before request build)
      var finalOutboxMessages = ctx.OutboxMessages;
      if (ctx.PendingAuditMessages is { Length: > 0 }) {
        finalOutboxMessages = [.. ctx.OutboxMessages, .. ctx.PendingAuditMessages];
      }

      // Call process_work_batch with snapshot
      var request = new ProcessWorkBatchRequest {
        InstanceId = ctx.InstanceProvider.InstanceId,
        ServiceName = ctx.InstanceProvider.ServiceName,
        HostName = ctx.InstanceProvider.HostName,
        ProcessId = ctx.InstanceProvider.ProcessId,
        Metadata = null,
        OutboxCompletions = ctx.OutboxCompletions,
        OutboxFailures = ctx.OutboxFailures,
        InboxCompletions = ctx.InboxCompletions,
        InboxFailures = ctx.InboxFailures,
        ReceptorCompletions = [],  // FUTURE: Add receptor processing support
        ReceptorFailures = [],
        PerspectiveCompletions = [],  // FUTURE: Add perspective cursor support
        PerspectiveEventCompletions = [],
        PerspectiveFailures = [],
        NewOutboxMessages = finalOutboxMessages,
        NewInboxMessages = ctx.InboxMessages,
        RenewOutboxLeaseIds = [],
        RenewInboxLeaseIds = [],
        Flags = ctx.Flags | (ctx.Options.DebugMode ? WorkBatchOptions.DebugMode : WorkBatchOptions.None),
        PartitionCount = ctx.Options.PartitionCount,
        LeaseSeconds = ctx.Options.LeaseSeconds,
        StaleThresholdSeconds = ctx.Options.StaleThresholdSeconds
      };
      var flushSw = System.Diagnostics.Stopwatch.StartNew();
      var workBatch = await resolvedCoordinator.ProcessWorkBatchAsync(request, ct);
      flushSw.Stop();
      ctx.Metrics?.FlushDuration.Record(flushSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("strategy", ctx.StrategyName));

      // PostDistribute lifecycle stages (after ProcessWorkBatchAsync)
      if (!ctx.SkipLifecycle) {
        var enableLifecycleTracingPost = ctx.TracingOptions?.CurrentValue.IsEnabled(TraceComponents.Lifecycle) ?? false;
        var postLifecycleContext = new DistributeLifecycleContext(
          ctx.OutboxMessages, ctx.InboxMessages, ctx.ScopeFactory, ctx.LifecycleMessageDeserializer,
          ctx.Logger, enableLifecycleTracingPost, ctx.LifecycleMetrics);
        await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
          LifecycleStage.PostDistributeDetached,
          LifecycleStage.PostDistributeInline,
          postLifecycleContext,
          ct: ct
        );
      }

      // NOTE: Do NOT write outbox work to channel here — the coordinator loop in
      // WorkCoordinatorPublisherWorker._processWorkBatchAsync writes to the channel
      // with proper ordering and in-flight tracking (lines 879-892).
      // Signal the coordinator to wake immediately so it picks up the work.
      if (ctx.WorkChannelWriter is not null) {
        if (workBatch.OutboxWork.Count > 0) {
          ctx.WorkChannelWriter.SignalNewWorkAvailable();
        }
        if (workBatch.PerspectiveWork.Count > 0) {
          ctx.WorkChannelWriter.SignalNewPerspectiveWorkAvailable();
        }
      }

      return workBatch;
    } finally {
      flushScope?.Dispose();
    }
  }
}
