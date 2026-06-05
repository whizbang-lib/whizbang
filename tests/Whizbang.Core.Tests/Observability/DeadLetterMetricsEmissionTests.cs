using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Observability;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Slice 5 of release/v0.645.0-alpha.1 (outbox-DLQ + dual-hash analysis) —
/// audits and regression-locks the <see cref="DeadLetterMetrics"/> OTel emission.
///
/// <para>The hidden risk:
/// <see cref="DeadLetterMetrics"/> has been registered as a singleton in the
/// worker pipeline for months. Counters are exposed on a public surface and
/// referenced by operator dashboards, but nothing in the test suite verified
/// the <c>Added</c> counter ACTUALLY increments end-to-end through a worker
/// path. A silently-broken meter would have left operator dashboards flat
/// indefinitely.</para>
///
/// <para>Locked invariants — DLQ promotion through
/// <see cref="OutboxPublishWorker"/> fires <c>DeadLetterMetrics.Added</c>:</para>
/// <list type="bullet">
/// <item><description>Counter name <c>whizbang.dead_letters.added</c> on meter
/// <c>Whizbang.DeadLetters</c>.</description></item>
/// <item><description>Value is +1 per promotion (one row → one metric event).</description></item>
/// <item><description>Tagged <c>source_table=wh_outbox</c> + <c>reason=MaxAttemptsExceeded</c>
/// so PromQL queries can slice by failure-mode + source.</description></item>
/// </list>
///
/// <para>OutboxDrainWorker's pre-publish gate (separate code path) and
/// InboxDispatchWorker / PerspectiveWorker emissions are still audited in
/// Slice 7's broader coverage sweep — this slice locks the legacy publisher
/// path that Slice 3b added so the new code is in the regression net from
/// day 1.</para>
/// </summary>
/// <docs>operations/dead-letter-queue/metrics</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class DeadLetterMetricsEmissionTests {

  // --- fakes (compact copies of Slice 3b's fixtures; tests stay self-contained) ---

  private sealed class _FakeWorkChannelWriter : IWorkChannelWriter {
    private readonly Channel<OutboxWork> _channel = Channel.CreateUnbounded<OutboxWork>();
    public ChannelReader<OutboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(OutboxWork work) => _channel.Writer.TryWrite(work);
    public void Complete() => _channel.Writer.Complete();
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public void ClearInFlight() { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public event Action? OnNewWorkAvailable;
    public void SignalNewWorkAvailable() => OnNewWorkAvailable?.Invoke();
    public event Action? OnNewPerspectiveWorkAvailable;
    public void SignalNewPerspectiveWorkAvailable() => OnNewPerspectiveWorkAvailable?.Invoke();
  }

  private sealed class _NoOpCompletionChannel : IOutboxCompletionChannel {
    public ValueTask EnqueueAsync(Guid id, CancellationToken ct = default) => ValueTask.CompletedTask;
  }

  private sealed class _NoOpFailureChannel : IFailureChannel {
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) => ValueTask.CompletedTask;
  }

  private sealed class _NoOpLeaseRenewalChannel : ILeaseRenewalChannel {
    public ValueTask EnqueueAsync(WorkCategory category, Guid id, CancellationToken ct = default) => ValueTask.CompletedTask;
  }

  private sealed class _FailingPublishStrategy : IMessagePublishStrategy {
    public TaskCompletionSource<OutboxWork> AttemptedPublish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) {
      AttemptedPublish.TrySetResult(work);
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = false,
        CompletedStatus = work.Status,
        Error = "simulated transport failure for OTel emission test",
        Reason = MessageFailureReason.Unknown,
      });
    }
  }

  private sealed class _CapturingDeadLetterStore : IDeadLetterStore {
    public TaskCompletionSource<Guid> FirstMove { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText, Guid instanceId, string generation,
        CancellationToken ct = default) {
      FirstMove.TrySetResult(deadLetterId);
      return Task.FromResult<Guid?>(deadLetterId);
    }
  }

  private sealed class _FakeServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private sealed class _FakeGenerationProvider(string value) : IGenerationProvider {
    public string GetGeneration() => value;
  }

  private sealed record _MetricRecording(string InstrumentName, long Value, IReadOnlyDictionary<string, object?> Tags);

  private static MeterListener _attachListener(ConcurrentBag<_MetricRecording> recordings) {
    var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (instrument.Meter.Name == DeadLetterMetrics.METER_NAME) {
          l.EnableMeasurementEvents(instrument);
        }
      }
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => {
      var dict = new Dictionary<string, object?>(tags.Length);
      foreach (var kvp in tags) { dict[kvp.Key] = kvp.Value; }
      recordings.Add(new _MetricRecording(instrument.Name, value, dict));
    });
    listener.Start();
    return listener;
  }

  private static OutboxWork _work(int attempts) {
    var msgId = (Guid)TrackedGuid.NewMedo();
    return new OutboxWork {
      MessageId = msgId,
      Destination = "test-topic",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = (Guid)TrackedGuid.NewMedo(),
      PartitionNumber = 1,
      Attempts = attempts,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
  }

  // --- tests ---

  [Test]
  public async Task OutboxPublishWorker_PromotesToDlq_EmitsAddedCounterTaggedSourceTableAndReasonAsync() {
    var recordings = new ConcurrentBag<_MetricRecording>();
    using var listener = _attachListener(recordings);

    var whizbangMetrics = new WhizbangMetrics();
    var dlqMetrics = new DeadLetterMetrics(whizbangMetrics);

    var channel = new _FakeWorkChannelWriter();
    var completion = new _NoOpCompletionChannel();
    var failure = new _NoOpFailureChannel();
    var renewal = new _NoOpLeaseRenewalChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var strategy = new _FailingPublishStrategy();
    var dlqStore = new _CapturingDeadLetterStore();

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new OutboxPublishWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      channel, completion, failure, renewal, gate,
      Options.Create(new OutboxPublishWorkerOptions { Enabled = true, MaxOutboxAttempts = 2 }),
      NullLogger<OutboxPublishWorker>.Instance,
      publishStrategy: strategy,
      instanceProvider: new _FakeServiceInstanceProvider(),
      deadLetterStore: dlqStore,
      generationProvider: new _FakeGenerationProvider("test-gen"),
      dlqMetrics: dlqMetrics);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await channel.WriteAsync(_work(attempts: 2), cts.Token);

    await dlqStore.FirstMove.Task.WaitAsync(TimeSpan.FromSeconds(5));
    // The metric increment fires immediately after MoveAsync returns. Yield once to let
    // the worker finish the rest of _routeResultAsync before sampling.
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (recordings.IsEmpty && sw.Elapsed < TimeSpan.FromSeconds(2)) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    var addedEvents = recordings.Where(r => r.InstrumentName == "whizbang.dead_letters.added").ToList();
    await Assert.That(addedEvents.Count).IsEqualTo(1)
      .Because("OutboxPublishWorker's DLQ promotion path MUST fire whizbang.dead_letters.added exactly once per row promoted — without it, operator dashboards never reflect the slot-3-style stuck-row clearance.");
    await Assert.That(addedEvents[0].Value).IsEqualTo(1L)
      .Because("Counter increment is +1 per promotion; bulk increments would skew dashboards counting individual failure modes.");
    await Assert.That(addedEvents[0].Tags.ContainsKey("source_table")).IsTrue()
      .Because("PromQL slicing by source_table is the canonical dashboard query; missing tag would force operators to grep raw logs to distinguish outbox vs inbox DLQ flow.");
    await Assert.That(addedEvents[0].Tags["source_table"]).IsEqualTo("wh_outbox")
      .Because("Promotion via OutboxPublishWorker MUST be tagged wh_outbox, not the inbox/perspective sources.");
    await Assert.That(addedEvents[0].Tags.ContainsKey("reason")).IsTrue()
      .Because("Reason tag lets operators distinguish MaxAttemptsExceeded clusters from other future reasons without separate counters.");
    await Assert.That(addedEvents[0].Tags["reason"]).IsEqualTo("MaxAttemptsExceeded")
      .Because("Slice 3b's promotion fires only on cap-reached, so the reason tag MUST be MaxAttemptsExceeded — any other value indicates a logic regression.");
  }

  [Test]
  public async Task OutboxPublishWorker_NoPromotion_NoAddedCounterEmissionAsync() {
    var recordings = new ConcurrentBag<_MetricRecording>();
    using var listener = _attachListener(recordings);

    var whizbangMetrics = new WhizbangMetrics();
    var dlqMetrics = new DeadLetterMetrics(whizbangMetrics);

    var channel = new _FakeWorkChannelWriter();
    var completion = new _NoOpCompletionChannel();
    var failure = new _NoOpFailureChannel();
    var renewal = new _NoOpLeaseRenewalChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var strategy = new _FailingPublishStrategy();
    var dlqStore = new _CapturingDeadLetterStore();

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new OutboxPublishWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      channel, completion, failure, renewal, gate,
      // MaxOutboxAttempts deliberately UNSET (default null in this test scenario)
      // — promotion gate disabled; failure-channel routing only.
      Options.Create(new OutboxPublishWorkerOptions { Enabled = true, MaxOutboxAttempts = null }),
      NullLogger<OutboxPublishWorker>.Instance,
      publishStrategy: strategy,
      instanceProvider: new _FakeServiceInstanceProvider(),
      deadLetterStore: dlqStore,
      generationProvider: new _FakeGenerationProvider("test-gen"),
      dlqMetrics: dlqMetrics);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await channel.WriteAsync(_work(attempts: 100), cts.Token);

    await strategy.AttemptedPublish.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken.None);  // safety window for any erroneous metric emission

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    var addedEvents = recordings.Where(r => r.InstrumentName == "whizbang.dead_letters.added").ToList();
    await Assert.That(addedEvents).IsEmpty()
      .Because("With MaxOutboxAttempts unset, no DLQ promotion fires, so the Added counter MUST stay silent — emitting a spurious +1 would inflate dashboards for ops who never opted into DLQ.");
  }
}
