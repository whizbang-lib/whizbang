// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #2115: the idle ledger re-check, one tick at a time. A ledger row clears only when its bucket is compared again, and
/// the routine audit compares a bucket at most once a day, so rows written under an older compare rule lingered for
/// days. Each tick claims the stalest rows (oldest report first, older than the threshold, bounded per tick) and asks
/// the owner for a stream-level answer over each row's window, marked as a re-check: a compare, never a repair request.
/// Nothing runs while the service is busy, and only the maintainer instance runs it.
/// </summary>
/// <docs>resilience/stream-integrity#idle-ledger-recheck</docs>
/// <code-under-test>src/Whizbang.Core/Workers/IntegrityLedgerRecheckWorker.cs</code-under-test>
[NotInParallel(Order = 106)]
public class LedgerRecheckWorkerTests {
  // A fixed instant: the tick takes "now" explicitly, so it never needs the real clock.
  private static readonly DateTimeOffset _fixedNow = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task RecheckTick_AStaleRow_IsAskedForAgainAsAStreamLevelRecheck_NeverARepairAsync() {
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions(), origin);
    var stream = TrackedGuid.New().Value;
    coordinator.Stale.Add(new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", stream, 100, 500));

    await worker.RecheckTickAsync(_fixedNow, CancellationToken.None);

    var request = _deserializeManifestRequest(transport.Published.Single().Envelope);
    await Assert.That(request.Recheck).IsTrue()
      .Because("the answer must be compared as a re-check, which never asks for a repair");
    await Assert.That(request.Level).IsEqualTo(ManifestLevel.Streams);
    await Assert.That(request.EventTypes).IsEquivalentTo(["Contracts.TypeA"]);
    await Assert.That(request.Windowed).IsTrue();
    await Assert.That(request.SinceSequence).IsEqualTo(100L)
      .Because("the row's own window: the bucket is compared over exactly the range it diverged in");
    await Assert.That(request.UntilSequence).IsEqualTo(500L);
    await Assert.That(transport.Published.Single().Envelope.Target).IsEqualTo("origin-svc");
    await Assert.That(transport.Published.Any(p => p.EnvelopeType?.Contains(nameof(RequestRedeliveryCommand), StringComparison.Ordinal) == true))
      .IsFalse().Because("a re-check is a compare, never a repair request");
  }

