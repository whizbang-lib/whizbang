using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Resilience;
using Whizbang.Core.Tags;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

#pragma warning disable CS0067 // Event is never used (test doubles)

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The startup barrier, from the other side: what each gated worker must NOT have done when the
/// host stops while migrations are still running.
/// </summary>
/// <remarks>
/// <para>Every worker here parks on <see cref="ISchemaReadyGate"/> before doing anything, because
/// the work behind the gate touches tables the migration creates. A host that shuts down during
/// that window cancels the stopping token while the worker is still parked. Two failures follow
/// from getting the catch wrong, and neither announces itself: letting the cancellation escape
/// turns an ordinary deploy into a faulted hosted service, and dropping the early <c>return</c>
/// lets the worker fall through into DDL-dependent work against a schema nobody confirmed
/// exists.</para>
/// <para>These tests differ from the shutdown tests already in this directory in one respect that
/// turned out to matter: the gate itself publishes a signal when the worker reaches it, so the
/// test waits for the worker to actually be parked rather than assuming <c>StartAsync</c> left it
/// there. Each case then asserts on the work that must NOT have happened — a subscription, a
/// scope, a publish — rather than only that the task settled.</para>
/// </remarks>
public class SchemaGateShutdownCoverageTests {

  // ── shared doubles ───────────────────────────────────────────────────────

  /// <summary>
  /// A gate that never opens and reports when a waiter arrives. <see cref="Entered"/> is the
  /// deterministic "the worker is parked at the barrier" signal these tests wait on; the
  /// infinite delay then observes the stopping token exactly as the real gate's
  /// <c>Task.WaitAsync</c> does.
  /// </summary>
  private sealed class BlockingGate : ISchemaReadyGate {
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;
    public bool IsReady => false;
    public void MarkReady() { }

