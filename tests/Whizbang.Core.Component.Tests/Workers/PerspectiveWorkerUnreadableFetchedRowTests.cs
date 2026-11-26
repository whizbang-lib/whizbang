// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Options;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable IDE1006 // local helpers _row, _buildWorker: the suite's convention

/// <summary>
/// #2591: a fetched row the read returned without its event's body is taken out alone, by name: an ephemeral event's
/// reaped body is dead-lettered on first sight, and a Sourced event's missing body is failed for the bounded retry and
/// logged at Error. Every other row is handed on, in the order the read returned them, wherever the unreadable row sits. The end-to-end shape (real Postgres, real claim loop, dead-lettering) is in
/// <c>tests/Whizbang.Data.EFCore.Postgres.Tests/ReapedBodyBatchIsolationTests.cs</c>.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
public class PerspectiveWorkerUnreadableFetchedRowTests {
  [Test]
  public async Task NoUnreadableRow_HandsOnTheSameListAndFailsNothingAsync() {
    var failures = new CapturingFailureChannel();
    var worker = _buildWorker(failures, NullLogger<PerspectiveWorker>.Instance, NullDeadLetterStore.Instance, metrics: null);
    List<StreamEventData> rows = [_row(), _row()];

    var kept = await worker.FailRowsReadWithoutTheirEventAsync(rows, CancellationToken.None);

    await Assert.That(ReferenceEquals(kept, rows)).IsTrue().Because("nothing to take out, so nothing is copied");
    await Assert.That(failures.Items).IsEmpty();
  }

  /// <summary>A Sourced event's body is never reaped: its absence is failed for the bounded retry and logged at Error.</summary>
  [Test]
  [Arguments(0)]
  [Arguments(1)]
  [Arguments(2)]
  public async Task ASourcedRowWithoutItsBody_IsFailedForRetryAlone_ByName_AtError_AndTheRestKeepTheirOrderAsync(int position) {
    var failures = new CapturingFailureChannel();
    var logs = new ListLogger();
    var store = new CapturingDeadLetterStore();
    var worker = _buildWorker(failures, logs, store, metrics: null);
    var missing = _unreadable(StreamEventData.BODY_MISSING);
    List<StreamEventData> readable = [_row(), _row()];
    var rows = new List<StreamEventData>(readable);
    rows.Insert(position, missing);

    var kept = await worker.FailRowsReadWithoutTheirEventAsync(rows, CancellationToken.None);

    await Assert.That(kept).IsEquivalentTo(readable, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(store.Moves).IsEmpty().Because("the bounded retry decides, through the pre-apply dead-letter filter");
    var (category, failure) = failures.Items.Single();
    await Assert.That(category).IsEqualTo(WorkCategory.PerspectiveEvent);
    await Assert.That(failure.MessageId).IsEqualTo(missing.EventWorkId);
    await Assert.That(failure.Reason).IsEqualTo(MessageFailureReason.EventBodyMissing);
    await Assert.That(failure.Error).Contains(missing.StreamId.ToString()).And.Contains(PERSPECTIVE)
      .And.Contains(missing.EventId.ToString()).And.Contains(StreamEventData.BODY_MISSING);
    var line = logs.Entries.Single(e => e.EventId == MISSING_BODY_EVENT_ID);
    await Assert.That(line.Level).IsEqualTo(LogLevel.Error);
    await Assert.That(line.Message).Contains(missing.StreamId.ToString()).And.Contains(PERSPECTIVE);
  }

  /// <summary>
  /// An ephemeral event's reaped body never comes back: the row is dead-lettered on first sight, with no failure recorded
  /// first, counted when the dead-letter metrics are wired and logged at Warning.
  /// </summary>
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task AReapedRow_IsDeadLetteredOnFirstSight_AndTheRestKeepTheirOrderAsync(bool withMetrics) {
    var failures = new CapturingFailureChannel();
    var logs = new ListLogger();
    var store = new CapturingDeadLetterStore();
    var worker = _buildWorker(failures, logs, store, withMetrics ? _metrics() : null);
    var reaped = _unreadable(StreamEventData.BODY_REAPED);
    var other = _row();

    var kept = await worker.FailRowsReadWithoutTheirEventAsync([reaped, other], CancellationToken.None);

    List<StreamEventData> expected = [other];
    await Assert.That(kept).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(failures.Items).IsEmpty().Because("no retry is spent on a body that never comes back");
    var (sourceId, sourceTable, reason, error) = store.Moves.Single();
    await Assert.That(sourceId).IsEqualTo(reaped.EventWorkId);
    await Assert.That(sourceTable).IsEqualTo(DeadLetterSourceTable.PERSPECTIVE_EVENTS);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.EventBodyMissing);
    await Assert.That(error).Contains(reaped.StreamId.ToString()).And.Contains(PERSPECTIVE).And.Contains(StreamEventData.BODY_REAPED);
    var line = logs.Entries.Single(e => e.EventId == REAPED_BODY_EVENT_ID);
    await Assert.That(line.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(line.Message).Contains(reaped.StreamId.ToString()).And.Contains(PERSPECTIVE);
  }

  /// <summary>
  /// A reaped row this worker cannot dead-letter (no dead-letter store, or a move that throws) is failed for the bounded
  /// retry instead, with a Warning that says why, so it is never left to be leased again with nothing recorded.
  /// </summary>
  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task AReapedRowThatCannotBeDeadLettered_IsFailedForRetryInsteadAsync(bool storeThrows) {
    var failures = new CapturingFailureChannel();
    var logs = new ListLogger();
    IDeadLetterStore store = storeThrows ? new CapturingDeadLetterStore { Throw = true } : NullDeadLetterStore.Instance;
    var worker = _buildWorker(failures, logs, store, metrics: null);
    var reaped = _unreadable(StreamEventData.BODY_REAPED);

    var kept = await worker.FailRowsReadWithoutTheirEventAsync([reaped], CancellationToken.None);

    await Assert.That(kept).IsEmpty();
    var (_, failure) = failures.Items.Single();
    await Assert.That(failure.MessageId).IsEqualTo(reaped.EventWorkId);
    await Assert.That(failure.Reason).IsEqualTo(MessageFailureReason.EventBodyMissing);
    var line = logs.Entries.Single(e => e.EventId == REAPED_BODY_NOT_MOVED_EVENT_ID);
    await Assert.That(line.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(line.Message).Contains(reaped.StreamId.ToString()).And.Contains(PERSPECTIVE);
    await Assert.That(line.Exception is InvalidOperationException).IsEqualTo(storeThrows);
  }

  /// <summary>Two rows without bodies in one batch are each handled by their own case; the readable row between them goes on.</summary>
  [Test]
  public async Task TwoRowsWithoutBodies_AreEachHandledByTheirOwnCase_AndTheReadableRowGoesOnAsync() {
    var failures = new CapturingFailureChannel();
    var store = new CapturingDeadLetterStore();
    var worker = _buildWorker(failures, new ListLogger(), store, metrics: null);
    var missing = _unreadable(StreamEventData.BODY_MISSING);
    var readable = _row();
    var reaped = _unreadable(StreamEventData.BODY_REAPED);

    var kept = await worker.FailRowsReadWithoutTheirEventAsync([missing, readable, reaped], CancellationToken.None);

    List<StreamEventData> expected = [readable];
    await Assert.That(kept).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(failures.Items.Single().failure.MessageId).IsEqualTo(missing.EventWorkId);
    await Assert.That(store.Moves.Single().SourceId).IsEqualTo(reaped.EventWorkId);
  }

  // ── helpers ─────────────────────────────────────────────────────────────────────────────────

  private const string PERSPECTIVE = "ActiveSessions+Projection";
  private const int REAPED_BODY_EVENT_ID = 84;
  private const int MISSING_BODY_EVENT_ID = 85;
  private const int REAPED_BODY_NOT_MOVED_EVENT_ID = 86;

  private static StreamEventData _unreadable(string reason) => _row() with {
    EventData = string.Empty,
    UnreadableReason = reason,
    PerspectiveName = PERSPECTIVE,
    Version = 1,
    Failures = 2,
  };

  private static DeadLetterMetrics _metrics() =>
    new(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));

  private sealed class CapturingDeadLetterStore : IDeadLetterStore {
    public List<(Guid SourceId, string SourceTable, MessageFailureReason Reason, string? Error)> Moves { get; } = [];
    public bool Throw { get; init; }

    public Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId, MessageFailureReason failureReason,
        string? errorText, Guid instanceId, string generation, CancellationToken ct = default) {
      if (Throw) {
        throw new InvalidOperationException("simulated dead-letter store failure");
      }
      Moves.Add((sourceId, sourceTable, failureReason, errorText));
      return Task.FromResult<Guid?>(deadLetterId);
    }
  }

