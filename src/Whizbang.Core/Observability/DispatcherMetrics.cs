using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Metrics for the dispatcher: Send, Publish, LocalInvoke, cascade, perspective sync.
/// Meter name: Whizbang.Dispatcher
/// </summary>
/// <docs>operations/observability/metrics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/DispatcherMetricsTests.cs</tests>
public sealed class DispatcherMetrics {
#pragma warning disable CA1707
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.Dispatcher";
#pragma warning restore CA1707

  // Dispatch timing (end-to-end per dispatch pattern)

  /// <summary>SendAsync duration: envelope creation, receptor, cascade, and lifecycle.</summary>
  public Histogram<double> SendDuration { get; }

  /// <summary>PublishAsync duration: local handlers, outbox queue, and flush.</summary>
  public Histogram<double> PublishDuration { get; }

  /// <summary>LocalInvokeAsync duration: receptor invocation only.</summary>
  public Histogram<double> LocalInvokeDuration { get; }

  /// <summary>LocalInvokeAndSyncAsync duration: invoke plus perspective wait.</summary>
  public Histogram<double> LocalInvokeAndSyncDuration { get; }

  /// <summary>CascadeMessageAsync duration: extraction, routing, outbox/local.</summary>
  public Histogram<double> CascadeDuration { get; }

  /// <summary>SendManyAsync batch dispatch total duration.</summary>
  public Histogram<double> SendManyDuration { get; }

  // Sub-operation timing

  /// <summary>Time in receptor delegate invocation.</summary>
  public Histogram<double> ReceptorDuration { get; }

  /// <summary>MessageExtractor.ExtractMessagesWithRouting duration.</summary>
  public Histogram<double> CascadeExtractionDuration { get; }

  /// <summary>_awaitPerspectiveSyncIfNeededAsync wait time.</summary>
  public Histogram<double> PerspectiveSyncDuration { get; }

  /// <summary>_waitForPerspectivesIfNeededAsync post-dispatch duration.</summary>
  public Histogram<double> PerspectiveWaitDuration { get; }

  /// <summary>_serializeToNewOutboxMessage JSON serialization duration.</summary>
  public Histogram<double> SerializationDuration { get; }

  /// <summary>_processTagsIfEnabledAsync execution duration.</summary>
  public Histogram<double> TagProcessingDuration { get; }

  // Throughput counters

  /// <summary>Total messages dispatched.</summary>
  public Counter<long> MessagesDispatched { get; }

  /// <summary>Events cascaded from receptor results.</summary>
  public Counter<long> EventsCascaded { get; }

  /// <summary>Messages serialized for outbox.</summary>
  public Counter<long> MessagesSerialized { get; }

  /// <summary>Inbox dedup rejections.</summary>
  public Counter<long> DuplicatesDetected { get; }

  /// <summary>Perspective sync wait timeouts.</summary>
  public Counter<long> PerspectiveSyncTimeouts { get; }

  /// <summary>Dispatch-level errors.</summary>
  public Counter<long> Errors { get; }

  // Batch metrics

  /// <summary>Events extracted per cascade.</summary>
  public Histogram<int> CascadeEventCount { get; }

  /// <summary>Messages per SendMany call.</summary>
  public Histogram<int> SendManyBatchSize { get; }

  /// <summary>Initializes a new instance of the <see cref="DispatcherMetrics"/> class.</summary>
  /// <param name="whizbangMetrics">The shared metrics factory providing the meter.</param>
  public DispatcherMetrics(WhizbangMetrics whizbangMetrics) {
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    SendDuration = meter.CreateHistogram<double>("whizbang.dispatcher.send.duration", "ms", "SendAsync: envelope creation → receptor → cascade → lifecycle");
    PublishDuration = meter.CreateHistogram<double>("whizbang.dispatcher.publish.duration", "ms", "PublishAsync: local handlers → outbox queue → flush");
    LocalInvokeDuration = meter.CreateHistogram<double>("whizbang.dispatcher.local_invoke.duration", "ms", "LocalInvokeAsync: receptor invocation only");
    LocalInvokeAndSyncDuration = meter.CreateHistogram<double>("whizbang.dispatcher.local_invoke_and_sync.duration", "ms", "LocalInvokeAndSyncAsync: invoke + perspective wait");
    CascadeDuration = meter.CreateHistogram<double>("whizbang.dispatcher.cascade.duration", "ms", "CascadeMessageAsync: extraction → routing → outbox/local");
    SendManyDuration = meter.CreateHistogram<double>("whizbang.dispatcher.send_many.duration", "ms", "SendManyAsync: batch dispatch total");

    ReceptorDuration = meter.CreateHistogram<double>("whizbang.dispatcher.receptor.duration", "ms", "Time in receptor delegate invocation");
    CascadeExtractionDuration = meter.CreateHistogram<double>("whizbang.dispatcher.cascade_extraction.duration", "ms", "MessageExtractor.ExtractMessagesWithRouting");
    PerspectiveSyncDuration = meter.CreateHistogram<double>("whizbang.dispatcher.perspective_sync.duration", "ms", "_awaitPerspectiveSyncIfNeededAsync wait time");
    PerspectiveWaitDuration = meter.CreateHistogram<double>("whizbang.dispatcher.perspective_wait.duration", "ms", "_waitForPerspectivesIfNeededAsync (post-dispatch)");
    SerializationDuration = meter.CreateHistogram<double>("whizbang.dispatcher.serialization.duration", "ms", "_serializeToNewOutboxMessage JSON serialization");
    TagProcessingDuration = meter.CreateHistogram<double>("whizbang.dispatcher.tag_processing.duration", "ms", "_processTagsIfEnabledAsync execution");

    MessagesDispatched = meter.CreateCounter<long>("whizbang.dispatcher.messages_dispatched", description: "Total messages dispatched");
    EventsCascaded = meter.CreateCounter<long>("whizbang.dispatcher.events_cascaded", description: "Events cascaded from receptor results");
    MessagesSerialized = meter.CreateCounter<long>("whizbang.dispatcher.messages_serialized", description: "Messages serialized for outbox");
    DuplicatesDetected = meter.CreateCounter<long>("whizbang.dispatcher.duplicates_detected", description: "Inbox dedup rejections");
    PerspectiveSyncTimeouts = meter.CreateCounter<long>("whizbang.dispatcher.perspective_sync_timeouts", description: "Perspective sync wait timeouts");
    Errors = meter.CreateCounter<long>("whizbang.dispatcher.errors", description: "Dispatch-level errors");

    CascadeEventCount = meter.CreateHistogram<int>("whizbang.dispatcher.cascade.event_count", description: "Events extracted per cascade");
    SendManyBatchSize = meter.CreateHistogram<int>("whizbang.dispatcher.send_many.batch_size", description: "Messages per SendMany call");
  }
}