    public async Task WaitForReadyAsync(CancellationToken cancellationToken) {
      _entered.TrySetResult();
      await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>Counts scope creation so a test can assert no scoped database work was started.</summary>
  private sealed class CountingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory {
    private int _created;
    public int Created => Volatile.Read(ref _created);
    public IServiceScope CreateScope() {
      Interlocked.Increment(ref _created);
      return inner.CreateScope();
    }
  }

  /// <summary>
  /// Forwards resolution but counts requests for <see cref="IServiceScopeFactory"/> — the exact
  /// call <c>IServiceProvider.CreateScope()</c> makes, so this counts scope openings for a worker
  /// that holds a provider rather than a factory.
  /// </summary>
  private sealed class ScopeWatchingProvider(IServiceProvider inner) : IServiceProvider {
    private int _scopeFactoryRequests;
    public int ScopeFactoryRequests => Volatile.Read(ref _scopeFactoryRequests);
    public object? GetService(Type serviceType) {
      if (serviceType == typeof(IServiceScopeFactory)) {
        Interlocked.Increment(ref _scopeFactoryRequests);
      }
      return inner.GetService(serviceType);
    }
  }

  private static readonly TimeSpan _wait = TimeSpan.FromSeconds(20);

  // ── OrphanInboxJanitor ───────────────────────────────────────────────────

  /// <summary>
  /// The janitor's whole job behind the gate is a DELETE. Falling through on a canceled wait would
  /// issue it against a schema that may not have <c>wh_inbox</c> yet — the precise reason the sweep
  /// was moved out of <c>StartAsync</c> and behind the barrier in the first place.
  /// </summary>
  [Test]
  public async Task OrphanInboxJanitor_StoppedAtTheGate_OpensNoScopeAndReturnsCleanlyAsync() {
    var gate = new BlockingGate();
    using var inner = new ServiceCollection().BuildServiceProvider();
    var watched = new ScopeWatchingProvider(inner);
    var snapshot = new HandledReceptorTypeSnapshot([typeof(SchemaGateShutdownCoverageTests)]);
    var janitor = new OrphanInboxJanitor(watched, snapshot, schemaReadyGate: gate);

    await janitor.StartAsync(CancellationToken.None);
    await gate.Entered.WaitAsync(_wait);

    await janitor.StopAsync(CancellationToken.None).WaitAsync(_wait);
    await janitor.ExecuteTask!.WaitAsync(_wait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(watched.ScopeFactoryRequests).IsEqualTo(0)
      .Because("the purge must not open a scope after the gate wait was canceled — that scope is "
             + "where the DELETE against a possibly-unmigrated inbox would be issued");
    await Assert.That(janitor.ExecuteTask!.IsCompleted).IsTrue()
      .Because("a canceled gate wait must settle the hosted service, not hang shutdown");
    await Assert.That(janitor.ExecuteTask!.IsFaulted).IsFalse()
      .Because("stopping during migrations is an ordinary deploy, not a crash to report");
  }

  // ── CoalesceShipWorker ───────────────────────────────────────────────────

  /// <summary>
  /// The shipper's first act behind the gate is startup recovery, which opens a scope to reach the
  /// coordinator. Canceled at the barrier it must open none: the release pass writes to the same
  /// tables the migration is still creating.
  /// </summary>
  [Test]
  public async Task CoalesceShipWorker_StoppedAtTheGate_RunsNoStartupRecoveryAsync() {
    var gate = new BlockingGate();
    using var inner = new ServiceCollection().BuildServiceProvider();
    var scopeFactory = new CountingScopeFactory(inner.GetRequiredService<IServiceScopeFactory>());

    var tagOptions = new TagOptions();
    tagOptions.Coalesce("record-digest", c => c.SlideSeconds = 15);
    var resolver = new CoalesceGroupResolver(tagOptions, TimeProvider.System, () => []);

    var worker = new CoalesceShipWorker(
      scopeFactory,
      gate,
      new ServiceInstanceProvider(),
      coalesceResolver: resolver,
      logger: NullLogger<CoalesceShipWorker>.Instance);

    await worker.StartAsync(CancellationToken.None);
    await gate.Entered.WaitAsync(_wait);

    await worker.StopAsync(CancellationToken.None).WaitAsync(_wait);
    await worker.ExecuteTask!.WaitAsync(_wait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(scopeFactory.Created).IsEqualTo(0)
      .Because("startup recovery must stay behind the barrier — a release pass issued before "
             + "migrations finish writes to tables that may not exist");
    await Assert.That(worker.ExecuteTask!.IsCompleted).IsTrue()
      .Because("the shipper must stop promptly when the gate wait is canceled");
    await Assert.That(worker.ExecuteTask!.IsFaulted).IsFalse()
      .Because("a shutdown-before-ready must not read as a crash");
  }

  /// <summary>
  /// The gate wait is only reached when a coalesce binding is enabled — with none, the worker
  /// parks earlier and never touches the gate at all. Pinning that keeps the test above honest:
  /// its zero-scope assertion would be satisfied by the parked path too.
  /// </summary>
  [Test]
  public async Task CoalesceShipWorker_NoEnabledBindings_NeverReachesTheGateAsync() {
    var gate = new BlockingGate();
    using var inner = new ServiceCollection().BuildServiceProvider();
    var scopeFactory = new CountingScopeFactory(inner.GetRequiredService<IServiceScopeFactory>());
    // The parked-no-bindings log is ExecuteAsync's first statement on this path, so it is the
    // signal that the body actually ran. Without it, StartAsync + StopAsync would assert on a
    // worker whose body the thread pool never started — the gate would be untouched for the
    // wrong reason.
    var logger = new FirstLogSignal<CoalesceShipWorker>();

    var worker = new CoalesceShipWorker(
      scopeFactory,
      gate,
      new ServiceInstanceProvider(),
      coalesceResolver: null,
      logger: logger);

    await worker.StartAsync(CancellationToken.None);
    await logger.Logged.WaitAsync(_wait);

    await Assert.That(gate.Entered.IsCompleted).IsFalse()
      .Because("with the feature unused the worker parks before the barrier, so the gate-cancel "
             + "test above is exercising the enabled path and not this one");

    await worker.StopAsync(CancellationToken.None).WaitAsync(_wait);
    await worker.ExecuteTask!.WaitAsync(_wait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await Assert.That(worker.ExecuteTask!.IsFaulted).IsFalse()
      .Because("the parked worker must unpark and return cleanly on stop");
  }

  /// <summary>Completes on the first log call — a deterministic "ExecuteAsync's body ran" signal.</summary>
  private sealed class FirstLogSignal<T> : ILogger<T> {
    private readonly TaskCompletionSource _logged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Logged => _logged.Task;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) => _logged.TrySetResult();
  }

  // ── OutboxPublishWorker ──────────────────────────────────────────────────

  /// <summary>
  /// Publishing behind a gate that never opened would read the outbox table the migration creates.
  /// The catch has to end the run before the loop starts.
  /// </summary>
  [Test]
  public async Task OutboxPublishWorker_StoppedAtTheGate_NeverEntersThePublishLoopAsync() {
    var gate = new BlockingGate();
    using var sp = new ServiceCollection().BuildServiceProvider();
    var channel = new RecordingWorkChannelWriter();
    var strategy = new RecordingPublishStrategy();

    var worker = new OutboxPublishWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      channel,
      new NoOpOutboxCompletionChannel(),
      new NoOpFailureChannel(),
      new NoOpLeaseRenewalChannel(),
      gate,
      Options.Create(new OutboxPublishWorkerOptions { Enabled = true }),
      NullLogger<OutboxPublishWorker>.Instance,
      instanceProvider: new ServiceInstanceProvider(),
      publishStrategy: strategy);

    await worker.StartAsync(CancellationToken.None);
    await gate.Entered.WaitAsync(_wait);

    await worker.StopAsync(CancellationToken.None).WaitAsync(_wait);
    await worker.ExecuteTask!.WaitAsync(_wait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(channel.ReaderReads).IsEqualTo(0)
      .Because("the publish loop's first act is to read the work channel; a run that reached it "
             + "was already past the barrier it was supposed to stop at");
    await Assert.That(strategy.PublishCalls).IsEqualTo(0)
      .Because("nothing may be published before the schema is confirmed");
    await Assert.That(worker.ExecuteTask!.IsCompleted).IsTrue()
      .Because("the worker must settle rather than hang StopAsync");
    await Assert.That(worker.ExecuteTask!.IsFaulted).IsFalse()
      .Because("the cancellation belongs to shutdown, not to a fault");
  }

  // ── TransportConsumerWorker ──────────────────────────────────────────────

  /// <summary>
  /// This is the barrier with the sharpest consequence: subscribing tells the broker to start
  /// delivering, and every delivery lands in the inbox tables the migration is still creating. A
  /// worker that fell through here would take real traffic it cannot store.
  /// </summary>
  [Test]
  public async Task TransportConsumerWorker_StoppedAtTheGate_SubscribesToNothingAsync() {
    var gate = new BlockingGate();
    var transport = new CountingTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("topic-under-test"));

    using var sp = new ServiceCollection().BuildServiceProvider();

    var worker = new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new ServiceInstanceProvider(),
      schemaReadyGate: gate);

    await worker.StartAsync(CancellationToken.None);
    await gate.Entered.WaitAsync(_wait);

    await worker.StopAsync(CancellationToken.None).WaitAsync(_wait);
    await worker.ExecuteTask!.WaitAsync(_wait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(transport.SubscribeCalls).IsEqualTo(0)
      .Because("a subscription created before migrations finish invites the broker to deliver "
             + "messages into inbox tables that do not exist yet");
    await Assert.That(worker.ExecuteTask!.IsCompleted).IsTrue()
      .Because("the consumer must settle on a canceled gate wait");
    await Assert.That(worker.ExecuteTask!.IsFaulted).IsFalse()
      .Because("shutdown before the schema is ready is not a consumer fault");
  }

  // ── PerspectiveMigrationWorker ───────────────────────────────────────────

  /// <summary>
  /// The migration rebuilder's two callbacks are wired by the hosting infrastructure, not by the
  /// registration. A host that registers the worker without wiring them must get an inert worker:
  /// invoking a null callback would fault ExecuteAsync during startup, and the resulting
  /// NullReferenceException names neither the worker nor the wiring that was missed.
  /// </summary>
  [Test]
  public async Task PerspectiveMigrationWorker_CallbacksNotWired_NeverEvenReachesTheGateAsync() {
    var gate = new BlockingGate();
    var rebuilder = new CountingRebuilder();
    var worker = new PerspectiveMigrationWorker(
      rebuilder: rebuilder,
      logger: NullLogger<PerspectiveMigrationWorker>.Instance,
      schemaReadyGate: gate);

    await worker.StartAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(_wait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(gate.Entered.IsCompleted).IsFalse()
      .Because("the unwired check has to come FIRST — a worker that waits on the schema gate "
             + "before noticing it has no callbacks holds a startup slot for a run it can never do");
    await Assert.That(rebuilder.RebuildCalls).IsEqualTo(0)
      .Because("nothing may be rebuilt when the host never said what to rebuild");
    await Assert.That(worker.ExecuteTask!.IsFaulted).IsFalse()
      .Because("an unwired worker must be inert, not a startup crash");

    await worker.StopAsync(CancellationToken.None).WaitAsync(_wait);
  }

  /// <summary>
  /// With the callbacks wired, the worker parks on the gate. Stopped there it must abandon the run
  /// rather than query <c>wh_schema_migrations</c> — a table the migration it is waiting on may be
  /// in the middle of creating.
  /// </summary>
  [Test]
  public async Task PerspectiveMigrationWorker_StoppedAtTheGate_NeverQueriesPendingRebuildsAsync() {
    var gate = new BlockingGate();
    var rebuilder = new CountingRebuilder();
    var pendingQueries = 0;

    var worker = new PerspectiveMigrationWorker(
      rebuilder: rebuilder,
      logger: NullLogger<PerspectiveMigrationWorker>.Instance,
      schemaReadyGate: gate) {
      GetPendingRebuilds = _ => {
        Interlocked.Increment(ref pendingQueries);
        return Task.FromResult<IReadOnlyList<PendingMigrationRebuild>>([
          new PendingMigrationRebuild("ShouldNotRun", "perspective:ShouldNotRun")
        ]);
      },
      UpdateMigrationStatus = (_, _, _, _) => Task.CompletedTask
    };

    await worker.StartAsync(CancellationToken.None);
    await gate.Entered.WaitAsync(_wait);

    await worker.StopAsync(CancellationToken.None).WaitAsync(_wait);
    await worker.ExecuteTask!.WaitAsync(_wait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(Volatile.Read(ref pendingQueries)).IsEqualTo(0)
      .Because("the pending-rebuild query reads a migrations table the barrier exists to protect; "
             + "a canceled gate wait must end the run before it");
    await Assert.That(rebuilder.RebuildCalls).IsEqualTo(0)
      .Because("and certainly nothing may be rebuilt");
    await Assert.That(worker.ExecuteTask!.IsFaulted).IsFalse()
      .Because("stopping mid-migration is a normal deploy, not a fault");
  }

  private sealed class CountingRebuilder : IPerspectiveRebuilder {
    private int _rebuildCalls;
    public int RebuildCalls => Volatile.Read(ref _rebuildCalls);

    private static RebuildResult _ok(string name) =>
      new(name, StreamsProcessed: 0, EventsReplayed: 0, Duration: TimeSpan.Zero, Success: true, Error: null);

    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct = default) {
      Interlocked.Increment(ref _rebuildCalls);
      return Task.FromResult(_ok(perspectiveName));
    }

    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default) {
      Interlocked.Increment(ref _rebuildCalls);
      return Task.FromResult(_ok(perspectiveName));
    }

    public Task<RebuildResult> RebuildStreamsAsync(
        string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default) {
      Interlocked.Increment(ref _rebuildCalls);
      return Task.FromResult(_ok(perspectiveName));
    }

    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<RebuildStatus?>(null);
  }

  // ── doubles used above ───────────────────────────────────────────────────

  private sealed class CountingTransport : ITransport {
    private int _subscribeCalls;
    public int SubscribeCalls => Volatile.Read(ref _subscribeCalls);
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null,
        ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
        Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
        TransportDestination destination,
        CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _subscribeCalls);
      return Task.FromResult<ISubscription>(new NoOpSubscription());
    }

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _subscribeCalls);
      return Task.FromResult<ISubscription>(new NoOpSubscription());
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  private sealed class NoOpSubscription : ISubscription {
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
    public bool IsActive => true;
    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Dispose() { }
  }