  private static StreamEventData _row() => new() {
    StreamId = (Guid)TrackedGuid.New(),
    EventId = (Guid)TrackedGuid.New(),
    EventType = "TestEvent",
    EventData = "{}",
    EventWorkId = (Guid)TrackedGuid.New(),
  };

  private sealed class ListLogger : ILogger<PerspectiveWorker> {
    public List<(LogLevel Level, int EventId, string Message, Exception? Exception)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, eventId.Id, formatter(state, exception), exception));
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

  private static PerspectiveWorker _buildWorker(CapturingFailureChannel failures, ILogger<PerspectiveWorker> logger,
      IDeadLetterStore deadLetterStore, DeadLetterMetrics? metrics) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddLogging();
    var provider = services.BuildServiceProvider();
    var options = new PerspectiveWorkerOptions();
    return new PerspectiveWorker(
      instanceProvider: new FixedInstance((Guid)TrackedGuid.New()),
      scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(options),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      completionStrategy: new InstantCompletionStrategy(NullLogger<InstantCompletionStrategy>.Instance),
      eventTypeProvider: provider.GetRequiredService<IEventTypeProvider>(),
      syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
      syncEventTracker: new SyncEventTracker(),
      logger: logger,
      snapshotStore: NullPerspectiveSnapshotStore.Instance,
      streamLocker: NullPerspectiveStreamLocker.Instance,
      streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
      streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
      processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
      workChannelWriter: new WorkChannelWriter(),
      rewindOptions: Options.Create(new PerspectiveRewindOptions()),
      perspectiveChannelWriter: new PerspectiveChannelWriter(),
      perspectiveCompletionChannel: new CapturingPerspectiveCompletionChannel(),
      failureChannel: failures,
      leaseRenewalChannel: new CapturingLeaseRenewalChannel(),
      perspectiveDrainChannel: new PerspectiveDrainChannel(),
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      deadLetterStore: deadLetterStore,
      generationProvider: new DefaultGenerationProvider(),
      perspectiveNotificationListener: new NoOpWorkNotificationListener(),
      governor: PerspectiveWorker.CreateDefaultGovernor(options),
      deadLetterMetrics: metrics);
  }
}
