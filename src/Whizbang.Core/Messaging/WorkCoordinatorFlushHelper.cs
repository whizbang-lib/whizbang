using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Observability;
using Whizbang.Core.Tracing;

namespace Whizbang.Core.Messaging;

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
#pragma warning disable S107 // Methods should not have too many parameters
  internal static async Task<WorkBatch> ExecuteFlushAsync(
    IWorkCoordinator? coordinator,
    IServiceScopeFactory? scopeFactory,
    IServiceInstanceProvider instanceProvider,
    WorkCoordinatorOptions options,
    string strategyName,
    OutboxMessage[] outboxMessages,
    InboxMessage[] inboxMessages,
    MessageCompletion[] outboxCompletions,
    MessageCompletion[] inboxCompletions,
    MessageFailure[] outboxFailures,
    MessageFailure[] inboxFailures,
    WorkBatchFlags flags,
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer,
    ILogger? logger,
    IOptionsMonitor<TracingOptions>? tracingOptions,
    WorkCoordinatorMetrics? metrics,
    LifecycleMetrics? lifecycleMetrics,
    CancellationToken ct
  ) {
#pragma warning restore S107
    // Resolve coordinator: use direct reference if available, otherwise create a scope
    IServiceScope? flushScope = null;
    IWorkCoordinator resolvedCoordinator;
    if (coordinator != null) {
      resolvedCoordinator = coordinator;
    } else {
      flushScope = scopeFactory!.CreateScope();
      resolvedCoordinator = flushScope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    }

    try {
      // Check if lifecycle tracing is enabled
      var enableLifecycleTracing = tracingOptions?.CurrentValue.IsEnabled(TraceComponents.Lifecycle) ?? false;

      // PreDistribute lifecycle stages (before ProcessWorkBatchAsync)
      await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
        LifecycleStage.PreDistributeAsync,
        LifecycleStage.PreDistributeInline,
        outboxMessages,
        inboxMessages,
        scopeFactory,
        lifecycleMessageDeserializer,
        logger,
        enableLifecycleTracing: enableLifecycleTracing,
        metrics: lifecycleMetrics,
        ct: ct
      );

      // DistributeAsync lifecycle stage (fire in parallel with ProcessWorkBatchAsync, non-blocking)
      LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(
        LifecycleStage.DistributeAsync,
        outboxMessages,
        inboxMessages,
        scopeFactory,
        lifecycleMessageDeserializer,
        logger,
        enableLifecycleTracing: enableLifecycleTracing,
        metrics: lifecycleMetrics,
        ct: ct
      );

      // Call process_work_batch with snapshot
      var request = new ProcessWorkBatchRequest {
        InstanceId = instanceProvider.InstanceId,
        ServiceName = instanceProvider.ServiceName,
        HostName = instanceProvider.HostName,
        ProcessId = instanceProvider.ProcessId,
        Metadata = null,
        OutboxCompletions = outboxCompletions,
        OutboxFailures = outboxFailures,
        InboxCompletions = inboxCompletions,
        InboxFailures = inboxFailures,
        ReceptorCompletions = [],  // FUTURE: Add receptor processing support
        ReceptorFailures = [],
        PerspectiveCompletions = [],  // FUTURE: Add perspective cursor support
        PerspectiveEventCompletions = [],
        PerspectiveFailures = [],
        NewOutboxMessages = outboxMessages,
        NewInboxMessages = inboxMessages,
        RenewOutboxLeaseIds = [],
        RenewInboxLeaseIds = [],
        Flags = flags | (options.DebugMode ? WorkBatchFlags.DebugMode : WorkBatchFlags.None),
        PartitionCount = options.PartitionCount,
        LeaseSeconds = options.LeaseSeconds,
        StaleThresholdSeconds = options.StaleThresholdSeconds
      };
      var flushSw = System.Diagnostics.Stopwatch.StartNew();
      var workBatch = await resolvedCoordinator.ProcessWorkBatchAsync(request, ct);
      flushSw.Stop();
      metrics?.FlushDuration.Record(flushSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("strategy", strategyName));

      // PostDistribute lifecycle stages (after ProcessWorkBatchAsync)
      await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
        LifecycleStage.PostDistributeAsync,
        LifecycleStage.PostDistributeInline,
        outboxMessages,
        inboxMessages,
        scopeFactory,
        lifecycleMessageDeserializer,
        logger,
        enableLifecycleTracing: enableLifecycleTracing,
        metrics: lifecycleMetrics,
        ct: ct
      );

      return workBatch;
    } finally {
      flushScope?.Dispose();
    }
  }
}
