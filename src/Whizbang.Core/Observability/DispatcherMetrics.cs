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
  public const string METER_NAME = "Whizbang.Dispatcher";
#pragma warning restore CA1707

  private readonly Meter _meter;

  // Dispatch timing (end-to-end per dispatch pattern)
  public Histogram<double> SendDuration { get; }
  public Histogram<double> PublishDuration { get; }
  public Histogram<double> LocalInvokeDuration { get; }
  public Histogram<double> LocalInvokeAndSyncDuration { get; }
  public Histogram<double> CascadeDuration { get; }
  public Histogram<double> SendManyDuration { get; }

  // Sub-operation timing
  public Histogram<double> ReceptorDuration { get; }
  public Histogram<double> CascadeExtractionDuration { get; }
  public Histogram<double> PerspectiveSyncDuration { get; }
  public Histogram<double> PerspectiveWaitDuration { get; }
  public Histogram<double> SerializationDuration { get; }
  public Histogram<double> TagProcessingDuration { get; }

  // Throughput counters
  public Counter<long> MessagesDispatched { get; }
  public Counter<long> EventsCascaded { get; }
  public Counter<long> MessagesSerialized { get; }
  public Counter<long> DuplicatesDetected { get; }
  public Counter<long> PerspectiveSyncTimeouts { get; }
  public Counter<long> Errors { get; }

  // Batch metrics
  public Histogram<int> CascadeEventCount { get; }
  public Histogram<int> SendManyBatchSize { get; }

  public DispatcherMetrics(WhizbangMetrics whizbangMetrics) {
    _meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    SendDuration = _meter.CreateHistogram<double>("whizbang.dispatcher.send.duration", "ms", "SendAsync: envelope creation → receptor → cascade → lifecycle");
    PublishDuration = _meter.CreateHistogram<double>("whizbang.dispatcher.publish.duration", "ms", "PublishAsync: local handlers → outbox queue → flush");
    LocalInvokeDuration = _meter.CreateHistogram<double>("whizbang.dispatcher.local_invoke.duration", "ms", "LocalInvokeAsync: receptor invocation only");
    LocalInvokeAndSyncDuration = _meter.CreateHistogram<double>("whizbang.dispatcher.local_invoke_and_sync.duration", "ms", "LocalInvokeAndSyncAsync: invoke + perspective wait");
    CascadeDuration = _meter.CreateHistogram<double>("whizbang.dispatcher.cascade.duration", "ms", "CascadeMessageAsync: extraction → routing → outbox/local");
    SendManyDuration = _meter.CreateHistogram<double>("whizbang.dispatcher.send_many.duration", "ms", "SendManyAsync: batch dispatch total");

    ReceptorDuration = _meter.CreateHistogram<double>("whizbang.dispatcher.receptor.duration", "ms", "Time in receptor delegate invocation");
    CascadeExtractionDuration = _meter.CreateHistogram<double>("whizbang.dispatcher.cascade_extraction.duration", "ms", "MessageExtractor.ExtractMessagesWithRouting");
    PerspectiveSyncDuration = _meter.CreateHistogram<double>("whizbang.dispatcher.perspective_sync.duration", "ms", "_awaitPerspectiveSyncIfNeededAsync wait time");
    PerspectiveWaitDuration = _meter.CreateHistogram<double>("whizbang.dispatcher.perspective_wait.duration", "ms", "_waitForPerspectivesIfNeededAsync (post-dispatch)");
    SerializationDuration = _meter.CreateHistogram<double>("whizbang.dispatcher.serialization.duration", "ms", "_serializeToNewOutboxMessage JSON serialization");
    TagProcessingDuration = _meter.CreateHistogram<double>("whizbang.dispatcher.tag_processing.duration", "ms", "_processTagsIfEnabledAsync execution");

    MessagesDispatched = _meter.CreateCounter<long>("whizbang.dispatcher.messages_dispatched", description: "Total messages dispatched");
    EventsCascaded = _meter.CreateCounter<long>("whizbang.dispatcher.events_cascaded", description: "Events cascaded from receptor results");
    MessagesSerialized = _meter.CreateCounter<long>("whizbang.dispatcher.messages_serialized", description: "Messages serialized for outbox");
    DuplicatesDetected = _meter.CreateCounter<long>("whizbang.dispatcher.duplicates_detected", description: "Inbox dedup rejections");
    PerspectiveSyncTimeouts = _meter.CreateCounter<long>("whizbang.dispatcher.perspective_sync_timeouts", description: "Perspective sync wait timeouts");
    Errors = _meter.CreateCounter<long>("whizbang.dispatcher.errors", description: "Dispatch-level errors");

    CascadeEventCount = _meter.CreateHistogram<int>("whizbang.dispatcher.cascade.event_count", description: "Events extracted per cascade");
    SendManyBatchSize = _meter.CreateHistogram<int>("whizbang.dispatcher.send_many.batch_size", description: "Messages per SendMany call");
  }
}
