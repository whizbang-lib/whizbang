// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// An inbox row whose envelope cannot be read is bounded (#1983): below the attempt bound it is failed, so its retry
/// is backed off and the next claim counts the attempt; past
/// <see cref="InboxDispatchWorkerOptions.MaxInboxAttempts"/> it is dead-lettered with the one dead-letter record.
/// </summary>
/// <remarks>
/// Before, the row was skipped and nothing was recorded against it. It stayed leased, was fetched again on every poll,
/// and once its lease lapsed was re-claimed with another attempt counted. The attempt bound is applied by the dispatch
/// worker, which an unreadable row never reaches, so the row was never failed and never dead-lettered: it was fetched,
/// logged at Error and skipped forever.
/// </remarks>
public partial class InboxDrainWorkerGapTests {
  private const int DESERIALIZE_FAILED_EVENT_ID = 5;
  private const int UNREADABLE_ROW_DEAD_LETTER_FAILED_EVENT_ID = 64;

  private sealed class UnreadableFailureChannel : IFailureChannel {
    public ConcurrentQueue<(WorkCategory Category, MessageFailure Failure)> Items { get; } = new();
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      Items.Enqueue((category, failure));
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Records each move; answers with the moved id, null (already gone) or a fault, as told.</summary>
  private sealed class UnreadableDeadLetterStore : IDeadLetterStore {
    public ConcurrentQueue<(string SourceTable, Guid SourceId, MessageFailureReason Reason, string? ErrorText)> Moves { get; } = new();
    public bool AlreadyGone { get; init; }
    public Exception? Fault { get; init; }
    public Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId, MessageFailureReason failureReason,
        string? errorText, Guid instanceId, string generation, CancellationToken ct = default) {
      Moves.Enqueue((sourceTable, sourceId, failureReason, errorText));
      if (Fault is not null) {
        return Task.FromException<Guid?>(Fault);
      }
      return Task.FromResult<Guid?>(AlreadyGone ? null : deadLetterId);
    }
  }

  private sealed record UnreadableSetup {
    public int? MaxInboxAttempts { get; init; } = 3;
    public IDeadLetterStore DeadLetterStore { get; init; } = NullDeadLetterStore.Instance;
    public DeadLetterMetrics? Metrics { get; init; }
    public DeadLetterLoggingOptions? DeadLetterLogging { get; init; }
    public int MaxPerStream { get; init; } = 100;
  }

