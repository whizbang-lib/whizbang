using System.Collections.Concurrent;
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

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Slice 3b of release/v0.645.0-alpha.1 (outbox-DLQ + dual-hash analysis) — locks the
/// invariant that <see cref="OutboxPublishWorker"/> promotes failed rows to
/// <c>wh_dead_letters</c> when the attempts cap is reached. Pre-slice, this legacy
/// worker had zero <c>IDeadLetterStore.MoveAsync</c> calls — failed rows retried
/// forever silently. <see cref="OutboxDrainWorker"/> already has DLQ promotion via
/// a pre-publish gate, but ops who haven't migrated off OutboxPublishWorker (or who
/// flip both for a rollback window) were exposed to the production stuck-row pattern.
///
/// <para>Locked invariants:</para>
/// <list type="bullet">
/// <item><description>When <see cref="OutboxPublishWorkerOptions.MaxOutboxAttempts"/>
/// is set AND <see cref="IDeadLetterStore"/> + <see cref="IGenerationProvider"/> are
/// wired AND publish fails for a row whose <c>Attempts</c> has reached the cap, the
/// worker calls <c>IDeadLetterStore.MoveAsync</c> with the source table
/// <c>wh_outbox</c>, the actual exception text (so Slice 2's SQL fingerprint sees
/// the type + frames), and the running instance/generation tags.</description></item>
/// <item><description>When the row is below the cap, normal failure-channel routing
/// continues (process_outbox_failures bumps attempts).</description></item>
/// <item><description>When DLQ store is null (no DI wiring), the worker silently
/// falls back to failure-channel routing — no crashes, just no promotion.</description></item>
/// </list>
/// </summary>
/// <docs>operations/dead-letter-queue/outbox-dlq-promotion</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class OutboxPublishWorkerDlqPromotionTests {

  // --- fakes ---

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

  private sealed class _FakeOutboxCompletionChannel : IOutboxCompletionChannel {
    public ValueTask EnqueueAsync(Guid id, CancellationToken ct = default) => ValueTask.CompletedTask;
  }

  private sealed class _FakeFailureChannel : IFailureChannel {
    public ConcurrentBag<(WorkCategory cat, MessageFailure f)> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      All.Add((category, failure));
      return ValueTask.CompletedTask;
    }
  }

  private sealed class _FakeLeaseRenewalChannel : ILeaseRenewalChannel {
    public ValueTask EnqueueAsync(WorkCategory category, Guid id, CancellationToken ct = default) => ValueTask.CompletedTask;
  }

  private sealed class _FakeFailingStrategy : IMessagePublishStrategy {
    public TaskCompletionSource<OutboxWork> AttemptedPublish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string ErrorMessage { get; init; } = "simulated transport failure";
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) {
      AttemptedPublish.TrySetResult(work);
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = false,
        CompletedStatus = work.Status,
        Error = ErrorMessage,
        Reason = MessageFailureReason.Unknown,
      });
    }
  }

  private sealed record _MoveCall(
      Guid DeadLetterId,
      string SourceTable,
      Guid SourceId,
      MessageFailureReason FailureReason,
      string? ErrorText,
      Guid InstanceId,
      string Generation);

  private sealed class _FakeDeadLetterStore : IDeadLetterStore {
    public ConcurrentBag<_MoveCall> Moves { get; } = [];
    public TaskCompletionSource<_MoveCall> FirstMove { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<Guid?> MoveAsync(
        Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText,
        Guid instanceId, string generation, CancellationToken ct = default) {
      var call = new _MoveCall(deadLetterId, sourceTable, sourceId, failureReason, errorText, instanceId, generation);
      Moves.Add(call);
      FirstMove.TrySetResult(call);
      return Task.FromResult<Guid?>(deadLetterId);
    }
  }

  private sealed class _FakeGenerationProvider(string value) : IGenerationProvider {
    public string GetGeneration() => value;
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

  // --- helpers ---

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

  private static (OutboxPublishWorker Worker, _FakeWorkChannelWriter Channel, _FakeFailureChannel Failure, _FakeDeadLetterStore Dlq) _build(
      int? maxOutboxAttempts,
      IMessagePublishStrategy strategy,
      bool wireDeadLetterStore = true) {
    var channel = new _FakeWorkChannelWriter();
    var completion = new _FakeOutboxCompletionChannel();
    var failure = new _FakeFailureChannel();
    var renewal = new _FakeLeaseRenewalChannel();
    var dlq = new _FakeDeadLetterStore();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new OutboxPublishWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      channel, completion, failure, renewal, gate,
      Options.Create(new OutboxPublishWorkerOptions {
        Enabled = true,
        MaxOutboxAttempts = maxOutboxAttempts,
      }),
      NullLogger<OutboxPublishWorker>.Instance,
      publishStrategy: strategy,
      instanceProvider: wireDeadLetterStore ? new _FakeServiceInstanceProvider() : null,
      deadLetterStore: wireDeadLetterStore ? dlq : null,
      generationProvider: wireDeadLetterStore ? new _FakeGenerationProvider("test-gen") : null);
    return (worker, channel, failure, dlq);
  }

  // --- tests ---

  [Test]
  public async Task SingularPublish_AttemptsAtCap_PublishFails_PromotesToDlqAsync() {
    var strategy = new _FakeFailingStrategy { ErrorMessage = "simulated transport faulted at cap" };
    var (worker, channel, failure, dlq) = _build(maxOutboxAttempts: 3, strategy);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _work(attempts: 3);  // at cap — this attempt's failure should promote
    await channel.WriteAsync(work, cts.Token);

    var move = await dlq.FirstMove.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(move.SourceTable).IsEqualTo(DeadLetterSourceTable.OUTBOX)
      .Because("Outbox-side failure MUST route to wh_outbox source so move_to_dead_letters DELETEs from the right table.");
    await Assert.That(move.SourceId).IsEqualTo(work.MessageId)
      .Because("Source row's message_id is the identity for the DLQ row.");
    await Assert.That(move.FailureReason).IsEqualTo(MessageFailureReason.MaxAttemptsExceeded)
      .Because("Promotion-at-cap is the MaxAttemptsExceeded reason — Slice 4's recovery policy keys off this.");
    await Assert.That(move.Generation).IsEqualTo("test-gen")
      .Because("Generation tag from IGenerationProvider populates wh_dead_letters.generation for deploy-aware auto-replay.");
    await Assert.That(move.ErrorText).Contains("simulated transport faulted at cap")
      .Because("The transport's error text MUST land in error_text so Slice 2's SQL fingerprint sees the type token.");
    await Assert.That(failure.All).IsEmpty()
      .Because("When DLQ promotion succeeds, the failure-channel enqueue is skipped — the row was DELETEd by move_to_dead_letters, so process_outbox_failures would no-op.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task SingularPublish_BelowCap_PublishFails_RoutesToFailureChannelAsync() {
    var strategy = new _FakeFailingStrategy();
    var (worker, channel, failure, dlq) = _build(maxOutboxAttempts: 5, strategy);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _work(attempts: 1);  // below cap; normal retry semantics
    await channel.WriteAsync(work, cts.Token);

    await strategy.AttemptedPublish.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (failure.All.IsEmpty && sw.Elapsed < TimeSpan.FromSeconds(2)) {
      await Task.Yield();
    }
    await Assert.That(failure.All.Count).IsEqualTo(1)
      .Because("Below the cap, normal failure-channel enqueue MUST fire so process_outbox_failures bumps attempts and the next claim retries.");
    await Assert.That(dlq.Moves).IsEmpty()
      .Because("Promotion fires ONLY when the cap is reached — below-cap failures retry; promoting too aggressively would discard recoverable transient transport faults.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task SingularPublish_AtCap_NoDeadLetterStoreWired_FallsBackToFailureChannelAsync() {
    var strategy = new _FakeFailingStrategy();
    var (worker, channel, failure, dlq) = _build(maxOutboxAttempts: 3, strategy, wireDeadLetterStore: false);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _work(attempts: 3);
    await channel.WriteAsync(work, cts.Token);

    await strategy.AttemptedPublish.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (failure.All.IsEmpty && sw.Elapsed < TimeSpan.FromSeconds(2)) {
      await Task.Yield();
    }
    await Assert.That(failure.All.Count).IsEqualTo(1)
      .Because("When IDeadLetterStore is null (no DI wiring), the worker silently falls back to failure-channel routing — no crashes; ops who never wire DLQ get the legacy behavior.");
    await Assert.That(dlq.Moves).IsEmpty()
      .Because("Defensive sanity check: a null store cannot have recorded calls.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task SingularPublish_AtCap_MaxOutboxAttemptsNotConfigured_RoutesToFailureChannelAsync() {
    var strategy = new _FakeFailingStrategy();
    var (worker, channel, failure, dlq) = _build(maxOutboxAttempts: null, strategy);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _work(attempts: 100);  // way over any cap, but no cap configured
    await channel.WriteAsync(work, cts.Token);

    await strategy.AttemptedPublish.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (failure.All.IsEmpty && sw.Elapsed < TimeSpan.FromSeconds(2)) {
      await Task.Yield();
    }
    await Assert.That(failure.All.Count).IsEqualTo(1)
      .Because("With MaxOutboxAttempts unset (null), the pre-v0.502 behavior is restored: rows retry indefinitely via failure-channel. Operators opt into DLQ promotion by configuring the cap.");
    await Assert.That(dlq.Moves).IsEmpty()
      .Because("No cap configured → no promotion gate → no MoveAsync.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // --- additional fakes for the catch/throw branches ---

  private sealed class _FakeThrowingPublishStrategy(string exceptionMessage) : IMessagePublishStrategy {
    public TaskCompletionSource<OutboxWork> Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) {
      Attempted.TrySetResult(work);
      throw new InvalidOperationException(exceptionMessage);
    }
  }

  private sealed class _FakeBulkThrowingPublishStrategy(string exceptionMessage) : IMessagePublishStrategy {
    public TaskCompletionSource<IReadOnlyList<OutboxWork>> AttemptedBatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool SupportsBulkPublish => true;
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct)
      => throw new InvalidOperationException("FakeBulkThrowingPublishStrategy: only PublishBatchAsync is exercised by this test");
    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> works, CancellationToken ct) {
      AttemptedBatch.TrySetResult(works);
      throw new InvalidOperationException(exceptionMessage);
    }
  }

  private sealed class _ThrowingDeadLetterStore : IDeadLetterStore {
    public TaskCompletionSource<Guid> Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText, Guid instanceId, string generation,
        CancellationToken ct = default) {
      Attempted.TrySetResult(deadLetterId);
      throw new InvalidOperationException("simulated MoveAsync transient DB failure");
    }
  }

  // --- additional tests for catch/throw paths (coverage) ---

  [Test]
  public async Task SingularPublish_AtCap_PublishAsyncThrows_PromotesToDlqWithFullExceptionTextAsync() {
    var strategy = new _FakeThrowingPublishStrategy("simulated transport publish-thrown");
    var (worker, channel, failure, dlq) = _build(maxOutboxAttempts: 2, strategy);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _work(attempts: 2);
    await channel.WriteAsync(work, cts.Token);

    var move = await dlq.FirstMove.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(move.ErrorText).Contains("simulated transport publish-thrown")
      .Because("The thrown exception's full text MUST reach MoveAsync's errorText so Slice 2's SQL fingerprint sees the type token + frames. Plain ex.Message would drop the stack.");
    await Assert.That(move.ErrorText).Contains("InvalidOperationException")
      .Because("Type token in error_text is what Slice 2's fingerprint reads from the first line — preserving it ensures the row clusters correctly with other InvalidOperation faults.");
    await Assert.That(failure.All).IsEmpty()
      .Because("Singular catch promotes-then-skips the failure-channel; the row is already gone from wh_outbox via move_to_dead_letters.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task BulkPublish_AtCap_PublishBatchAsyncThrows_PromotesEachRowIndependentlyAsync() {
    var strategy = new _FakeBulkThrowingPublishStrategy("simulated bulk transport thrown");
    var channel = new _FakeWorkChannelWriter();
    var completion = new _NoOpCompletionChannel();
    var failure = new _FakeFailureChannel();
    var renewal = new _NoOpLeaseRenewalChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var dlq = new _FakeDeadLetterStore();
    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new OutboxPublishWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      channel, completion, failure, renewal, gate,
      Options.Create(new OutboxPublishWorkerOptions { Enabled = true, MaxOutboxAttempts = 2 }),
      NullLogger<OutboxPublishWorker>.Instance,
      publishStrategy: strategy,
      instanceProvider: new _FakeServiceInstanceProvider(),
      deadLetterStore: dlq,
      generationProvider: new _FakeGenerationProvider("test-gen"));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // Two rows, both at cap — bulk catch should promote each independently.
    await channel.WriteAsync(_work(attempts: 2), cts.Token);
    await channel.WriteAsync(_work(attempts: 2), cts.Token);

    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (dlq.Moves.Count < 2 && sw.Elapsed < TimeSpan.FromSeconds(5)) {
      await Task.Yield();
    }
    await Assert.That(dlq.Moves.Count).IsEqualTo(2)
      .Because("Bulk catch must promote EVERY row in the failing batch — a single bulk transport throw affects all in-flight rows, so each gets its own MoveAsync call.");
    await Assert.That(failure.All).IsEmpty()
      .Because("Bulk catch promotes-then-continues, skipping the per-row failure-channel enqueue for each promoted row.");
    foreach (var m in dlq.Moves) {
      await Assert.That(m.ErrorText).Contains("simulated bulk transport thrown")
        .Because("Each promoted row carries the full bulk exception text so all rows in the batch get the same fingerprint cluster (operators see one root cause, not N false-distinct entries).");
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task SingularPublish_AtCap_MoveAsyncThrows_FallsBackToFailureChannelAsync() {
    var strategy = new _FakeFailingStrategy();
    var throwingDlq = new _ThrowingDeadLetterStore();

    var channel = new _FakeWorkChannelWriter();
    var completion = new _NoOpCompletionChannel();
    var failure = new _FakeFailureChannel();
    var renewal = new _NoOpLeaseRenewalChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new OutboxPublishWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      channel, completion, failure, renewal, gate,
      Options.Create(new OutboxPublishWorkerOptions { Enabled = true, MaxOutboxAttempts = 2 }),
      NullLogger<OutboxPublishWorker>.Instance,
      publishStrategy: strategy,
      instanceProvider: new _FakeServiceInstanceProvider(),
      deadLetterStore: throwingDlq,
      generationProvider: new _FakeGenerationProvider("test-gen"));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await channel.WriteAsync(_work(attempts: 2), cts.Token);

    await throwingDlq.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (failure.All.IsEmpty && sw.Elapsed < TimeSpan.FromSeconds(2)) {
      await Task.Yield();
    }

    await Assert.That(failure.All.Count).IsEqualTo(1)
      .Because("When MoveAsync throws (transient DB issue), the worker MUST fall through to the failure channel so process_outbox_failures bumps attempts and the next claim retries the move via OutboxDrainWorker's pre-publish gate. Without the fallback, the row would be silently lost from the worker's perspective.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // --- shared no-op channels for the catch/throw tests above ---
  private sealed class _NoOpCompletionChannel : IOutboxCompletionChannel {
    public ValueTask EnqueueAsync(Guid id, CancellationToken ct = default) => ValueTask.CompletedTask;
  }

  private sealed class _NoOpLeaseRenewalChannel : ILeaseRenewalChannel {
    public ValueTask EnqueueAsync(WorkCategory category, Guid id, CancellationToken ct = default) => ValueTask.CompletedTask;
  }
}
