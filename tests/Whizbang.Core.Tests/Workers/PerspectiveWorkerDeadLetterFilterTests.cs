using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable IDE1006 // local helpers _row, _buildWorker — internal convention used elsewhere in the suite

/// <summary>
/// v0.502 slice C.4c regression locks — the perspective worker MUST move rows whose
/// <c>attempts &gt; MaxPerspectiveEventAttempts</c> into <c>wh_dead_letters</c> BEFORE
/// the deserialization + apply path runs. Symmetric with the inbox + outbox pre-apply
/// dead-letter checks shipped in C.4a/C.4b. Without this filter, perspective events
/// continue to be re-claimed indefinitely — the bug class chunk A was meant to close.
/// </summary>
public class PerspectiveWorkerDeadLetterFilterTests {

  private sealed class CapturingDeadLetterStore : IDeadLetterStore {
    public readonly List<(Guid SourceId, string SourceTable, MessageFailureReason Reason, string Generation)> Moves = [];
    public bool Throw { get; set; }

    public Task<Guid?> MoveAsync(
      Guid deadLetterId,
      string sourceTable,
      Guid sourceId,
      MessageFailureReason failureReason,
      string? errorText,
      Guid instanceId,
      string generation,
      CancellationToken ct = default) {
      if (Throw) {
        throw new InvalidOperationException("simulated DLQ store failure");
      }
      Moves.Add((sourceId, sourceTable, failureReason, generation));
      return Task.FromResult<Guid?>(deadLetterId);
    }
  }

  private sealed class FixedGeneration(string gen) : IGenerationProvider {
    public string GetGeneration() => gen;
  }