  /// <summary>Drains one stream once, with the services the unreadable-row path resolves from the drain's scope.</summary>
  private static async Task<(TestInboxWriter Writer, UnreadableFailureChannel Failures, FakeLogger<InboxDrainWorker> Log)> _drainUnreadableAsync(
      Guid streamId, List<InboxBatchRow> rows, UnreadableSetup setup) {
    var coord = new ScriptedCoordinator();
    coord.RowsByStream[streamId] = rows;
    var failures = new UnreadableFailureChannel();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    services.AddSingleton<IFailureChannel>(failures);
    services.AddSingleton(setup.DeadLetterStore);
    services.AddSingleton<IGenerationProvider>(new DefaultGenerationProvider());
    services.AddOptions();
    services.Configure<InboxDispatchWorkerOptions>(o => o.MaxInboxAttempts = setup.MaxInboxAttempts);
    if (setup.Metrics is not null) {
      services.AddSingleton(setup.Metrics);
    }
    if (setup.DeadLetterLogging is not null) {
      services.AddSingleton(setup.DeadLetterLogging);
    }
    await using var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var drain = new RecordingDrainChannel();
    var writer = new TestInboxWriter();
    var logger = new FakeLogger<InboxDrainWorker>();
    var worker = new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(), new FakeInstance(), drain, writer, gate,
      Options.Create(new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = setup.MaxPerStream }), _jsonOpts, logger);

    var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingIdle += () => idle.TrySetResult();
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(streamId);
    await idle.Task.WaitAsync(_timeout);
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }
    return (writer, failures, logger);
  }

  private static InboxBatchRow _unreadable(Guid messageId, Guid streamId, int attempts) =>
    _malformedRow(messageId, streamId) with { Attempts = attempts, MessageType = "Contracts.OrderPlaced, Contracts" };

  private static List<FakeLogRecord> _records(FakeLogger<InboxDrainWorker> log, int eventId) =>
    [.. log.Collector.GetSnapshot().Where(r => r.Id.Id == eventId)];

  [Test]
  public async Task UnreadableRow_BelowTheAttemptBound_IsFailedForBackoff_AndLoggedAtWarningAsync() {
    var streamId = (Guid)TrackedGuid.New();
    var bad = (Guid)TrackedGuid.New();
    var good = (Guid)TrackedGuid.New();
    var store = new UnreadableDeadLetterStore();

    var (writer, failures, log) = await _drainUnreadableAsync(streamId,
      [_unreadable(bad, streamId, attempts: 3), _row(good, streamId)], new UnreadableSetup { DeadLetterStore = store });

    var (category, failure) = failures.Items.Single();
    await Assert.That(category).IsEqualTo(WorkCategory.Inbox);
    await Assert.That(failure.MessageId).IsEqualTo(bad)
      .Because("the failure is recorded against the row, so its retry is backed off and the next claim counts the attempt");
    await Assert.That(failure.Reason).IsEqualTo(MessageFailureReason.SerializationError);
    await Assert.That(failure.Error).IsNotNull().And.IsNotEmpty();
    await Assert.That(store.Moves).IsEmpty()
      .Because("three attempts with a bound of three is the last attempt allowed, not past it");
    var attempt = _records(log, DESERIALIZE_FAILED_EVENT_ID).Single();
    await Assert.That(attempt.Level).IsEqualTo(LogLevel.Warning)
      .Because("the row is retried and dead-lettered at the bound, where the dead-letter record is the Error");
    await Assert.That(attempt.Exception).IsNotNull();
    await Assert.That(_records(log, DeadLetterLog.EVENT_ID)).IsEmpty();
    await Assert.That(writer.Written.Select(w => w.MessageId)).IsEquivalentTo([good])
      .Because("the rest of the stream still drains");
  }

  [Test]
  public async Task UnreadableRow_InTheTailOfAStreamThatFilledItsPage_IsFailedTooAsync() {
    // A stream whose first fetch fills its page is drained further by the per-stream loop, which reads rows on its own.
    var streamId = (Guid)TrackedGuid.New();
    var first = (Guid)TrackedGuid.New();
    var bad = (Guid)TrackedGuid.New();

    var (writer, failures, log) = await _drainUnreadableAsync(streamId,
      [_row(first, streamId), _unreadable(bad, streamId, attempts: 1)], new UnreadableSetup { MaxPerStream = 1 });

    var (category, failure) = failures.Items.Single();
    await Assert.That(category).IsEqualTo(WorkCategory.Inbox);
    await Assert.That(failure.MessageId).IsEqualTo(bad);
    await Assert.That(_records(log, DESERIALIZE_FAILED_EVENT_ID).Single().Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(writer.Written.Select(w => w.MessageId)).IsEquivalentTo([first]);
  }

  [Test]
  public async Task UnreadableRow_PastTheAttemptBound_IsDeadLetteredWithOneRecord_AndNotFailedAgainAsync() {
    var streamId = (Guid)TrackedGuid.New();
    var bad = (Guid)TrackedGuid.New();
    var store = new UnreadableDeadLetterStore();
    using var meterFactory = new TestMeterFactory();
    var metrics = new DeadLetterMetrics(new WhizbangMetrics(meterFactory));

    var (_, failures, log) = await _drainUnreadableAsync(streamId,
      [_unreadable(bad, streamId, attempts: 4)], new UnreadableSetup { DeadLetterStore = store, Metrics = metrics });

    var move = store.Moves.Single();
    await Assert.That(move.SourceTable).IsEqualTo(DeadLetterSourceTable.INBOX);
    await Assert.That(move.SourceId).IsEqualTo(bad);
    await Assert.That(move.Reason).IsEqualTo(MessageFailureReason.MaxAttemptsExceeded)
      .Because("the bound that fired is the attempt bound, as on every other inbox dead-letter path");
    await Assert.That(move.ErrorText).IsNotNull().And.IsNotEmpty();
    await Assert.That(failures.Items).IsEmpty()
      .Because("a dead-lettered row is gone; failing it as well would report a retry that will never happen");
    var record = _records(log, DeadLetterLog.EVENT_ID).Single();
    await Assert.That(record.Level).IsEqualTo(LogLevel.Error)
      .Because("the dead-letter record is logged at the configured level, Error by default");
    await Assert.That(record.Message).Contains(bad.ToString(), StringComparison.Ordinal);
    await Assert.That(record.Exception).IsNotNull();
    await Assert.That(_records(log, DESERIALIZE_FAILED_EVENT_ID)).IsEmpty()
      .Because("the dead-letter record is the one line for the attempt that ends it");
    var meter = meterFactory.CreatedMeters.Single(m => m.Name == DeadLetterMetrics.METER_NAME);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.dead_letters.added")).IsEqualTo(1);
  }

  [Test]
  public async Task UnreadableRow_PastTheAttemptBound_LogsTheRecordAtTheConfiguredLevelAsync() {
    var streamId = (Guid)TrackedGuid.New();
    var bad = (Guid)TrackedGuid.New();

    var (_, _, log) = await _drainUnreadableAsync(streamId, [_unreadable(bad, streamId, attempts: 4)], new UnreadableSetup {
      DeadLetterStore = new UnreadableDeadLetterStore(),
      DeadLetterLogging = new DeadLetterLoggingOptions { Level = LogLevel.Warning },
    });

    await Assert.That(_records(log, DeadLetterLog.EVENT_ID).Single().Level).IsEqualTo(LogLevel.Warning);
  }

  [Test]
  public async Task UnreadableRow_PastTheAttemptBound_AlreadyGone_LogsNoRecordAndFailsNothingAsync() {
    var streamId = (Guid)TrackedGuid.New();
    var store = new UnreadableDeadLetterStore { AlreadyGone = true };

    var (_, failures, log) = await _drainUnreadableAsync(streamId,
      [_unreadable((Guid)TrackedGuid.New(), streamId, attempts: 4)], new UnreadableSetup { DeadLetterStore = store });

    await Assert.That(store.Moves.Count).IsEqualTo(1);
    await Assert.That(_records(log, DeadLetterLog.EVENT_ID)).IsEmpty()
      .Because("a row another path already moved got its record from that path");
    await Assert.That(failures.Items).IsEmpty();
  }

  [Test]
  public async Task UnreadableRow_PastTheAttemptBound_WhenTheMoveFails_IsFailedForAnotherTryAsync() {
    var streamId = (Guid)TrackedGuid.New();
    var bad = (Guid)TrackedGuid.New();
    var store = new UnreadableDeadLetterStore { Fault = new InvalidOperationException("dead-letter table unavailable") };

    var (_, failures, log) = await _drainUnreadableAsync(streamId,
      [_unreadable(bad, streamId, attempts: 4)], new UnreadableSetup { DeadLetterStore = store });

    await Assert.That(failures.Items.Single().Failure.MessageId).IsEqualTo(bad)
      .Because("the move is tried again on the row's next attempt; until then the row is backed off, not re-fetched every poll");
    var moveFailed = _records(log, UNREADABLE_ROW_DEAD_LETTER_FAILED_EVENT_ID).Single();
    await Assert.That(moveFailed.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(moveFailed.Exception?.Message).IsEqualTo("dead-letter table unavailable");
    await Assert.That(_records(log, DeadLetterLog.EVENT_ID)).IsEmpty();
  }

  [Test]
  public async Task UnreadableRow_PastTheAttemptBound_WithNoDeadLetterStore_IsFailedAsync() {
    var streamId = (Guid)TrackedGuid.New();
    var bad = (Guid)TrackedGuid.New();

    var (_, failures, log) = await _drainUnreadableAsync(streamId,
      [_unreadable(bad, streamId, attempts: 4)], new UnreadableSetup());

    await Assert.That(failures.Items.Single().Failure.MessageId).IsEqualTo(bad);
    await Assert.That(_records(log, DeadLetterLog.EVENT_ID)).IsEmpty();
  }

  [Test]
  public async Task UnreadableRow_WithNoAttemptBound_IsFailedAsync() {
    var streamId = (Guid)TrackedGuid.New();
    var bad = (Guid)TrackedGuid.New();
    var store = new UnreadableDeadLetterStore();

    var (_, failures, _) = await _drainUnreadableAsync(streamId,
      [_unreadable(bad, streamId, attempts: 50)], new UnreadableSetup { MaxInboxAttempts = null, DeadLetterStore = store });

    await Assert.That(failures.Items.Single().Failure.MessageId).IsEqualTo(bad);
    await Assert.That(store.Moves).IsEmpty()
      .Because("with the bound switched off nothing is dead-lettered, as on the dispatch path");
  }
}