  private sealed class RecordingWorkChannelWriter : IWorkChannelWriter {
    private readonly System.Threading.Channels.Channel<OutboxWork> _channel =
      System.Threading.Channels.Channel.CreateUnbounded<OutboxWork>();
    private int _readerReads;

    /// <summary>How many times the publish loop asked for the channel reader.</summary>
    public int ReaderReads => Volatile.Read(ref _readerReads);

    public System.Threading.Channels.ChannelReader<OutboxWork> Reader {
      get {
        Interlocked.Increment(ref _readerReads);
        return _channel.Reader;
      }
    }

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

  private sealed class NoOpOutboxCompletionChannel : IOutboxCompletionChannel {
    public ValueTask EnqueueAsync(Guid outboxMessageId, CancellationToken cancellationToken = default)
      => ValueTask.CompletedTask;
  }

  private sealed class NoOpFailureChannel : IFailureChannel {
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default)
      => ValueTask.CompletedTask;
  }

  private sealed class NoOpLeaseRenewalChannel : ILeaseRenewalChannel {
    public ValueTask EnqueueAsync(WorkCategory category, Guid id, CancellationToken cancellationToken = default)
      => ValueTask.CompletedTask;
  }

  private sealed class RecordingPublishStrategy : IMessagePublishStrategy {
    private int _publishCalls;
    public int PublishCalls => Volatile.Read(ref _publishCalls);
    public bool SupportsBulkPublish => false;

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _publishCalls);
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null,
        Reason = MessageFailureReason.Unknown
      });
    }
  }
}
