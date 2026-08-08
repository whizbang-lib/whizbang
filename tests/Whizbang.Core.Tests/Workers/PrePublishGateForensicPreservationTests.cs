using System.Collections.Concurrent;
using System.Text.Json;
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

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Slice 1 of release/v0.648.0-alpha.1 (DLQ forensics follow-up) — locks the
/// invariant that <see cref="OutboxDrainWorker"/>'s pre-publish DLQ gate uses
/// the row's <c>wh_outbox.error</c> column (the real exception text from the
/// last <c>process_outbox_failures</c> cycle) as <c>errorText</c> when present,
/// falling back to the synthetic meta-message only when the column is NULL.
///
/// <para>A production forensic investigation found a gap: <c>wh_dead_letter_summary</c> showed
/// a handful of "clusters" but all the raw rows shared a single
/// <c>error_fingerprint</c> because every promotion
/// used the meta-message
/// <c>"OutboxDrainWorker dead-lettered: attempts=N > max=10"</c> as
/// <c>error_text</c>. Slice 2's fingerprint algorithm extracts
/// "OutboxDrainWorker" as the type token + zero stack frames → identical
/// hash regardless of underlying root cause. Operators could see "we have
/// failures" but not "which exception type is causing them". This slice
/// closes that — the row's existing <c>error</c> column already carries the
/// real exception (Slice 1 v0.645 wired
/// <c>process_outbox_failures</c> to populate it on every failure cycle);
/// the pre-publish gate just needs to use it.</para>
///
/// <para>Locked invariants:</para>
/// <list type="bullet">
/// <item><description>When <c>row.Error</c> is non-NULL and non-empty AND
/// the pre-publish gate fires, <c>IDeadLetterStore.MoveAsync</c> receives
/// <c>row.Error</c> as <c>errorText</c> — verbatim, no truncation, no
/// transformation.</description></item>
/// <item><description>When <c>row.Error</c> is NULL or empty, the gate
/// falls back to the meta-message (the legacy default) so the
/// gate-firing signal is preserved even when no prior failure was
/// recorded.</description></item>
/// </list>
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class PrePublishGateForensicPreservationTests {

  // --- fakes ---

  private sealed class _FakeOutboxDrainChannel : IOutboxDrainChannel {
    private readonly System.Threading.Channels.Channel<Guid> _channel = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
    public System.Threading.Channels.ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken ct = default) => _channel.Writer.WriteAsync(streamId, ct);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
    public void Complete() => _channel.Writer.Complete();
  }

  private sealed class _FakeOutboxCompletionChannel : IOutboxCompletionChannel {
    public ValueTask EnqueueAsync(Guid id, CancellationToken ct = default) => ValueTask.CompletedTask;
  }

  private sealed class _FakeFailureChannel : IFailureChannel {
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) => ValueTask.CompletedTask;
  }

  private sealed class _FakePublishStrategy : IMessagePublishStrategy {
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) =>
      throw new InvalidOperationException("test: row should DLQ-promote before publish; PublishAsync must never be called");
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

  private sealed class _FakeGenerationProvider : IGenerationProvider {
    public string GetGeneration() => "test-gen";
  }

  private sealed record _MoveCall(string SourceTable, Guid SourceId, MessageFailureReason FailureReason, string? ErrorText);

  private sealed class _CapturingDeadLetterStore : IDeadLetterStore {
    public ConcurrentBag<_MoveCall> Moves { get; } = [];
    public TaskCompletionSource<_MoveCall> FirstMove { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<Guid?> MoveAsync(
        Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText,
        Guid instanceId, string generation, CancellationToken ct = default) {
      var call = new _MoveCall(sourceTable, sourceId, failureReason, errorText);
      Moves.Add(call);
      FirstMove.TrySetResult(call);
      return Task.FromResult<Guid?>(deadLetterId);
    }
  }

  private sealed class _FakeWorkCoordinator : IWorkCoordinator {
    public Dictionary<Guid, List<OutboxBatchRow>> RowsByStream { get; } = [];
    public Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
        IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream = 100, CancellationToken cancellationToken = default) {
      var result = new List<OutboxBatchRow>();
      foreach (var sid in streamIds) {
        if (RowsByStream.TryGetValue(sid, out var rows)) {
          result.AddRange(rows.Take(maxPerStream));
          // Mimic the "row no longer fetchable" semantics so the drain loop doesn't refetch
          // the same row indefinitely — the production fetch_outbox_batch only returns rows
          // whose lease/processed_at allows it.
          rows.Clear();
        }
      }
      return Task.FromResult<IReadOnlyList<OutboxBatchRow>>(result);
    }
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string name, CancellationToken ct = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  // --- helpers ---

  private static readonly JsonSerializerOptions _jsonOpts = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  private static OutboxBatchRow _row(Guid messageId, Guid streamId, int attempts, string? rowError, string? messageType = null) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    var typeInfo = _jsonOpts.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
      ?? throw new InvalidOperationException("Test setup: no JsonTypeInfo for MessageEnvelope<JsonElement>");
    var envelopeJson = JsonSerializer.Serialize(envelope, typeInfo);
    return new OutboxBatchRow {
      MessageId = messageId,
      StreamId = streamId,
      Destination = "test-topic",
      MessageType = messageType ?? "TestMessage",
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName ?? "MessageEnvelope",
      EventData = envelopeJson,
      Metadata = "{}",
      Scope = null,
      Status = 1,
      Attempts = attempts,
      PartitionNumber = 0,
      IsEvent = false,
      Error = rowError,  // ← Slice 1's new field
    };
  }

  // --- tests ---

  /// <summary>
  /// Control-plane traffic is DROPPED at the outbox gate, never stored. The outbox is where the
  /// bulk of a divergence storm accumulates (one service held over a hundred thousand such rows),
  /// and a stored copy is not inert — the recovery worker re-emits it into the inbox on a later
  /// boot, so a burst becomes a backlog every restart replays. The next cycle emits a fresh one.
  /// </summary>
  [Test]
  public async Task PrePublishGate_ControlPlaneRow_IsDroppedNotStoredAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var coord = new _FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId, attempts: 11, rowError: "transport unavailable",
      messageType: TypeNameFormatter.Format(typeof(IntegrityDivergenceDetected)))];

    var drainChannel = new _FakeOutboxDrainChannel();
    var dlqStore = new _CapturingDeadLetterStore();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new _FakeServiceInstanceProvider(),
      drainChannel,
      new _FakeOutboxCompletionChannel(),
      new _FakeFailureChannel(),
      gate,
      Options.Create(new OutboxDrainWorkerOptions {
        Enabled = true,
        MaxPerStream = 100,
        MaxOutboxAttempts = 10,
      }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      new _FakePublishStrategy(),
      deadLetterStore: dlqStore,
      generationProvider: new _FakeGenerationProvider());

    // The drop path stores NOTHING, so there is no MoveAsync to await on — and IsIdle starts
    // true, so polling it would assert before the worker ever ran (a vacuous pass that survived
    // deleting the fix). Await the worker's own idle-transition hook instead: it fires only after
    // a batch has actually been processed.
    var batchProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingIdle += () => batchProcessed.TrySetResult();

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId, cts.Token);

    await batchProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(dlqStore.Moves).IsEmpty()
      .Because("storing control-plane traffic is what let a purged storm revive itself on the next " +
               "boot — the row is dropped and the audit re-issues a fresh one on its own cadence");
  }

  /// <summary>
  /// RED for Slice 1: drive the drain worker with a row whose Attempts exceeds
  /// MaxOutboxAttempts AND whose Error column carries a real exception stack.
  /// Asserts the pre-publish DLQ gate fires MoveAsync with the row's actual
  /// stack as errorText — NOT the synthetic meta-message
  /// "OutboxDrainWorker dead-lettered: attempts=N > max=M". Pre-fix, the gate
  /// always used the meta-message, which collapsed a production consumer's large
  /// volume of DLQ rows to a single fingerprint cluster regardless of root cause.
  /// </summary>
  [Test]
  public async Task PrePublishGate_RowHasRealError_PromotesUsingRowErrorNotMetaMessageAsync() {
    var realStack = """
      System.InvalidOperationException: Could not open connection to 'consumer_db'
         at Whizbang.Data.EFCore.Postgres.Functions.OutboxClaim.LeaseAsync(Guid instanceId)
         at Whizbang.Core.Workers.OutboxDrainWorker.PublishBulkAsync()
      """;

    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var coord = new _FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId, attempts: 11, rowError: realStack)];

    var drainChannel = new _FakeOutboxDrainChannel();
    var dlqStore = new _CapturingDeadLetterStore();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new _FakeServiceInstanceProvider(),
      drainChannel,
      new _FakeOutboxCompletionChannel(),
      new _FakeFailureChannel(),
      gate,
      Options.Create(new OutboxDrainWorkerOptions {
        Enabled = true,
        MaxPerStream = 100,
        MaxOutboxAttempts = 10,  // row.Attempts = 11 > 10 → gate fires
      }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      new _FakePublishStrategy(),
      deadLetterStore: dlqStore,
      generationProvider: new _FakeGenerationProvider());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId, cts.Token);

    var move = await dlqStore.FirstMove.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(move.SourceTable).IsEqualTo(DeadLetterSourceTable.OUTBOX)
      .Because("DLQ promotion path setup sanity check.");
    await Assert.That(move.ErrorText).IsEqualTo(realStack)
      .Because("Slice 1 invariant: pre-publish gate MUST use the row's actual error column verbatim — NOT the meta-message. The production DLQ rows all collapsed to one fingerprint because the meta-message was used; using the real stack restores forensic diversity.");
    await Assert.That(move.ErrorText).DoesNotContain("dead-lettered: attempts=")
      .Because("Explicit anti-assertion: the meta-message text must NOT appear in errorText when row.Error is present.");
  }

  /// <summary>
  /// GREEN backstop: when the row has no error captured (NULL), the meta-message
  /// is preserved as the fallback so the DLQ row still records WHY it was
  /// promoted (cap reached) even when no failure was logged against the row.
  /// </summary>
  [Test]
  public async Task PrePublishGate_RowErrorIsNull_FallsBackToMetaMessageAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    var coord = new _FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId, attempts: 11, rowError: null)];

    var drainChannel = new _FakeOutboxDrainChannel();
    var dlqStore = new _CapturingDeadLetterStore();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new _FakeServiceInstanceProvider(),
      drainChannel,
      new _FakeOutboxCompletionChannel(),
      new _FakeFailureChannel(),
      gate,
      Options.Create(new OutboxDrainWorkerOptions {
        Enabled = true,
        MaxPerStream = 100,
        MaxOutboxAttempts = 10,
      }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      new _FakePublishStrategy(),
      deadLetterStore: dlqStore,
      generationProvider: new _FakeGenerationProvider());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId, cts.Token);

    var move = await dlqStore.FirstMove.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(move.ErrorText).Contains("dead-lettered")
      .Because("With no row-side error, the meta-message remains the operator's only signal that the gate fired — must be preserved as a fallback.");
    await Assert.That(move.ErrorText).Contains("attempts=11")
      .Because("Meta-message includes the attempt count for operator visibility into how many retries the row burned.");
  }
}
