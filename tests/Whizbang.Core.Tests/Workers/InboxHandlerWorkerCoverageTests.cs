using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage for two branches the sibling test file never drives: the flush-time killswitch check
/// that can fire on a batch already in flight when the option flips mid-run, and the slow-phase
/// diagnostic that names WHICH of the three commit phases is stalling.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/InboxHandlerWorker.cs</code-under-test>
[Category("Workers")]
[Timeout(30_000)]
public sealed class InboxHandlerWorkerCoverageTests {
  private static InboxHandlerWorkerOptions _enabledOptions() => new() {
    Enabled = true,
    Flusher = new BatchFlusherOptions {
      MaxBatchSize = 10,
      CoalesceWindowMs = 5,
      ImmediateFlushThreshold = 1,
      ChannelCapacity = 100
    }
  };

  private static HandlerCommitRequest _request(Guid handlerId, Guid messageId) => new(
    HandlerId: handlerId,
    InstanceId: Guid.NewGuid(),
    ServiceName: "svc",
    HostName: "host",
    ProcessId: 1,
    PartitionCount: 2,
    InboxCompletion: new HandlerInboxCompletion(messageId, 0));

  [Test]
  public async Task FlushBatch_KillswitchFlippedWhileBatchInFlight_LogsAndDropsWithoutCommittingAsync(
      CancellationToken testToken) {
    // A commit already queued when the killswitch flips must not vanish silently: dropping it with
    // no log leaves the row stuck, re-claimed on every lease expiry, burning a retry attempt each
    // cycle until it dead-letters having never actually failed -- indistinguishable from a stuck
    // handler on a dashboard that shows nothing wrong.
    var opts = _enabledOptions();
    opts.Flusher.CoalesceWindowMs = 300; // real window the test uses to flip Enabled before the flush fires
    var coordinator = new _countingCoordinator();
    var log = new _disabledFlushCapturingLogger();
    var worker = new InboxHandlerWorker(
      new _stubScopeFactory(coordinator),
      new _noopFailureChannel(),
      SchemaReadyGate.AlreadyReady(),
      Options.Create(opts),
      log);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    await worker.StartAsync(cts.Token);
    await worker.EnqueueAsync(_request(Guid.NewGuid(), Guid.NewGuid()), cts.Token);
    opts.Enabled = false;

    await log.DisabledFlushLogged.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

    await Assert.That(coordinator.CallCount).IsEqualTo(0)
      .Because("a commit refused by the killswitch must never reach the coordinator -- it stays unprocessed rather than silently committing after being told not to");

    await worker.StopAsync(cts.Token);
  }

  [Test]
  public async Task FlushBatch_SchemaGateUnusuallySlow_LogsWhichPhaseIsStallingAsync(
      CancellationToken testToken) {
    // If this diagnostic regresses, a stalled flush phase becomes indistinguishable from any
    // other phase from the outside -- rows simply stop completing -- and an operator is back to
    // guessing which of three completely different causes (schema gate, pinned pool exhaustion, or
    // coordinator contention) is actually responsible, exactly the ambiguity this warning exists
    // to remove.
    var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var coordinator = new _countingCoordinator(committed);
    var log = new _slowPhaseCapturingLogger();
    var worker = new InboxHandlerWorker(
      new _stubScopeFactory(coordinator),
      new _noopFailureChannel(),
      new _slowSchemaReadyGate(TimeSpan.FromSeconds(5.5)),
      Options.Create(_enabledOptions()),
      log);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    await worker.StartAsync(cts.Token);
    await worker.EnqueueAsync(_request(Guid.NewGuid(), Guid.NewGuid()), cts.Token);

    var loggedMessage = await log.SlowPhaseLogged.WaitAsync(TimeSpan.FromSeconds(20), cts.Token);
    await committed.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
    await worker.StopAsync(cts.Token);

    await Assert.That(loggedMessage).Contains("schema-ready-gate")
      .Because("the warning must name WHICH phase stalled, not just that a flush was slow -- naming the wrong (or no) phase sends an operator to check the wrong subsystem");
    await Assert.That(coordinator.CallCount).IsEqualTo(1)
      .Because("the slow phase must still complete, not abort the flush -- a stall warning is a diagnostic, not a timeout");
  }

  // ==========================================================================
  // Test doubles
  // ==========================================================================

  private sealed class _noopFailureChannel : IFailureChannel {
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default)
      => ValueTask.CompletedTask;
  }

  private sealed class _slowSchemaReadyGate(TimeSpan delay) : ISchemaReadyGate {
    public bool IsReady => true;
    public async Task WaitForReadyAsync(CancellationToken cancellationToken) => await Task.Delay(delay, cancellationToken);
    public void MarkReady() { }
  }

  // Extends the shared NoOpWorkCoordinator rather than reimplementing IWorkCoordinator: the
  // interface has many members this test does not care about, and re-declaring them here would
  // break every time one is added.
  private sealed class _countingCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    private readonly TaskCompletionSource? _committed;
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public _countingCoordinator() { }
    public _countingCoordinator(TaskCompletionSource committed) => _committed = committed;

    public Task<IReadOnlyList<HandlerBatchResult>> CommitHandlerBatchAsync(
        IReadOnlyList<HandlerCommitRequest> requests, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _callCount);
      _committed?.TrySetResult();
      return Task.FromResult<IReadOnlyList<HandlerBatchResult>>(
        [.. requests.Select(r => new HandlerBatchResult(r.HandlerId, Success: true, ErrorMessage: null))]);
    }
  }

  private sealed class _disabledFlushCapturingLogger : Microsoft.Extensions.Logging.ILogger<InboxHandlerWorker> {
    private readonly TaskCompletionSource _logged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task DisabledFlushLogged => _logged.Task;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      var message = formatter(state, exception);
      if (message.Contains("dropping", StringComparison.OrdinalIgnoreCase)) {
        _logged.TrySetResult();
      }
    }
  }

  private sealed class _slowPhaseCapturingLogger : Microsoft.Extensions.Logging.ILogger<InboxHandlerWorker> {
    private readonly TaskCompletionSource<string> _slowPhaseLogged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<string> SlowPhaseLogged => _slowPhaseLogged.Task;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      var message = formatter(state, exception);
      if (message.Contains("flush phase", StringComparison.Ordinal)) {
        _slowPhaseLogged.TrySetResult(message);
      }
    }
  }

  private sealed class _stubScopeFactory(IWorkCoordinator coordinator) : IServiceScopeFactory {
    private readonly IWorkCoordinator _coordinator = coordinator;

    public IServiceScope CreateScope() => new _stubScope(_coordinator);

    private sealed class _stubScope(IWorkCoordinator coordinator) : IServiceScope {
      public IServiceProvider ServiceProvider { get; } = new _stubProvider(coordinator);
      public void Dispose() { }
    }

    private sealed class _stubProvider(IWorkCoordinator coordinator) : IServiceProvider {
      private readonly IWorkCoordinator _coordinator = coordinator;

      public object? GetService(Type serviceType)
        => serviceType == typeof(IWorkCoordinator) ? _coordinator : null;
    }
  }
}
