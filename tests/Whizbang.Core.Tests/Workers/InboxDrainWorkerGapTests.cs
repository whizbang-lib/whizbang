using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

/// <summary>
/// Gap-coverage tests for <see cref="InboxDrainWorker"/> targeting branches not exercised by
/// <c>InboxDrainWorkerTests</c> / <c>DrainWorkerIdleSignalTests</c>: constructor guards, the
/// Enabled=false killswitch path, schema-gate cancellation, batched-fetch failure + per-stream
/// error isolation, the message_id fallback drain key, seen-set dedup, inner-loop termination
/// branches (empty fetch, newRows==0), cancellation short-circuits, and the Debug perf log.
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class InboxDrainWorkerGapTests {

  // --- fakes ---

  /// <summary>
  /// Drain channel fake that records <see cref="IInboxDrainChannel.MarkDraining"/> /
  /// <see cref="IInboxDrainChannel.MarkDrained"/> calls (the default interface implementations
  /// are no-ops, so the release-marker invariant in the worker's finally blocks is only
  /// observable through an overriding fake).
  /// </summary>
  private sealed class RecordingDrainChannel : IInboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ConcurrentQueue<Guid> Draining { get; } = new();
    public ConcurrentQueue<Guid> Drained { get; } = new();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      _channel.Writer.WriteAsync(streamId, cancellationToken);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
    public void MarkDraining(Guid streamId) => Draining.Enqueue(streamId);
    public void MarkDrained(Guid streamId) => Drained.Enqueue(streamId);
  }

  /// <summary>
  /// Capturing inbox writer with a completion signal at <see cref="TargetCount"/> writes,
  /// a signal counter for <see cref="SignalNewInboxWorkAvailable"/>, and an optional
  /// per-write failure injector (<see cref="FailWith"/>) for the error-isolation branches.
  /// </summary>
  private sealed class TestInboxWriter : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    private int _signalCount;
    public ConcurrentQueue<InboxWork> Written { get; } = new();
    public int TargetCount { get; set; } = 1;
    public TaskCompletionSource<int> ReachedTarget { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Func<InboxWork, Exception?>? FailWith { get; set; }
    public int SignalCount => _signalCount;
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) {
      var injected = FailWith?.Invoke(work);
      if (injected is not null) {
        throw injected;
      }
      Written.Enqueue(work);
      if (Written.Count >= TargetCount) {
        ReachedTarget.TrySetResult(Written.Count);
      }
      return _channel.Writer.WriteAsync(work, ct);
    }
    public bool TryWrite(InboxWork work) {
      Written.Enqueue(work);
      if (Written.Count >= TargetCount) {
        ReachedTarget.TrySetResult(Written.Count);
      }
      return _channel.Writer.TryWrite(work);
    }
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() {
      Interlocked.Increment(ref _signalCount);
      OnNewInboxWorkAvailable?.Invoke();
    }
  }

  private sealed class FakeInstance : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
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

  /// <summary>
  /// Scriptable coordinator: rows per stream (optionally consumed on fetch to mimic the
  /// post-completion DELETE), per-call streamIds capture, an optional pre-return callback
  /// (used to cancel the worker mid-fetch), and an optional injected fetch exception.
  /// </summary>
  private sealed class ScriptedCoordinator : IWorkCoordinator {
    public Dictionary<Guid, List<InboxBatchRow>> RowsByStream { get; } = [];
    public bool ConsumeRows { get; set; } = true;
    public Exception? ThrowOnFetch { get; set; }
    public Action? OnFetch { get; set; }
    public ConcurrentQueue<Guid[]> FetchCalls { get; } = new();

    public Task<IReadOnlyList<InboxBatchRow>> FetchInboxBatchAsync(
      IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream = 100, CancellationToken cancellationToken = default) {
      FetchCalls.Enqueue(streamIds.ToArray());
      OnFetch?.Invoke();
      if (ThrowOnFetch is not null) {
        throw ThrowOnFetch;
      }
      var result = new List<InboxBatchRow>();
      foreach (var sid in streamIds) {
        if (RowsByStream.TryGetValue(sid, out var rows)) {
          var taken = rows.Take(maxPerStream).ToList();
          result.AddRange(taken);
          if (ConsumeRows) {
            rows.RemoveRange(0, taken.Count);
          }
        }
      }
      return Task.FromResult<IReadOnlyList<InboxBatchRow>>(result);
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

  /// <summary>
  /// Minimal recording logger. Unlike NullLogger, <see cref="IsEnabled"/> honours the
  /// configured minimum level so the Debug-guarded perf-log branch in
  /// <c>_logPerfIfInteresting</c> is reachable. Optionally signals a TCS when a specific
  /// EventId is logged (used to synchronize on the disabled-path log without polling).
  /// </summary>
  private sealed class RecordingLogger : ILogger<InboxDrainWorker> {
    private readonly LogLevel _minLevel;
    public RecordingLogger(LogLevel minLevel) {
      _minLevel = minLevel;
    }
    public int? SignalOnEventId { get; init; }
    public TaskCompletionSource<bool> EventSignaled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<(int EventId, LogLevel Level, string Message)> Entries { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (!IsEnabled(logLevel)) {
        return;
      }
      Entries.Enqueue((eventId.Id, logLevel, formatter(state, exception)));
      if (SignalOnEventId == eventId.Id) {
        EventSignaled.TrySetResult(true);
      }
    }
  }

  /// <summary>
  /// Schema gate that never becomes ready. Signals <see cref="Entered"/> once the worker is
  /// deterministically inside <see cref="WaitForReadyAsync"/>, so the test can cancel exactly
  /// while the worker is parked on the gate.
  /// </summary>
  private sealed class NeverReadyGate : ISchemaReadyGate {
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Entered => _entered.Task;
    public bool IsReady => false;
    public void MarkReady() { }
    public Task WaitForReadyAsync(CancellationToken cancellationToken) {
      _entered.TrySetResult();
      return _never.Task.WaitAsync(cancellationToken);
    }
  }

  // --- helpers ---

  private static readonly JsonSerializerOptions _jsonOpts =
    Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);

  private static InboxBatchRow _row(Guid messageId, Guid? streamId) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    var typeInfo = _jsonOpts.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
      ?? throw new InvalidOperationException("Test setup: no JsonTypeInfo for MessageEnvelope<JsonElement>");
    var envelopeJson = JsonSerializer.Serialize(envelope, typeInfo);
    return new InboxBatchRow {
      MessageId = messageId,
      StreamId = streamId,
      HandlerName = "TestHandler",
      MessageType = "TestMessage",
      EventData = envelopeJson,
      Metadata = "{}",
      Scope = null,
      Status = 1,
      Attempts = 0,
      PartitionNumber = 0,
      IsEvent = false,
    };
  }

  private static InboxBatchRow _malformedRow(Guid messageId, Guid streamId) => new() {
    MessageId = messageId,
    StreamId = streamId,
    HandlerName = "TestHandler",
    MessageType = "TestMessage",
    EventData = "{not valid json",   // <-- deliberate, _toInboxWork must throw
    Metadata = "{}",
    Scope = null,
    Status = 1,
    Attempts = 0,
    PartitionNumber = 0,
    IsEvent = false,
  };

  private static InboxDrainWorker _buildWorker(
      IWorkCoordinator coordinator,
      RecordingDrainChannel drainChannel,
      TestInboxWriter inboxWriter,
      InboxDrainWorkerOptions options,
      ILogger<InboxDrainWorker>? logger = null,
      ISchemaReadyGate? gate = null) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    var sp = services.BuildServiceProvider();

    if (gate is null) {
      var readyGate = new SchemaReadyGate();
      readyGate.MarkReady();
      gate = readyGate;
    }

    return new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeInstance(),
      drainChannel,
      inboxWriter,
      gate,
      Options.Create(options),
      _jsonOpts,
      logger ?? NullLogger<InboxDrainWorker>.Instance);
  }

  // --- tests ---

  /// <summary>
  /// Covers all constructor null-guard branches (InboxDrainWorker.cs lines 81-88), including
  /// the <c>options?.Value</c> null-Value variant of the options guard.
  /// </summary>
  [Test]
  public async Task Constructor_NullArguments_ThrowArgumentNullExceptionPerParameterAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var instance = new FakeInstance();
    var drain = new RecordingDrainChannel();
    var writer = new TestInboxWriter();
    var gate = new SchemaReadyGate();
    var options = Options.Create(new InboxDrainWorkerOptions());
    var logger = NullLogger<InboxDrainWorker>.Instance;

    var exScope = Assert.Throws<ArgumentNullException>(() =>
      _ = new InboxDrainWorker(null!, instance, drain, writer, gate, options, _jsonOpts, logger));
    await Assert.That(exScope.ParamName).IsEqualTo("scopeFactory");

    var exInstance = Assert.Throws<ArgumentNullException>(() =>
      _ = new InboxDrainWorker(scopeFactory, null!, drain, writer, gate, options, _jsonOpts, logger));
    await Assert.That(exInstance.ParamName).IsEqualTo("instanceProvider");

    var exDrain = Assert.Throws<ArgumentNullException>(() =>
      _ = new InboxDrainWorker(scopeFactory, instance, null!, writer, gate, options, _jsonOpts, logger));
    await Assert.That(exDrain.ParamName).IsEqualTo("drainChannel");

    var exWriter = Assert.Throws<ArgumentNullException>(() =>
      _ = new InboxDrainWorker(scopeFactory, instance, drain, null!, gate, options, _jsonOpts, logger));
    await Assert.That(exWriter.ParamName).IsEqualTo("inboxChannelWriter");

    var exGate = Assert.Throws<ArgumentNullException>(() =>
      _ = new InboxDrainWorker(scopeFactory, instance, drain, writer, null!, options, _jsonOpts, logger));
    await Assert.That(exGate.ParamName).IsEqualTo("schemaReadyGate");

    var exOptions = Assert.Throws<ArgumentNullException>(() =>
      _ = new InboxDrainWorker(scopeFactory, instance, drain, writer, gate, null!, _jsonOpts, logger));
    await Assert.That(exOptions.ParamName).IsEqualTo("options");

    var exOptionsValue = Assert.Throws<ArgumentNullException>(() =>
      _ = new InboxDrainWorker(scopeFactory, instance, drain, writer, gate,
        new OptionsWrapper<InboxDrainWorkerOptions>(null!), _jsonOpts, logger));
    await Assert.That(exOptionsValue.ParamName).IsEqualTo("options");

    var exJson = Assert.Throws<ArgumentNullException>(() =>
      _ = new InboxDrainWorker(scopeFactory, instance, drain, writer, gate, options, null!, logger));
    await Assert.That(exJson.ParamName).IsEqualTo("jsonOptions");

    var exLogger = Assert.Throws<ArgumentNullException>(() =>
      _ = new InboxDrainWorker(scopeFactory, instance, drain, writer, gate, options, _jsonOpts, null!));
    await Assert.That(exLogger.ParamName).IsEqualTo("logger");
  }

  /// <summary>
  /// Covers the Enabled=false killswitch path (lines 95-100): the worker logs "disabled"
  /// (EventId 3), parks on an infinite delay without ever touching the coordinator, and on
  /// shutdown swallows the cancellation and logs "stopped" (EventId 2). The logger's TCS
  /// signals deterministically once the disabled branch has been entered.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_Disabled_NeverFetches_LogsDisabledThenStoppedOnShutdownAsync() {
    var coord = new ScriptedCoordinator();
    var drain = new RecordingDrainChannel();
    var writer = new TestInboxWriter();
    var logger = new RecordingLogger(LogLevel.Information) { SignalOnEventId = 3 };

    var worker = _buildWorker(coord, drain, writer,
      new InboxDrainWorkerOptions { Enabled = false }, logger);

    await worker.StartAsync(CancellationToken.None);
    await logger.EventSignaled.Task.WaitAsync(_timeout);

    // Cancel the stopping token; the disabled path catches the OCE and logs "stopped".
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(logger.Entries.Count(e => e.EventId == 3)).IsEqualTo(1)
      .Because("the disabled branch logs LogDisabled exactly once");
    await Assert.That(logger.Entries.Count(e => e.EventId == 2)).IsEqualTo(1)
      .Because("the disabled branch still logs LogStopped after cancellation");
    await Assert.That(coord.FetchCalls.Count).IsEqualTo(0)
      .Because("a disabled drainer must never issue a fetch");
    await Assert.That(writer.Written.Count).IsEqualTo(0);
    await Assert.That(worker.IsIdle).IsTrue();
  }

  /// <summary>
  /// Covers the schema-gate cancellation branch (lines 102-106): cancellation while parked on
  /// <see cref="ISchemaReadyGate.WaitForReadyAsync"/> returns from ExecuteAsync WITHOUT
  /// entering the batch loop and WITHOUT reaching the trailing LogStopped (EventId 2) — that
  /// early return skips line 133 entirely.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_CanceledWhileWaitingForSchemaGate_ReturnsWithoutBatchingAsync() {
    var coord = new ScriptedCoordinator();
    var drain = new RecordingDrainChannel();
    var writer = new TestInboxWriter();
    var logger = new RecordingLogger(LogLevel.Information);
    var gate = new NeverReadyGate();

    var worker = _buildWorker(coord, drain, writer,
      new InboxDrainWorkerOptions { Enabled = true }, logger, gate);

    await worker.StartAsync(CancellationToken.None);
    await gate.Entered.WaitAsync(_timeout);

    await worker.StopAsync(CancellationToken.None);

    var executeTask = worker.ExecuteTask ?? Task.CompletedTask;
    await Assert.That(executeTask.IsCompleted).IsTrue()
      .Because("the gate-cancellation branch swallows the OCE and returns cleanly");
    await Assert.That(executeTask.IsFaulted).IsFalse()
      .Because("graceful shutdown must not fault (Canceled or RanToCompletion both clean)");
    await Assert.That(logger.Entries.Count(e => e.EventId == 1)).IsEqualTo(1)
      .Because("LogStarted fires before the gate wait");
    await Assert.That(logger.Entries.Count(e => e.EventId == 2)).IsEqualTo(0)
      .Because("the early return on gate cancellation skips LogStopped");
    await Assert.That(coord.FetchCalls.Count).IsEqualTo(0);
  }

  /// <summary>
  /// Covers the batched-fetch failure path: FetchInboxBatchAsync throws a non-OCE exception,
  /// the finally block still releases the draining marker for every stream, the idle
  /// transition fires, the failure is logged (EventId 6) — and the worker SURVIVES. An escaped
  /// exception would fault the BackgroundService and, under the host default
  /// (BackgroundServiceExceptionBehavior.StopHost), stop the whole host — observed live as a
  /// clean-exit restart loop when connection-pool exhaustion made the fetch throw.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_BatchFetchThrows_MarksStreamsDrained_FiresIdle_WorkerSurvivesAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var coord = new ScriptedCoordinator { ThrowOnFetch = new InvalidOperationException("fetch-boom") };
    var drain = new RecordingDrainChannel();
    var writer = new TestInboxWriter();
    var logger = new RecordingLogger(LogLevel.Information);

    var worker = _buildWorker(coord, drain, writer,
      new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }, logger);

    var startedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var idleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingStarted += () => startedTcs.TrySetResult(true);
    worker.OnWorkProcessingIdle += () => idleTcs.TrySetResult(true);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(streamId);

    await startedTcs.Task.WaitAsync(_timeout);
    await idleTcs.Task.WaitAsync(_timeout);

    // The stream was marked draining before the fetch, and the finally block must
    // release the marker even when the batched fetch throws.
    await Assert.That(drain.Draining.ToList()).Contains(streamId);
    await Assert.That(drain.Drained.ToList()).Contains(streamId);
    await Assert.That(writer.Written.Count).IsEqualTo(0);

    var executeTask = worker.ExecuteTask ?? Task.CompletedTask;
    await Assert.That(executeTask.IsFaulted).IsFalse()
      .Because("a transient fetch failure is logged and the loop continues — a faulted " +
               "BackgroundService stops the whole host under the StopHost default, turning " +
               "one DB blip into a service outage.");
    await Assert.That(logger.Entries.Count(e => e.EventId == 6)).IsEqualTo(1)
      .Because("the failure is loud (LogBatchDrainFailed), never silent.");

    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
  }

  /// <summary>
  /// Covers per-stream error isolation in the batch dispatch loop (lines 231-233): a write
  /// failure for one stream is logged via LogDrainError (EventId 4) and the remaining streams
  /// in the same batch still drain. Both streams are marked drained.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_WriteFailsForOneStream_LogsDrainError_OtherStreamStillDrainsAsync() {
    var streamA = (Guid)TrackedGuid.NewMedo();
    var streamB = (Guid)TrackedGuid.NewMedo();
    var msgB = (Guid)TrackedGuid.NewMedo();

    var coord = new ScriptedCoordinator();
    coord.RowsByStream[streamA] = [_row((Guid)TrackedGuid.NewMedo(), streamA)];
    coord.RowsByStream[streamB] = [_row(msgB, streamB)];

    var drain = new RecordingDrainChannel();
    var writer = new TestInboxWriter {
      FailWith = work => work.StreamId == streamA ? new InvalidOperationException("write-boom") : null,
    };
    var logger = new RecordingLogger(LogLevel.Information);

    var worker = _buildWorker(coord, drain, writer,
      new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }, logger);

    var idleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingIdle += () => idleTcs.TrySetResult(true);

    // Pre-queue both stream ids so the sliding-window batcher sees them in one batch.
    _ = drain.TryWrite(streamA);
    _ = drain.TryWrite(streamB);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await idleTcs.Task.WaitAsync(_timeout);

    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(writer.Written.Count).IsEqualTo(1)
      .Because("stream B's row must still be enqueued after stream A's write failed");
    await Assert.That(writer.Written.First().MessageId).IsEqualTo(msgB);
    await Assert.That(logger.Entries.Count(e => e.EventId == 4)).IsEqualTo(1)
      .Because("the per-stream catch logs LogDrainError exactly once for stream A");
    await Assert.That(drain.Drained.ToList()).Contains(streamA);
    await Assert.That(drain.Drained.ToList()).Contains(streamB);
  }

  /// <summary>
  /// Covers batch dedup (lines 119-120) and the empty-stream skip (lines 195-197): the same
  /// stream id signalled twice collapses into one fetch entry, and a stream with no fetched
  /// rows is skipped without writes yet still released via MarkDrained.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_DuplicateStreamIdsAndEmptyStream_DedupesFetch_SkipsEmptyStreamAsync() {
    var streamA = (Guid)TrackedGuid.NewMedo();   // no rows — the empty-skip branch
    var streamB = (Guid)TrackedGuid.NewMedo();
    var msgB = (Guid)TrackedGuid.NewMedo();

    var coord = new ScriptedCoordinator();
    coord.RowsByStream[streamB] = [_row(msgB, streamB)];

    var drain = new RecordingDrainChannel();
    var writer = new TestInboxWriter { TargetCount = 1 };

    var worker = _buildWorker(coord, drain, writer,
      new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 });

    var idleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingIdle += () => idleTcs.TrySetResult(true);

    _ = drain.TryWrite(streamA);
    _ = drain.TryWrite(streamA);   // duplicate signal — must be deduped within the batch
    _ = drain.TryWrite(streamB);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await idleTcs.Task.WaitAsync(_timeout);

    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(coord.FetchCalls.Count).IsEqualTo(1)
      .Because("one deduped multi-stream fetch drains the whole window");
    var fetched = coord.FetchCalls.First();
    await Assert.That(fetched.Length).IsEqualTo(2)
      .Because("streamA appears once despite two drain-channel signals");
    await Assert.That(fetched.ToHashSet()).Contains(streamA);
    await Assert.That(fetched.ToHashSet()).Contains(streamB);
    await Assert.That(writer.Written.Count).IsEqualTo(1)
      .Because("only streamB had a row; streamA hits the hasRows=false continue");
    await Assert.That(drain.Drained.ToList()).Contains(streamA);
    await Assert.That(drain.Drained.ToList()).Contains(streamB);
  }

  /// <summary>
  /// Covers the message_id fallback drain key (line 187, right side of
  /// <c>r.StreamId ?? r.MessageId</c>): an unscoped row with StreamId=null is grouped under
  /// its MessageId, which is the key the drain channel was fed, so it still dispatches.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_NullStreamIdRow_GroupsByMessageIdFallbackKeyAsync() {
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new ScriptedCoordinator();
    // The drain channel carries the message_id for unscoped rows — mirror that here.
    coord.RowsByStream[msgId] = [_row(msgId, null)];

    var drain = new RecordingDrainChannel();
    var writer = new TestInboxWriter { TargetCount = 1 };

    var worker = _buildWorker(coord, drain, writer,
      new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 });

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(msgId);

    await writer.ReachedTarget.Task.WaitAsync(_timeout);
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(writer.Written.Count).IsEqualTo(1)
      .Because("the null-stream row must be found under its message_id fallback key");
    var written = writer.Written.First();
    await Assert.That(written.MessageId).IsEqualTo(msgId);
    await Assert.That(written.StreamId).IsNull();
  }

  /// <summary>
  /// Covers the batch-path seen-set dedup (lines 205-207): a fetch returning two rows with the
  /// same MessageId enqueues only one InboxWork, then signals via the else-if branch
  /// (lines 226-228) because the stream did not fill its per-stream cap.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_DuplicateMessageIdsInFetch_SeenSetSkipsSecondOccurrenceAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new ScriptedCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId), _row(msgId, streamId)];

    var drain = new RecordingDrainChannel();
    var writer = new TestInboxWriter { TargetCount = 1 };

    var worker = _buildWorker(coord, drain, writer,
      new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 });

    var idleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingIdle += () => idleTcs.TrySetResult(true);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(streamId);
    await idleTcs.Task.WaitAsync(_timeout);

    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(writer.Written.Count).IsEqualTo(1)
      .Because("the second row with the same MessageId must be skipped by the seen-set");
    await Assert.That(writer.SignalCount).IsEqualTo(1)
      .Because("a below-cap batch with new work signals the dispatch worker once");
  }

  /// <summary>
  /// Covers the inner-loop newRows==0 termination branch (lines 302-308): with a
  /// non-consuming coordinator that returns the SAME cap-filling rows on every fetch, the
  /// cap-filled stream falls back to the inner loop (lines 224-225), whose fresh seen-set
  /// accepts the rows once, then the second inner fetch yields zero NEW rows — signal + return
  /// instead of looping forever.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_RepeatedRowsAtCap_InnerLoopStopsWhenNoNewRowsAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msg1 = (Guid)TrackedGuid.NewMedo();
    var msg2 = (Guid)TrackedGuid.NewMedo();

    var coord = new ScriptedCoordinator { ConsumeRows = false };
    coord.RowsByStream[streamId] = [_row(msg1, streamId), _row(msg2, streamId)];

    var drain = new RecordingDrainChannel();
    var writer = new TestInboxWriter { TargetCount = 4 };

    var worker = _buildWorker(coord, drain, writer,
      new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 2 });

    var idleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingIdle += () => idleTcs.TrySetResult(true);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(streamId);
    await idleTcs.Task.WaitAsync(_timeout);

    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Batch path writes 2, inner loop's independent seen-set writes the same 2 again
    // (dedupped downstream by wh_message_deduplication), then terminates on newRows==0.
    await Assert.That(writer.Written.Count).IsEqualTo(4)
      .Because("batch dispatch (2) + one inner-loop pass (2); the third fetch has no new rows");
    await Assert.That(coord.FetchCalls.Count).IsEqualTo(3)
      .Because("one batched fetch + two inner-loop fetches (second finds nothing new)");
    await Assert.That(writer.SignalCount).IsEqualTo(1)
      .Because("the newRows==0 exit signals exactly once when work was enqueued");
  }

  /// <summary>
  /// Covers the inner-loop empty-fetch exit with pending signal (lines 265-271): a cap-filled
  /// stream drains through the inner loop until a fetch returns zero rows; because rows were
  /// enqueued, SignalNewInboxWorkAvailable fires on that exit path.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_CapFilledStream_InnerLoopEmptyFetchSignalsAndReturnsAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgs = Enumerable.Range(0, 4).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();

    var coord = new ScriptedCoordinator();   // consuming — fetched rows disappear
    coord.RowsByStream[streamId] = [.. msgs.Select(m => _row(m, streamId))];

    var drain = new RecordingDrainChannel();
    var writer = new TestInboxWriter { TargetCount = 4 };

    var worker = _buildWorker(coord, drain, writer,
      new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 2 });

    var idleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingIdle += () => idleTcs.TrySetResult(true);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(streamId);
    await idleTcs.Task.WaitAsync(_timeout);

    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(writer.Written.Count).IsEqualTo(4)
      .Because("2 rows from the batched fetch + 2 from the inner cap-fill fallback");
    await Assert.That(coord.FetchCalls.Count).IsEqualTo(3)
      .Because("batch fetch (2 rows) + inner fetch (2 rows, == cap so loop) + inner fetch (0 rows)");
    await Assert.That(writer.SignalCount).IsEqualTo(1)
      .Because("the empty-fetch exit signals because the inner loop had enqueued new work");
  }

  /// <summary>
  /// Covers the Debug-gated perf log (lines 324-337): with a Debug-enabled logger and an
  /// inner-loop drain that enqueues >= 5 rows, the "PERF InboxDrain" LogDebug line fires.
  /// NullLogger-based tests only ever cover the IsEnabled=false early return.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_DebugLoggerAndLargeInnerDrain_EmitsPerfLogLineAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgs = Enumerable.Range(0, 9).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();

    var coord = new ScriptedCoordinator();
    coord.RowsByStream[streamId] = [.. msgs.Select(m => _row(m, streamId))];

    var drain = new RecordingDrainChannel();
    var writer = new TestInboxWriter { TargetCount = 9 };
    var logger = new RecordingLogger(LogLevel.Debug);

    var worker = _buildWorker(coord, drain, writer,
      new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 3 }, logger);

    var idleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingIdle += () => idleTcs.TrySetResult(true);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(streamId);
    await idleTcs.Task.WaitAsync(_timeout);

    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(writer.Written.Count).IsEqualTo(9);
    // Inner loop enqueued 6 rows (batch took the first 3) — >= 5 triggers the PERF line.
    var perfLines = logger.Entries.Count(e => e.Level == LogLevel.Debug && e.Message.Contains("PERF InboxDrain"));
    await Assert.That(perfLines).IsEqualTo(1)
      .Because("the inner drain enqueued 6 rows (>= 5), so exactly one PERF line is emitted");
  }

  /// <summary>
  /// Covers the inner-loop deserialize-failure continue (lines 288-291): a malformed row
  /// encountered during the cap-fill fallback is logged (EventId 5) and skipped while the
  /// remaining rows keep flowing.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_MalformedRowInInnerLoop_LogsAndSkips_ContinuesDrainAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var good1 = (Guid)TrackedGuid.NewMedo();
    var good2 = (Guid)TrackedGuid.NewMedo();
    var bad = (Guid)TrackedGuid.NewMedo();
    var good3 = (Guid)TrackedGuid.NewMedo();

    var coord = new ScriptedCoordinator();
    coord.RowsByStream[streamId] = [
      _row(good1, streamId),
      _row(good2, streamId),
      _malformedRow(bad, streamId),
      _row(good3, streamId),
    ];

    var drain = new RecordingDrainChannel();
    var writer = new TestInboxWriter { TargetCount = 3 };
    var logger = new RecordingLogger(LogLevel.Information);

    var worker = _buildWorker(coord, drain, writer,
      new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 2 }, logger);

    var idleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingIdle += () => idleTcs.TrySetResult(true);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(streamId);
    await idleTcs.Task.WaitAsync(_timeout);

    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(writer.Written.Count).IsEqualTo(3)
      .Because("the malformed inner-loop row is skipped; good1/good2 (batch) + good3 (inner) flow through");
    var writtenIds = writer.Written.Select(w => w.MessageId).ToHashSet();
    await Assert.That(writtenIds).Contains(good1);
    await Assert.That(writtenIds).Contains(good2);
    await Assert.That(writtenIds).Contains(good3);
    await Assert.That(logger.Entries.Count(e => e.EventId == 5)).IsEqualTo(1)
      .Because("LogDeserializeFailed fires once for the malformed row");
  }

  /// <summary>
  /// Covers the cancellation short-circuit in the batch dispatch loop (lines 191-193): the
  /// stopping token is canceled DURING the fetch, so even though rows came back, the per-sid
  /// loop breaks before any write. The stream is still marked drained and the worker exits its
  /// loop via the outer OCE catch, logging LogStopped (EventId 2).
  /// </summary>
  [Test]
  public async Task ExecuteAsync_CanceledDuringBatchFetch_BreaksBeforeDispatch_StopsCleanlyAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    using var cts = new CancellationTokenSource();

    var coord = new ScriptedCoordinator { OnFetch = () => cts.Cancel() };
    coord.RowsByStream[streamId] = [_row((Guid)TrackedGuid.NewMedo(), streamId)];

    var drain = new RecordingDrainChannel();
    var writer = new TestInboxWriter();
    var logger = new RecordingLogger(LogLevel.Information);

    var worker = _buildWorker(coord, drain, writer,
      new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }, logger);

    var idleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingIdle += () => idleTcs.TrySetResult(true);

    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(streamId);
    await idleTcs.Task.WaitAsync(_timeout);

    var executeTask = worker.ExecuteTask ?? Task.CompletedTask;
    await executeTask.WaitAsync(_timeout);
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(writer.Written.Count).IsEqualTo(0)
      .Because("cancellation observed after the fetch must break before any dispatch");
    // The finally block releases the draining marker on the cancellation path too.
    await Assert.That(drain.Drained.ToList()).Contains(streamId);
    await Assert.That(executeTask.IsCompleted).IsTrue()
      .Because("the batcher's OCE is swallowed by the outer catch");
    await Assert.That(executeTask.IsFaulted).IsFalse()
      .Because("graceful shutdown must not fault (Canceled or RanToCompletion both clean)");
    await Assert.That(logger.Entries.Count(e => e.EventId == 2)).IsEqualTo(1)
      .Because("shutdown via cancellation reaches the trailing LogStopped");
  }

  /// <summary>
  /// Covers the OperationCanceledException rethrow filter in the batch dispatch loop
  /// (lines 229-230): a write that throws OCE while the stopping token IS canceled must be
  /// rethrown (not swallowed as a per-stream error), unwinding through the marker-release
  /// finally into the outer OCE catch — clean stop, LogStopped, no LogDrainError.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_WriterThrowsOceWhileCanceled_RethrowsAndStopsCleanlyAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    using var cts = new CancellationTokenSource();

    var coord = new ScriptedCoordinator();
    coord.RowsByStream[streamId] = [_row((Guid)TrackedGuid.NewMedo(), streamId)];

    var drain = new RecordingDrainChannel();
    var writer = new TestInboxWriter {
      FailWith = _ => {
        cts.Cancel();
        return new OperationCanceledException(cts.Token);
      },
    };
    var logger = new RecordingLogger(LogLevel.Information);

    var worker = _buildWorker(coord, drain, writer,
      new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }, logger);

    var idleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingIdle += () => idleTcs.TrySetResult(true);

    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(streamId);
    await idleTcs.Task.WaitAsync(_timeout);

    var executeTask = worker.ExecuteTask ?? Task.CompletedTask;
    await executeTask.WaitAsync(_timeout);
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(writer.Written.Count).IsEqualTo(0)
      .Because("the injected OCE prevented the write from being recorded");
    await Assert.That(logger.Entries.Count(e => e.EventId == 4)).IsEqualTo(0)
      .Because("a cancellation-driven OCE must NOT be logged as a per-stream drain error");
    // The marker-release finally runs while the OCE unwinds.
    await Assert.That(drain.Drained.ToList()).Contains(streamId);
    await Assert.That(executeTask.IsCompleted).IsTrue()
      .Because("the rethrown OCE is absorbed by ExecuteAsync's outer catch — a clean shutdown");
    await Assert.That(executeTask.IsFaulted).IsFalse()
      .Because("graceful shutdown must not fault (Canceled or RanToCompletion both clean)");
    await Assert.That(logger.Entries.Count(e => e.EventId == 2)).IsEqualTo(1);
  }

  /// <summary>
  /// Locks the remaining <see cref="InboxDrainWorkerOptions"/> defaults (MaxPerStream and the
  /// Batcher policy object) — the sibling test file only locks Enabled=true.
  /// </summary>
  [Test]
  public async Task InboxDrainWorkerOptions_Defaults_MaxPerStreamAndBatcherLockedAsync() {
    var defaults = new InboxDrainWorkerOptions();
    await Assert.That(defaults.MaxPerStream).IsEqualTo(100);
    await Assert.That(defaults.Batcher).IsNotNull();
    await Assert.That(defaults.Batcher.MaxSize).IsEqualTo(100);
    await Assert.That(defaults.Batcher.SlidingWindow).IsEqualTo(TimeSpan.FromMilliseconds(50));
    await Assert.That(defaults.Batcher.MaxWait).IsEqualTo(TimeSpan.FromSeconds(1));
  }
}
