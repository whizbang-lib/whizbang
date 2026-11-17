// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
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

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2223: the paced repair drain records a request's buckets as awaiting their answer before it sends the request, and
/// takes the record back when the send fails. An origin can answer before the send returns (a fast origin, or this host's
/// continuation delayed under load); recorded after the send, that answer was overwritten by the late record, so the
/// bucket lost its answered state and counted against the outstanding-answer cap until the answer timeout. The drain runs
/// through its real loop against the real ledger, and the transport answers inside the publish call, so the answer lands
/// before the publish returns on every run.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/RepairDrainWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/395_ARepairSendThatFailsIsWithdrawn.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#repair-waits-for-its-answer</docs>
[Category("Integration")]
[Category("Shard5")]
public class RepairDrainRecordsBeforeSendingTests : EFCoreTestBase {
  private const string MESSAGE_TYPE = "Upstream.Contracts.OrderPlaced, Upstream.Contracts";
  private const int MAX_ATTEMPTS = 8;

  // Fixed instants, not the host clock: every function under test takes the caller's clock.
  private static readonly DateTimeOffset _t0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
  private static readonly TimeSpan _backoff = TimeSpan.FromSeconds(300);
  private static readonly TimeSpan _cooldown = TimeSpan.FromMinutes(60);
  private static readonly TimeSpan _answerTimeout = TimeSpan.FromMinutes(180);
  private static readonly TimeSpan _guard = TimeSpan.FromSeconds(60);

  // ===== The drain, end to end =====

  [Test]
  public async Task AnAnswerThatLandsBeforeThePublishReturns_IsKept_AndTheBucketIsNotLeftAwaitingAnotherAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    var key = _key(origin, stream, await _normalizedTypeAsync(conn));
    await _reportAsync(_coordinator(), key, at: _t0);
    var answered = 0;
    var transport = new InlineTransport(async (now, cancellationToken) => {
      // The origin's answer, applied before the publish call returns.
      var missing = TrackedGuid.New().Value;
      await _coordinator().StoreInboxMessagesAsync([_message(missing, stream, origin, 7)], partitionCount: 100, cancellationToken);
      _ = await _coordinator().IntegrityRecordBundleAnswerAsync(origin, [missing], [7], now, cancellationToken);
      answered++;
    });

    await _runOneTickAsync(origin, transport);

