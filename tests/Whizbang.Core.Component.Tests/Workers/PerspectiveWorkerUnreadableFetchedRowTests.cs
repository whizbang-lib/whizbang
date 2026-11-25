// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// #2591: a fetched row the read returned without its event (a body the reap deleted) is failed alone, by name, through
/// the failure channel; every other row is handed on, in the order the read returned them, wherever the unreadable row
/// sits among them. The end-to-end shape (real Postgres, real claim loop, dead-lettering) is in
/// <c>tests/Whizbang.Data.EFCore.Postgres.Tests/ReapedBodyBatchIsolationTests.cs</c>.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
public class PerspectiveWorkerUnreadableFetchedRowTests {
  [Test]
  public async Task NoUnreadableRow_HandsOnTheSameListAndFailsNothingAsync() {
    var failures = new CapturingFailureChannel();
    var worker = _buildWorker(failures, NullLogger<PerspectiveWorker>.Instance);
    List<StreamEventData> rows = [_row(), _row()];

    var kept = await worker.FailRowsReadWithoutTheirEventAsync(rows, CancellationToken.None);

    await Assert.That(ReferenceEquals(kept, rows)).IsTrue().Because("nothing to take out, so nothing is copied");
    await Assert.That(failures.Items).IsEmpty();
  }

  [Test]
  [Arguments(0)]
  [Arguments(1)]
  [Arguments(2)]
  public async Task AnUnreadableRow_IsFailedAlone_ByName_AndTheRestKeepTheirOrderAsync(int position) {
    var failures = new CapturingFailureChannel();
    var logs = new ListLogger();
    var worker = _buildWorker(failures, logs);
    var reaped = _row() with {
      EventData = string.Empty,
      UnreadableReason = StreamEventData.NO_STORED_BODY,
      PerspectiveName = "ActiveSessions+Projection",
      Version = 1,
      Failures = 2,
    };
    List<StreamEventData> readable = [_row(), _row()];
    var rows = new List<StreamEventData>(readable);
    rows.Insert(position, reaped);

    var kept = await worker.FailRowsReadWithoutTheirEventAsync(rows, CancellationToken.None);

    await Assert.That(kept).IsEquivalentTo(readable, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(failures.Items.Count).IsEqualTo(1);
    var (category, failure) = failures.Items.Single();
    await Assert.That(category).IsEqualTo(WorkCategory.PerspectiveEvent);
    await Assert.That(failure.MessageId).IsEqualTo(reaped.EventWorkId);
    await Assert.That(failure.Reason).IsEqualTo(MessageFailureReason.EventBodyMissing);
    await Assert.That(failure.Error).Contains(reaped.StreamId.ToString()).And.Contains("ActiveSessions+Projection")
      .And.Contains(reaped.EventId.ToString()).And.Contains(StreamEventData.NO_STORED_BODY);
    var warning = logs.Entries.Single(e => e.EventId == 84);
    await Assert.That(warning.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(warning.Message).Contains(reaped.StreamId.ToString()).And.Contains("ActiveSessions+Projection")
      .And.Contains(StreamEventData.NO_STORED_BODY);
  }

  // ── helpers ─────────────────────────────────────────────────────────────────────────────────

  private static StreamEventData _row() => new() {
    StreamId = (Guid)TrackedGuid.New(),
    EventId = (Guid)TrackedGuid.New(),
    EventType = "TestEvent",
    EventData = "{}",
    EventWorkId = (Guid)TrackedGuid.New(),
  };

  private sealed class ListLogger : ILogger<PerspectiveWorker> {
    public List<(LogLevel Level, int EventId, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, eventId.Id, formatter(state, exception)));
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

  private static PerspectiveWorker _buildWorker(CapturingFailureChannel failures, ILogger<PerspectiveWorker> logger) {
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
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      perspectiveNotificationListener: new NoOpWorkNotificationListener(),
      governor: PerspectiveWorker.CreateDefaultGovernor(options));
  }
}