  private sealed class FixedInstance(Guid id) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = id;
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private static PerspectiveWorker _buildWorker(
      int? maxAttempts,
      IDeadLetterStore? store,
      IGenerationProvider? gen,
      DeadLetterMetrics? metrics,
      Guid instanceId) {
    var services = new ServiceCollection();
    services.AddLogging();
    var provider = services.BuildServiceProvider();
    return new PerspectiveWorker(
      instanceProvider: new FixedInstance(instanceId),
      scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions { MaxPerspectiveEventAttempts = maxAttempts }),
      deadLetterStore: store,
      generationProvider: gen,
      deadLetterMetrics: metrics,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());
  }

  private static StreamEventData _row(int attempts, int failures = 0) {
    return new StreamEventData {
      StreamId = (Guid)TrackedGuid.NewMedo(),
      EventId = (Guid)TrackedGuid.NewMedo(),
      EventType = "TestEvent",
      EventData = "{}",
      EventWorkId = (Guid)TrackedGuid.NewMedo(),
      Attempts = attempts,
      Failures = failures,
    };
  }

  // -------------------------------------------------------------------------------------------
  // Issue #700: the decision reads failures, never the lease count. attempts is bumped by every
  // claim, and a lease can lapse without an apply (the worker skipped the row, died mid-batch, or
  // classified it as recently processed). Under a backlog that turned lease churn into thrash-
  // casualty dead letters for perfectly good events.
  // -------------------------------------------------------------------------------------------

  [Test]
  public async Task LeaseCountAboveMax_WithNoFailures_SurvivesAsync() {
    var store = new CapturingDeadLetterStore();
    var worker = _buildWorker(maxAttempts: 10, store: store, gen: new FixedGeneration("g"),
      metrics: null, instanceId: (Guid)TrackedGuid.NewMedo());
    var churned = _row(attempts: 99, failures: 0);

    var survivors = await worker.FilterDeadLetteredAsync([churned], CancellationToken.None);

    await Assert.That(survivors.Count).IsEqualTo(1)
      .Because("ninety-nine leases with no failed apply is scheduling churn, not poison");
    await Assert.That(store.Moves.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FailuresExceedMax_WithFewLeases_MovesToDeadLetterAsync() {
    var store = new CapturingDeadLetterStore();
    var worker = _buildWorker(maxAttempts: 10, store: store, gen: new FixedGeneration("g"),
      metrics: null, instanceId: (Guid)TrackedGuid.NewMedo());
    var poison = _row(attempts: 1, failures: 11);

    var survivors = await worker.FilterDeadLetteredAsync([poison], CancellationToken.None);

    await Assert.That(survivors.Count).IsEqualTo(0)
      .Because("eleven failed applies is the threshold crossing, whatever the lease count says");
    await Assert.That(store.Moves.Count).IsEqualTo(1);
    await Assert.That(store.Moves[0].SourceId).IsEqualTo(poison.EventWorkId);
  }

  [Test]
  public async Task NoStore_PassesThroughAllRowsAsync() {
    var worker = _buildWorker(maxAttempts: 5, store: null, gen: null, metrics: null,
      instanceId: (Guid)TrackedGuid.NewMedo());
    var rows = new List<StreamEventData> { _row(attempts: 1), _row(attempts: 99) };

    var survivors = await worker.FilterDeadLetteredAsync(rows, CancellationToken.None);

    await Assert.That(survivors.Count).IsEqualTo(2);
  }

  [Test]
  public async Task NoMaxAttempts_PassesThroughAllRowsAsync() {
    var store = new CapturingDeadLetterStore();
    var worker = _buildWorker(maxAttempts: null, store: store, gen: new FixedGeneration("g"),
      metrics: null, instanceId: (Guid)TrackedGuid.NewMedo());
    var rows = new List<StreamEventData> { _row(attempts: 99) };

    var survivors = await worker.FilterDeadLetteredAsync(rows, CancellationToken.None);

    await Assert.That(survivors.Count).IsEqualTo(1);
    await Assert.That(store.Moves.Count).IsEqualTo(0);
  }

  [Test]
  public async Task AttemptsAtMax_DoesNotDeadLetterAsync() {
    var store = new CapturingDeadLetterStore();
    var worker = _buildWorker(maxAttempts: 10, store: store, gen: new FixedGeneration("g"),
      metrics: null, instanceId: (Guid)TrackedGuid.NewMedo());
    // attempts=10, max=10 — strict-greater-than means this row survives (10 attempts permitted)
    var rows = new List<StreamEventData> { _row(attempts: 10) };

    var survivors = await worker.FilterDeadLetteredAsync(rows, CancellationToken.None);

    await Assert.That(survivors.Count).IsEqualTo(1);
    await Assert.That(store.Moves.Count).IsEqualTo(0);
  }

  [Test]
  public async Task AttemptsExceedsMax_MovesToDeadLetterAndDropsRowAsync() {
    var store = new CapturingDeadLetterStore();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var worker = _buildWorker(maxAttempts: 10, store: store, gen: new FixedGeneration("whizbang/test-gen"),
      metrics: null, instanceId: instanceId);
    var doomed = _row(attempts: 11, failures: 11);
    var keeper = _row(attempts: 3, failures: 3);
    var rows = new List<StreamEventData> { doomed, keeper };

    var survivors = await worker.FilterDeadLetteredAsync(rows, CancellationToken.None);

    await Assert.That(survivors.Count).IsEqualTo(1);
    await Assert.That(survivors[0].EventWorkId).IsEqualTo(keeper.EventWorkId);
    await Assert.That(store.Moves.Count).IsEqualTo(1);
    await Assert.That(store.Moves[0].SourceId).IsEqualTo(doomed.EventWorkId);
    await Assert.That(store.Moves[0].SourceTable).IsEqualTo(DeadLetterSourceTable.PERSPECTIVE_EVENTS);
    await Assert.That(store.Moves[0].Reason).IsEqualTo(MessageFailureReason.MaxAttemptsExceeded);
    await Assert.That(store.Moves[0].Generation).IsEqualTo("whizbang/test-gen");
  }

  [Test]
  public async Task DeadLetterStoreThrows_KeepsRowInApplySetAsync() {
    var store = new CapturingDeadLetterStore { Throw = true };
    var worker = _buildWorker(maxAttempts: 5, store: store, gen: new FixedGeneration("g"),
      metrics: null, instanceId: (Guid)TrackedGuid.NewMedo());
    var doomed = _row(attempts: 99, failures: 99);
    var rows = new List<StreamEventData> { doomed };

    var survivors = await worker.FilterDeadLetteredAsync(rows, CancellationToken.None);

    // Fallback policy: if MoveAsync throws, the row remains in apply set so the next
    // claim cycle can retry (rather than silently disappearing).
    await Assert.That(survivors.Count).IsEqualTo(1);
    await Assert.That(survivors[0].EventWorkId).IsEqualTo(doomed.EventWorkId);
  }

  [Test]
  public async Task MetricsIncrementedOnDeadLetterAsync() {
    var store = new CapturingDeadLetterStore();
    var metrics = new DeadLetterMetrics(new WhizbangMetrics());
    var worker = _buildWorker(maxAttempts: 5, store: store, gen: new FixedGeneration("g"),
      metrics: metrics, instanceId: (Guid)TrackedGuid.NewMedo());
    var rows = new List<StreamEventData> { _row(attempts: 11, failures: 11) };

    // Smoke check: counter is wired so Add(1, ...) is reached on the dead-letter path.
    // Full metric-value assertion would require a MeterListener; the store + metrics-not-null
    // path verifies the call-site doesn't NPE when metrics are present.
    var survivors = await worker.FilterDeadLetteredAsync(rows, CancellationToken.None);

    await Assert.That(survivors.Count).IsEqualTo(0);
    await Assert.That(store.Moves.Count).IsEqualTo(1);
    await Assert.That(metrics.Added).IsNotNull();
  }
}