  /// <summary>#2262: a re-check's answer passes the generation guard too, so its ask names the recorded generation.</summary>
  [Test]
  public async Task RecheckTick_NamesTheRecordedOriginGeneration_ReadOncePerOriginAsync() {
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions(), origin);
    coordinator.SealGeneration = 41;
    coordinator.Stale.Add(new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 100, 500));
    coordinator.Stale.Add(new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeB", TrackedGuid.New().Value, 100, 500));

    await worker.RecheckTickAsync(_fixedNow, CancellationToken.None);

    await Assert.That(transport.Published.Count).IsEqualTo(2);
    foreach (var (envelope, _, _) in transport.Published) {
      await Assert.That(_deserializeManifestRequest(envelope).KnownOriginGeneration).IsEqualTo(41L);
    }
    await Assert.That(coordinator.SealGenerationReads).IsEqualTo(1)
      .Because("the generation belongs to the origin, so one read serves every ask to it in the tick");
  }

  // #2208: a row with no stamped window was compared over the whole history, so it is answered again over the whole
  // history: an unwindowed answer from the origin's recompute, every stream of the type, compared to this service's own
  // whole-history fold. A window would prove only itself, and the origin caps a window opened from the start.
  [Test]
  public async Task RecheckTick_ARowWithNoWindow_IsAskedForAWholeHistoryRecomputeAsync() {
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, metrics) = _build(new StreamIntegrityOptions(), origin);
    coordinator.Stale.AddRange([
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, null, null),
      new IntegrityRepairDrainItem(origin, "tenant-b", "Contracts.TypeA", TrackedGuid.New().Value, null, null),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 100, 500),
    ]);

    await worker.RecheckTickAsync(_fixedNow, CancellationToken.None);

    var requests = transport.Published.ConvertAll(p => _deserializeManifestRequest(p.Envelope));
    await Assert.That(requests.Count).IsEqualTo(2)
      .Because("the rows with no window share one whole-history ask; the windowed row keeps its own");
    var whole = requests.Single(r => !r.Windowed);
    await Assert.That(whole.UseRecompute).IsTrue()
      .Because("the whole history is answered from the origin's recompute, never its maintained table");
    await Assert.That(whole.Recheck).IsTrue().Because("a re-check is a compare, never a repair request");
    await Assert.That(whole.Level).IsEqualTo(ManifestLevel.Streams);
    await Assert.That(whole.EventTypes).IsEquivalentTo(["Contracts.TypeA"]);
    await Assert.That(whole.MaxDigests).IsNull().Because("an unwindowed answer is not paged");
    await Assert.That(requests.Single(r => r.Windowed).UseRecompute).IsFalse();
    await Assert.That(metrics.Rechecked).IsEqualTo(3L);
  }

  [Test]
  public async Task RecheckTick_ClaimsTheStalestRows_BoundedPerTick_OlderThanTheThresholdAsync() {
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, _, _) = _build(
      new StreamIntegrityOptions { LedgerRecheckRowsPerTick = 7, LedgerRecheckAfterMinutes = 30 }, origin);

    await worker.RecheckTickAsync(_fixedNow, CancellationToken.None);

    var (origins, reportedBefore, now, limit) = coordinator.Claims.Single();
    await Assert.That(limit).IsEqualTo(7).Because("one tick never claims more than its bound");
    await Assert.That(reportedBefore).IsEqualTo(_fixedNow.AddMinutes(-30))
      .Because("only rows whose report is older than the threshold are re-checked");
    await Assert.That(now).IsEqualTo(_fixedNow);
    await Assert.That(origins).IsEquivalentTo([origin])
      .Because("only an origin whose request topic is known can be asked");
  }

  [Test]
  public async Task RecheckTick_RowsThatShareAWindow_AreAskedInOneRequest_OthersInTheirOwnAsync() {
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, metrics) = _build(new StreamIntegrityOptions(), origin);
    coordinator.Stale.AddRange([
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 100, 500),
      new IntegrityRepairDrainItem(origin, "tenant-b", "Contracts.TypeA", TrackedGuid.New().Value, 100, 500),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 500, 900),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeB", TrackedGuid.New().Value, 100, 500),
    ]);

    await worker.RecheckTickAsync(_fixedNow, CancellationToken.None);

    await Assert.That(transport.Published.Count).IsEqualTo(3)
      .Because("one stream-level answer covers every tenant of a type over a window");
    await Assert.That(metrics.Rechecked).IsEqualTo(4L).Because("every claimed row counts as re-checked");
  }

  [Test]
  public async Task RecheckTick_WhileTheServiceIsBusy_ClaimsAndSendsNothingAsync() {
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, _) = _build(
      new StreamIntegrityOptions(), origin,
      housekeeping: new HousekeepingCoordinator());
    coordinator.Backlog = new ServiceBacklog { UnprocessedInboxRows = 500, ActiveLeasedRows = 3 };
    coordinator.Stale.Add(new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 100, 500));

    await worker.RecheckTickAsync(_fixedNow, CancellationToken.None);

    await Assert.That(coordinator.Claims).IsEmpty()
      .Because("a busy service does not spend its database on re-checks; the idle gate defers them");
    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  [Arguments(false, true)]
  [Arguments(true, false)]
  public async Task RecheckTick_DisabledOrAuditOff_DoesNothingAsync(bool recheckEnabled, bool auditEnabled) {
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, _) = _build(
      new StreamIntegrityOptions { LedgerRecheckEnabled = recheckEnabled, AuditEnabled = auditEnabled }, origin);
    coordinator.Stale.Add(new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 100, 500));

    await worker.RecheckTickAsync(_fixedNow, CancellationToken.None);

    await Assert.That(coordinator.Claims).IsEmpty();
    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  public async Task RecheckTick_WithNoOriginWhoseAddressIsKnown_ClaimsNothingAsync() {
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions(), origin, learnTopic: false);

    await worker.RecheckTickAsync(_fixedNow, CancellationToken.None);

    await Assert.That(coordinator.Claims).IsEmpty()
      .Because("a row claimed for an origin that cannot be asked would only be marked looked at for nothing");
    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  public async Task RecheckTick_NothingStale_SendsNothingAsync() {
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, metrics) = _build(new StreamIntegrityOptions(), origin);

    await worker.RecheckTickAsync(_fixedNow, CancellationToken.None);

    await Assert.That(coordinator.Claims.Count).IsEqualTo(1);
    await Assert.That(transport.Published).IsEmpty().Because("the re-check stops when no row is older than the threshold");
    await Assert.That(metrics.Rechecked).IsEqualTo(0L);
  }

  [Test]
  public async Task ExecuteAsync_ATickThatFails_IsLogged_AndTheNextTickRunsAsync() {
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, _, _) = _build(new StreamIntegrityOptions { LedgerRecheckIntervalSeconds = 1 }, origin,
      clock: new FakeTimeProvider(_fixedNow));
    coordinator.ThrowOnClaim.Enqueue(new InvalidOperationException("the ledger is unreachable this tick"));
    // Every wait returns at once, so the loop runs tick after tick on its own: no clock to race against the loop.
    worker.DelayAsync = static (_, token) => token.IsCancellationRequested ? Task.FromCanceled(token) : Task.CompletedTask;

    await worker.StartAsync(CancellationToken.None);

    await Assert.That(async () => await coordinator.SecondClaim.Task.WaitAsync(TimeSpan.FromSeconds(30))).ThrowsNothing()
      .Because("a failed tick is logged and the next one tries again; one failure never stops the re-check");
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_ACancellationInsideATick_EndsTheLoopAsync() {
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, _, _) = _build(new StreamIntegrityOptions { LedgerRecheckIntervalSeconds = 1 }, origin,
      clock: new FakeTimeProvider(_fixedNow));
    coordinator.ThrowOnClaim.Enqueue(new OperationCanceledException("shutting down"));
    worker.DelayAsync = static (_, token) => token.IsCancellationRequested ? Task.FromCanceled(token) : Task.CompletedTask;

    await worker.StartAsync(CancellationToken.None);

    await Assert.That(async () => await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30))).ThrowsNothing()
      .Because("a cancellation is how the host stops the worker, so it ends the loop rather than counting as a failure");
    await Assert.That(coordinator.Claims.Count).IsEqualTo(1);
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_StoppingWhileWaiting_EndsTheLoopAsync() {
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, _, _) = _build(new StreamIntegrityOptions(), origin, clock: new FakeTimeProvider(_fixedNow));
    using var stopping = new CancellationTokenSource();

    // Called directly, the loop runs synchronously into its first wait on the fake clock, which never advances.
    var loop = worker.RunLoopAsync(stopping.Token);
    await Assert.That(loop.IsCompleted).IsFalse().Because("the loop is waiting out its interval");
    await stopping.CancelAsync();

    await Assert.That(async () => await loop.WaitAsync(TimeSpan.FromSeconds(30))).ThrowsNothing()
      .Because("stopping while the loop waits ends it rather than surfacing the cancellation");
    await Assert.That(coordinator.Claims).IsEmpty().Because("the interval never elapsed, so no tick ran");
  }

  [Test]
  public async Task RecheckTick_WithoutAServiceIdentity_ClaimsNothingAsync() {
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions(), origin, withInstanceProvider: false);

    await worker.RecheckTickAsync(_fixedNow, CancellationToken.None);

    await Assert.That(coordinator.Claims).IsEmpty().Because("without a service name there is no return address for the answer");
    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  public async Task RecheckTick_WithoutMetrics_StillAsksAsync() {
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions(), origin, withMetrics: false);
    coordinator.Stale.Add(new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 100, 500));

    await worker.RecheckTickAsync(_fixedNow, CancellationToken.None);

    await Assert.That(transport.Published.Count).IsEqualTo(1).Because("counting is optional; the re-check is not");
  }

  [Test]
  public async Task Constructor_RejectsMissingOptions_AndDefaultsTheClockAsync() {
    var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    await Assert.That(() => new IntegrityLedgerRecheckWorker(scopeFactory, null!, NullLogger<IntegrityLedgerRecheckWorker>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new IntegrityLedgerRecheckWorker(scopeFactory, new NullValueOptions(), NullLogger<IntegrityLedgerRecheckWorker>.Instance))
      .Throws<ArgumentNullException>();
    var worker = new IntegrityLedgerRecheckWorker(
      scopeFactory, Options.Create(new StreamIntegrityOptions { LedgerRecheckEnabled = false }), NullLogger<IntegrityLedgerRecheckWorker>.Instance);
    await Assert.That(async () => await worker.RecheckTickAsync(_fixedNow, CancellationToken.None)).ThrowsNothing()
      .Because("without a clock of its own the worker uses the system clock");
  }

  private sealed class NullValueOptions : IOptions<StreamIntegrityOptions> {
    public StreamIntegrityOptions Value => null!;
  }

  [Test]
  public async Task TheWorker_IsRegisteredWithTheIntegrityWorkersAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangWorkers();

    await Assert.That(services.Any(d => d.ServiceType == typeof(IntegrityLedgerRecheckWorker))).IsTrue()
      .Because("a consumer that registers the workers gets the idle re-check with them, on by default");
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static (IntegrityLedgerRecheckWorker Worker, RecheckCoordinator Coordinator, CaptureTransport Transport, MetricsProbe Metrics) _build(
      StreamIntegrityOptions options, Guid origin, bool learnTopic = true, HousekeepingCoordinator? housekeeping = null,
      FakeTimeProvider? clock = null, bool withInstanceProvider = true, bool withMetrics = true) {
    var coordinator = new RecheckCoordinator();
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(origin, "origin-svc", _fixedNow, learnTopic ? "origin.requests" : null);
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddMetrics();
    services.AddSingleton(sp => new WhizbangMetrics(meterFactory: sp.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));
    if (withMetrics) {
      services.AddSingleton<StreamIntegrityMetrics>();
    }
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton(tracker);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    if (withInstanceProvider) {
      services.AddSingleton<IServiceInstanceProvider>(new InstanceProvider("rechecker-svc"));
    }
    if (housekeeping is not null) {
      services.AddSingleton(housekeeping);
    }
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    var probe = new MetricsProbe(sp.GetService<StreamIntegrityMetrics>()
      ?? new StreamIntegrityMetrics(sp.GetRequiredService<WhizbangMetrics>()));
    var worker = new IntegrityLedgerRecheckWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(options),
      NullLogger<IntegrityLedgerRecheckWorker>.Instance,
      clock ?? new FakeTimeProvider(_fixedNow));
    return (worker, coordinator, transport, probe);
  }

  /// <summary>Reads the re-check counter back: it is an observable counter, so each read collects its series' totals.</summary>
  private sealed class MetricsProbe : IDisposable {
    private readonly System.Diagnostics.Metrics.MeterListener _listener = new();
    private long _observed;

    public MetricsProbe(StreamIntegrityMetrics metrics) {
      var instrument = metrics.LedgerRowsRechecked.Instrument;
      _listener.InstrumentPublished = (published, listener) => {
        if (ReferenceEquals(published, instrument)) {
          listener.EnableMeasurementEvents(published);
        }
      };
      _listener.SetMeasurementEventCallback<long>((_, value, _, _) => _observed += value);
      _listener.Start();
    }

    public long Rechecked {
      get {
        _observed = 0;
        _listener.RecordObservableInstruments();
        return _observed;
      }
    }

    public void Dispose() => _listener.Dispose();
  }

  private static RequestIntegrityManifest _deserializeManifestRequest(IMessageEnvelope envelope) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    return (RequestIntegrityManifest)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(RequestIntegrityManifest)))!;
  }

  private sealed class RecheckCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public List<IntegrityRepairDrainItem> Stale { get; } = [];
    public List<(IReadOnlyList<Guid> Origins, DateTimeOffset ReportedBefore, DateTimeOffset Now, int Limit)> Claims { get; } = [];
    public ServiceBacklog? Backlog { get; set; }
    /// <summary>Thrown by the next claims, one each, before any claim is recorded.</summary>
    public Queue<Exception> ThrowOnClaim { get; } = new();
    /// <summary>Completes once this many claims have been recorded, so a loop test waits on the effect, not a clock.</summary>
    public TaskCompletionSource SecondClaim { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<IReadOnlyList<IntegrityRepairDrainItem>> IntegrityClaimLedgerRecheckAsync(
        IReadOnlyList<Guid> originIds, DateTimeOffset reportedBefore, DateTimeOffset now, int limit,
        CancellationToken cancellationToken = default) {
      lock (Claims) {
        if (ThrowOnClaim.TryDequeue(out var failure)) {
          Claims.Add((originIds, reportedBefore, now, limit));
          throw failure;
        }
        Claims.Add((originIds, reportedBefore, now, limit));
        if (Claims.Count >= 2) {
          SecondClaim.TrySetResult();
        }
      }
      var take = Stale.Where(e => originIds.Contains(e.OriginServiceId)).Take(limit).ToList();
      return Task.FromResult<IReadOnlyList<IntegrityRepairDrainItem>>(take);
    }

    public ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken cancellationToken = default) =>
      ValueTask.FromResult(Backlog);

    /// <summary>#2262: the origin generation recorded beside the seal.</summary>
    public long? SealGeneration { get; set; }
    public int SealGenerationReads;

    public Task<long?> GetIntegritySealGenerationAsync(Guid originServiceId, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref SealGenerationReads);
      return Task.FromResult(SealGeneration);
    }
  }

  private sealed class CaptureTransport : ITransport {
    public List<(IMessageEnvelope Envelope, TransportDestination Destination, string? EnvelopeType)> Published { get; } = [];
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      lock (Published) {
        Published.Add((envelope, destination, envelopeType));
      }
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  private sealed class InstanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.New().Value;
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }
}
