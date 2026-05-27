using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Slice 6 of plans/pump-then-process.md (Half A) — transport-agnostic invariant locks.
/// The cross-transport-with-real-emulator verification (RabbitMQ container + ASB emulator)
/// needs Docker and lives in separate transport-specific test projects. The invariants
/// below — single bulk insert per batch, drop-semantics filtering before insert — are
/// transport-agnostic (TransportConsumerWorker is ITransport-driven) and lockable here
/// against the in-memory CapturingBatchTransport.
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class TransportConsumerWorkerBulkInsertInvariantTests {

  /// <summary>Captures the batch handler so the test can deliver simulated batches.</summary>
  private sealed class CapturingBatchTransport : ITransport {
    private Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? _batchHandler;
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ISubscription> SubscribeAsync(
        Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
        TransportDestination destination, CancellationToken cancellationToken = default)
      => Task.FromResult<ISubscription>(new _NopSubscription());
    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      _batchHandler = batchHandler;
      return Task.FromResult<ISubscription>(new _NopSubscription());
    }
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope envelope,
        TransportDestination destination, CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull
      => throw new NotImplementedException();
    public void Dispose() { }

    public async Task SimulateBatchReceivedAsync(IReadOnlyList<TransportMessage> batch) {
      if (_batchHandler is null) {
        throw new InvalidOperationException("SubscribeBatchAsync was never called by the worker.");
      }
      await _batchHandler(batch, CancellationToken.None);
    }

    private sealed class _NopSubscription : ISubscription {
      public bool IsActive { get; private set; } = true;
#pragma warning disable CS0067
      public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067
      public Task PauseAsync() { IsActive = false; return Task.CompletedTask; }
      public Task ResumeAsync() { IsActive = true; return Task.CompletedTask; }
      public void Dispose() { IsActive = false; }
    }
  }

  /// <summary>Registry where every type is consumed (bulk-insert path tested without drops).</summary>
  private sealed class AlwaysConsumedRegistry : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => true;
    public bool HasAnyConsumer(string messageType) => true;
  }

  /// <summary>Wraps an <see cref="IServiceScopeFactory"/> and counts scope creations.
  /// Test doubles can't observe DI scope creation directly; the counter is the only way
  /// to lock the "one scope per batch" invariant against a future refactor that
  /// accidentally moves scope creation inside the per-message foreach.</summary>
  private sealed class CountingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory {
    public int CreateScopeCallCount { get; private set; }
    public IServiceScope CreateScope() {
      CreateScopeCallCount++;
      return inner.CreateScope();
    }
  }

  /// <summary>Registry where ONLY a specific type is consumed — used for the drop-then-bulk-insert test.</summary>
  private sealed class SelectiveRegistry(string consumedInnerType) : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => string.Equals(messageType, consumedInnerType, StringComparison.Ordinal);
    public bool HasAnyConsumer(string messageType) => string.Equals(messageType, consumedInnerType, StringComparison.Ordinal);
  }

  private static MessageEnvelope<JsonElement> _makeEnvelope() => new() {
    MessageId = MessageId.New(),
    Payload = JsonDocument.Parse("{}").RootElement,
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = ServiceInstanceInfo.Unknown,
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  // Wrapper envelope-type strings for the two types in the drop-test. Inner types are what
  // the registry compares against.
  private const string CONSUMED_ENVELOPE_TYPE =
    "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.Consumed, TestApp]], Whizbang.Core";
  private const string CONSUMED_INNER = "TestApp.Consumed, TestApp";
  private const string DROPPED_ENVELOPE_TYPE =
    "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.Dropped, TestApp]], Whizbang.Core";

  private static (
      TransportConsumerWorker worker,
      CapturingBatchTransport transport,
      NoOpWorkCoordinator coordinator,
      ServiceProvider sp)
    _buildWorker(IReceptorRegistryQuery? registry) {
    var transport = new CapturingBatchTransport();
    var coordinator = new NoOpWorkCoordinator();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();

    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      sp.GetRequiredService<IServiceScopeFactory>(),
      new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null, metrics: null,
      NullLogger<TransportConsumerWorker>.Instance,
      receptorRegistry: registry);

    return (worker, transport, coordinator, sp);
  }

  [Test]
  public async Task BatchOf100SubscribedMessages_StoredViaSingleBulkInsertAsync() {
    // The load-bearing slice 6 invariant: a 100-message batch lands as ONE
    // StoreInboxMessagesAsync call carrying all 100 messages — not 100 separate calls,
    // not even 10 calls of 10. Critical for the a consumer BFF saga-fanout path where ~6,000
    // events arrive in fast bursts; per-message inserts would saturate Postgres.
    var registry = new AlwaysConsumedRegistry();
    var (worker, transport, coordinator, sp) = _buildWorker(registry);
    await using (sp) {
      using var cts = new CancellationTokenSource();
      _ = worker.StartAsync(cts.Token);
      await Task.Delay(150);

      var batch = new TransportMessage[100];
      for (var i = 0; i < 100; i++) {
        batch[i] = new TransportMessage(_makeEnvelope(), CONSUMED_ENVELOPE_TYPE);
      }
      await transport.SimulateBatchReceivedAsync(batch);

      cts.Cancel();

      await Assert.That(coordinator.StoreInboxCallCount).IsEqualTo(1)
        .Because("100 messages in one transport batch must produce exactly ONE StoreInboxMessagesAsync call.");
      await Assert.That(coordinator.StoreInboxBatchSizes).IsEquivalentTo([100])
        .Because("That single call must carry all 100 messages — not multiple smaller batches.");
      await Assert.That(coordinator.StoredInboxCount).IsEqualTo(100);
    }
  }

  [Test]
  public async Task MixedBatch_DroppedTypesFilteredBeforeBulkInsertAsync() {
    // Drop-gate + bulk-insert combined invariant: when a batch has both consumed and
    // unsubscribed types, the drop runs FIRST, then the remainder lands in a single bulk
    // call. The dropped messages never appear in StoredMessages — locks the slice 6
    // assertion "messages with no consumer never appear in wh_inbox".
    var registry = new SelectiveRegistry(CONSUMED_INNER);
    var (worker, transport, coordinator, sp) = _buildWorker(registry);
    await using (sp) {
      using var cts = new CancellationTokenSource();
      _ = worker.StartAsync(cts.Token);
      await Task.Delay(150);

      // 7 consumed + 3 dropped, interleaved
      await transport.SimulateBatchReceivedAsync([
        new TransportMessage(_makeEnvelope(), CONSUMED_ENVELOPE_TYPE),
        new TransportMessage(_makeEnvelope(), DROPPED_ENVELOPE_TYPE),
        new TransportMessage(_makeEnvelope(), CONSUMED_ENVELOPE_TYPE),
        new TransportMessage(_makeEnvelope(), CONSUMED_ENVELOPE_TYPE),
        new TransportMessage(_makeEnvelope(), DROPPED_ENVELOPE_TYPE),
        new TransportMessage(_makeEnvelope(), CONSUMED_ENVELOPE_TYPE),
        new TransportMessage(_makeEnvelope(), CONSUMED_ENVELOPE_TYPE),
        new TransportMessage(_makeEnvelope(), DROPPED_ENVELOPE_TYPE),
        new TransportMessage(_makeEnvelope(), CONSUMED_ENVELOPE_TYPE),
        new TransportMessage(_makeEnvelope(), CONSUMED_ENVELOPE_TYPE),
      ]);

      cts.Cancel();

      await Assert.That(coordinator.StoreInboxCallCount).IsEqualTo(1)
        .Because("Even with drops mixed in, the surviving messages still bulk-insert as one call.");
      await Assert.That(coordinator.StoreInboxBatchSizes).IsEquivalentTo([7])
        .Because("3 of 10 messages are dropped; the bulk insert carries the remaining 7.");
      await Assert.That(coordinator.StoredInboxCount).IsEqualTo(7);
    }
  }

  [Test]
  public async Task BatchOfAllDroppedMessages_NoBulkInsertCallAsync() {
    // Edge case: every message in the batch drops. StoreInboxMessagesAsync must NOT be
    // called at all — saves the empty-array round-trip to SQL.
    var registry = new SelectiveRegistry(CONSUMED_INNER);
    var (worker, transport, coordinator, sp) = _buildWorker(registry);
    await using (sp) {
      using var cts = new CancellationTokenSource();
      _ = worker.StartAsync(cts.Token);
      await Task.Delay(150);

      await transport.SimulateBatchReceivedAsync([
        new TransportMessage(_makeEnvelope(), DROPPED_ENVELOPE_TYPE),
        new TransportMessage(_makeEnvelope(), DROPPED_ENVELOPE_TYPE),
        new TransportMessage(_makeEnvelope(), DROPPED_ENVELOPE_TYPE),
      ]);

      cts.Cancel();

      await Assert.That(coordinator.StoreInboxCallCount).IsEqualTo(0)
        .Because("All-dropped batch must skip the bulk-insert call entirely.");
      await Assert.That(coordinator.StoredInboxCount).IsEqualTo(0);
    }
  }

  [Test]
  public async Task BatchProcessing_CreatesExactlyOneScopePerBatchAsync() {
    // Slice 6 plan calls for "Per-message scope created only when PreInbox is registered" —
    // gated by slice 3's bulk-buffer-append refactor (deferred). The current
    // TransportConsumerWorker behavior is BETTER than the plan goal: one scope per BATCH
    // regardless of message count or PreInbox registration. Lock that invariant so a
    // future refactor that accidentally moves CreateAsyncScope() inside the per-message
    // foreach (returning to per-message scope semantics) fails this test.
    var registry = new AlwaysConsumedRegistry();
    var transport = new CapturingBatchTransport();
    var coordinator = new NoOpWorkCoordinator();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    await using var sp = services.BuildServiceProvider();
    var countingFactory = new CountingScopeFactory(sp.GetRequiredService<IServiceScopeFactory>());

    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      countingFactory,
      new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null, metrics: null,
      NullLogger<TransportConsumerWorker>.Instance,
      receptorRegistry: registry);

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await Task.Delay(150);

    // StartAsync spins one scope for transport-readiness + infrastructure provisioning.
    // That scope is one-shot (not per-batch), so capture the count after startup settles
    // and measure the delta on the per-batch path.
    var preBatchScopeCount = countingFactory.CreateScopeCallCount;

    // 25-message batch — if scope was per-message, the delta would be 25.
    var batch = new TransportMessage[25];
    for (var i = 0; i < 25; i++) {
      batch[i] = new TransportMessage(_makeEnvelope(), CONSUMED_ENVELOPE_TYPE);
    }
    await transport.SimulateBatchReceivedAsync(batch);

    cts.Cancel();

    var perBatchDelta = countingFactory.CreateScopeCallCount - preBatchScopeCount;
    await Assert.That(perBatchDelta).IsEqualTo(1)
      .Because("TransportConsumerWorker creates exactly ONE scope per batch — not one per message. The scope is shared across all 25 message-builds and the bulk StoreInboxMessagesAsync call.");
  }

  [Test]
  public async Task TwoSequentialBatches_EachStoredAsSingleBulkInsertAsync() {
    // Two transport batches arrive in sequence. Each must produce its own single bulk
    // insert — no cross-batch coalescing (which would change ack semantics) and no
    // splitting of a single batch.
    var registry = new AlwaysConsumedRegistry();
    var (worker, transport, coordinator, sp) = _buildWorker(registry);
    await using (sp) {
      using var cts = new CancellationTokenSource();
      _ = worker.StartAsync(cts.Token);
      await Task.Delay(150);

      var batch1 = new TransportMessage[5];
      for (var i = 0; i < 5; i++) {
        batch1[i] = new TransportMessage(_makeEnvelope(), CONSUMED_ENVELOPE_TYPE);
      }
      await transport.SimulateBatchReceivedAsync(batch1);

      var batch2 = new TransportMessage[8];
      for (var i = 0; i < 8; i++) {
        batch2[i] = new TransportMessage(_makeEnvelope(), CONSUMED_ENVELOPE_TYPE);
      }
      await transport.SimulateBatchReceivedAsync(batch2);

      cts.Cancel();

      await Assert.That(coordinator.StoreInboxCallCount).IsEqualTo(2)
        .Because("Two transport batches must produce two StoreInboxMessagesAsync calls.");
      await Assert.That(coordinator.StoreInboxBatchSizes).IsEquivalentTo([5, 8])
        .Because("Each call carries its own batch; no coalescing or splitting.");
      await Assert.That(coordinator.StoredInboxCount).IsEqualTo(13);
    }
  }
}