    await Assert.That(answered).IsEqualTo(1).Because("precondition: the request was sent and answered inside the publish");
    await Assert.That(await _scalarAsync<bool>(conn,
        "SELECT answer_pending_until IS NULL AND answered_at IS NOT NULL FROM wh_integrity_ledger WHERE stream_id = $1", stream))
      .IsTrue().Because("the bucket was recorded as awaiting its answer before the request left, so the answer that outran the publish stands");
    await Assert.That(await _coordinator().IntegrityCountAnswerPendingAsync(_t0.AddDays(1), 10)).IsEqualTo(0)
      .Because("an answered bucket does not count against the outstanding-answer cap");
  }

  [Test]
  public async Task ASendThatFails_IsWithdrawn_AndCostsNoAttemptAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    var key = _key(origin, stream, await _normalizedTypeAsync(conn));
    await _reportAsync(_coordinator(), key, at: _t0);
    var transport = new InlineTransport((_, _) => throw new TimeoutException("SendMessageAsync timed out (simulated broker throttle)"));

    await _runOneTickAsync(origin, transport);

    await Assert.That(transport.Attempts).IsEqualTo(1).Because("precondition: the drain claimed the bucket and tried to send");
    await Assert.That(await _scalarAsync<bool>(conn,
        "SELECT answer_pending_until IS NULL AND repair_attempts = 0 AND exhausted_at IS NULL FROM wh_integrity_ledger WHERE stream_id = $1", stream))
      .IsTrue().Because("the request never left: the record made before the send is taken back, and so is the claim's attempt");
  }

  // ===== The withdrawal, against the ledger =====

  [Test]
  public async Task Withdraw_ClearsTheWait_AndReturnsTheAttemptAsync() {
    // Through the throughput gate, as a host that bounds its coordinator calls runs it.
    var coordinator = _coordinator(new WorkCoordinatorGate(
      maxConcurrent: 1, logger: NullLogger<WorkCoordinatorGate>.Instance));
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    var key = _key(origin, stream);
    await _reportAsync(coordinator, key, at: _t0);
    var at = _t0.AddSeconds(1);
    _ = await _claimAsync(coordinator, origin, at);
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [key], null, null, at, _answerTimeout);

    var withdrawn = await _withdrawAsync(coordinator, origin, [key], at, _answerTimeout);

    await Assert.That(withdrawn).IsEqualTo(1);
    await Assert.That(await _scalarAsync<bool>(conn,
        "SELECT answer_pending_until IS NULL AND repair_attempts = 0 FROM wh_integrity_ledger WHERE stream_id = $1", stream))
      .IsTrue();
    await Assert.That((await _claimAsync(coordinator, origin, at.AddSeconds(1))).Count).IsEqualTo(0)
      .Because("the failed send still waits out its backoff, so a broker that just failed is not asked again on the next tick");
    await Assert.That((await _claimAsync(coordinator, origin, at + _backoff)).Count).IsEqualTo(1)
      .Because("once the backoff passes, the bucket is offered again without waiting for an answer that was never asked for");
  }

  [Test]
  public async Task Withdraw_OfTheClaimThatSpentTheLastAttempt_LiftsTheExhaustionAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    var key = _key(origin, stream);
    await _reportAsync(coordinator, key, at: _t0);
    var at = _t0.AddSeconds(1);
    var claimed = await coordinator.IntegrityClaimRepairDrainAsync([origin], at, _backoff, maxAttempts: 1, limit: 10);
    await Assert.That(claimed.Count).IsEqualTo(1).Because("precondition: the claim spends the only attempt");
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [key], null, null, at, _answerTimeout);

    var withdrawn = await _withdrawAsync(coordinator, origin, [key], at, _answerTimeout);

    await Assert.That(withdrawn).IsEqualTo(1);
    await Assert.That(await _scalarAsync<bool>(conn,
        "SELECT exhausted_at IS NULL AND repair_attempts = 0 FROM wh_integrity_ledger WHERE stream_id = $1", stream))
      .IsTrue().Because("an attempt that never reached the wire does not spend the budget");
  }

  [Test]
  public async Task Withdraw_AfterTheAnswerLanded_LeavesTheAnswerStandingAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    var key = _key(origin, stream, await _normalizedTypeAsync(conn));
    await _reportAsync(coordinator, key, at: _t0);
    var at = _t0.AddSeconds(1);
    _ = await _claimAsync(coordinator, origin, at);
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [key], null, null, at, _answerTimeout);
    var missing = TrackedGuid.New().Value;
    await _coordinator().StoreInboxMessagesAsync([_message(missing, stream, origin, 7)], partitionCount: 100);
    _ = await coordinator.IntegrityRecordBundleAnswerAsync(origin, [missing], [7], at.AddSeconds(1));

    var withdrawn = await _withdrawAsync(coordinator, origin, [key], at, _answerTimeout);

    await Assert.That(withdrawn).IsEqualTo(0);
    await Assert.That(await _scalarAsync<bool>(conn,
        "SELECT answer_pending_until IS NULL AND answered_at IS NOT NULL AND repair_attempts = 1 FROM wh_integrity_ledger WHERE stream_id = $1", stream))
      .IsTrue().Because("the request reached the origin after all: its answer and the attempt it cost stand");
  }

  [Test]
  public async Task Withdraw_OfAnotherRecord_LeavesItStandingAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await using var conn = await _openAsync();
    var key = _key(origin, stream);
    await _reportAsync(coordinator, key, at: _t0);
    var at = _t0.AddSeconds(1);
    _ = await _claimAsync(coordinator, origin, at);
    _ = await coordinator.IntegrityMarkAnswerPendingAsync(origin, [key], null, null, at, _answerTimeout);

    var otherTimeout = await _withdrawAsync(coordinator, origin, [key], at, _answerTimeout.Add(TimeSpan.FromMinutes(1)));
    var otherInstant = await _withdrawAsync(coordinator, origin, [key], at.AddSeconds(1), _answerTimeout);
    var otherOrigin = await _withdrawAsync(coordinator, TrackedGuid.New().Value, [key], at, _answerTimeout);

    await Assert.That(otherTimeout).IsEqualTo(0).Because("only the record the failed send's own mark made is taken back");
    await Assert.That(otherInstant).IsEqualTo(0);
    await Assert.That(otherOrigin).IsEqualTo(0);
    await Assert.That(await _scalarAsync<bool>(conn,
        "SELECT answer_pending_until IS NOT NULL AND repair_attempts = 1 FROM wh_integrity_ledger WHERE stream_id = $1", stream))
      .IsTrue();
  }

  // ===== Helpers =====

  private readonly List<WorkCoordinationDbContext> _contexts = [];

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinatorGate? gate = null) {
    var ctx = CreateDbContext();
    _contexts.Add(ctx);
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions(), gate: gate);
  }

  [After(Test)]
  public async Task DisposeContextsAsync() {
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
  }

  /// <summary>
  /// Starts the drain on a clock that signals each time the loop arms its one-second wait, lets it run exactly one tick,
  /// and stops it once that tick is over and the loop waits again.
  /// </summary>
  private async Task _runOneTickAsync(Guid origin, InlineTransport transport) {
    var clock = new ArmSignalingClock(_t0.AddMinutes(1));
    transport.Clock = clock;
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(origin, "origin-svc", _t0, "origin.requests");
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => _coordinator());
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton(tracker);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IServiceInstanceProvider>(new NamedInstance("drainer-svc"));
    var consumer = new TransportConsumerOptions();
    consumer.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumer);
    await using var provider = services.BuildServiceProvider();
    using var worker = new RepairDrainWorker(
      provider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new StreamIntegrityOptions {
        RepairMode = IntegrityRepairMode.AutoRepairCapped,
        RepairDrainRatePerSecond = 10,
        RepairAnswerTimeoutMinutes = (int)_answerTimeout.TotalMinutes,
        RepairRequestBackoffSeconds = (int)_backoff.TotalSeconds,
      }),
      NullLogger<RepairDrainWorker>.Instance,
      clock);

    await worker.StartAsync(CancellationToken.None);
    try {
      await clock.Armed.WaitAsync(_guard);   // the loop waits out its first second
      clock.Advance(TimeSpan.FromSeconds(1));
      await clock.Armed.WaitAsync(_guard);   // the tick is over and the loop waits again
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  // Through the interface, as the drain calls it.
  private static Task<int> _withdrawAsync(
      IWorkCoordinator coordinator, Guid origin, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, DateTimeOffset now,
      TimeSpan timeout) =>
    coordinator.IntegrityWithdrawAnswerPendingAsync(origin, keys, now, timeout);

  private static IntegrityRepairLedger.DivergenceKey _key(Guid origin, Guid stream, string eventType = "Contracts.AnswerProbe") =>
    new(origin, null, eventType, stream);

  private static async Task _reportAsync(
      EFCoreWorkCoordinator<WorkCoordinationDbContext> coordinator, IntegrityRepairLedger.DivergenceKey key, DateTimeOffset at) {
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      key.OriginServiceId, [new IntegrityReportObservation(key, 11, 21, 0, 0, IsDeficit: true, DeficitEvents: 1)],
      at, _cooldown) ?? throw new InvalidOperationException("the report batch failed");
  }

  private static Task<IReadOnlyList<IntegrityRepairDrainItem>> _claimAsync(
      EFCoreWorkCoordinator<WorkCoordinationDbContext> coordinator, Guid origin, DateTimeOffset at) =>
    coordinator.IntegrityClaimRepairDrainAsync([origin], at, _backoff, MAX_ATTEMPTS, limit: 10);

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static InboxMessage _message(Guid messageId, Guid streamId, Guid origin, long sequence) => new() {
    MessageId = messageId,
    HandlerName = "OrderPlacedHandler",
    Envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonElement.Parse("""{"reference":"R-1"}"""),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox, Source = MessageSource.Outbox },
    },
    EnvelopeType = EnvelopeTypeNameHelper.Format(MESSAGE_TYPE),
    StreamId = streamId,
    IsEvent = true,
    Flags = EventFlags.None,
    Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] },
    MessageType = MESSAGE_TYPE,
    SourceServiceId = origin,
    SourceCommitSequence = sequence,
    Priority = 250,
    RestoreIfMissing = true,
  };

  private static Task<string> _normalizedTypeAsync(NpgsqlConnection conn) =>
    _scalarAsync<string>(conn, "SELECT normalize_event_type($1)", MESSAGE_TYPE);

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var arg in args) {
      cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
    }
    return (T)(await cmd.ExecuteScalarAsync())!;
  }

  /// <summary>A transport that runs <c>onPublish</c> inside each publish, with the drain's clock reading.</summary>
  private sealed class InlineTransport(Func<DateTimeOffset, CancellationToken, Task> onPublish) : ITransport {
    public TimeProvider? Clock { get; set; }
    public int Attempts { get; private set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      Attempts++;
      return onPublish(Clock!.GetUtcNow(), cancellationToken);
    }
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  /// <summary>A fake clock that releases <see cref="Armed"/> each time a timer is created, which is each time the drain's
  /// loop starts waiting out its interval.</summary>
  private sealed class ArmSignalingClock(DateTimeOffset start) : TimeProvider {
    private readonly FakeTimeProvider _inner = new(start);
    public SemaphoreSlim Armed { get; } = new(0);
    public void Advance(TimeSpan by) => _inner.Advance(by);
    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
    public override long GetTimestamp() => _inner.GetTimestamp();
    public override long TimestampFrequency => _inner.TimestampFrequency;
    public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
      var timer = _inner.CreateTimer(callback, state, dueTime, period);
      Armed.Release();
      return timer;
    }
  }

  private sealed class NamedInstance(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.New().Value;
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }
}
