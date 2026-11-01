// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// #1734 (WS-3 S5) and #1735 (WS-3 S6): the maintenance step that disposes of sealed streams. Each run schedules the
/// sealed streams with no disposition yet, then takes the due ones: a stream a perspective still owes events, or that an
/// active hold covers, is deferred for the retry backoff; a ready one gets its final snapshot at every perspective's
/// cursor, then the PreDestruction hook decides (a veto defers), then the disposition runs: Archive (the default) writes
/// the whole stream through the archive store, Delete purges it, Compact runs the handler and purges, CryptoShred and a
/// Compact with no handler defer and log.
/// </summary>
/// <docs>fundamentals/events/stream-end-of-life#scheduled-disposition</docs>
public class StreamDispositionStepTests {
  private static readonly DateTimeOffset _now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
  private const int BACKOFF_SECONDS = 300;
  private static readonly Guid _stream = new("0192f1a0-0000-7000-8000-0000000000c1");
  private static readonly Guid _cursorEvent = new("0192f1a0-0000-7000-8000-0000000000e1");

  private sealed class Ledger(List<string> log, params DueStreamDisposition[] due) : IStreamDispositionLedger {
    public List<(TimeSpan Grace, int Limit, DateTimeOffset Now)> Scheduled { get; } = [];
    public List<(Guid Stream, DateTimeOffset? Due)> Moved { get; } = [];
    public int? DueLimit { get; private set; }
    public IReadOnlyList<StreamCursorPosition> Cursors { get; init; } = [new("Orders", _cursorEvent)];

    public Task<int> ScheduleAsync(TimeSpan grace, int limit, DateTimeOffset now, CancellationToken cancellationToken = default) {
      Scheduled.Add((grace, limit, now));
      return Task.FromResult(1);
    }

    public Task<IReadOnlyList<DueStreamDisposition>> GetDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default) {
      DueLimit = limit;
      return Task.FromResult<IReadOnlyList<DueStreamDisposition>>(due);
    }

    public Task<IReadOnlyList<StreamCursorPosition>> GetCursorsAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(Cursors);

    public Task SetDueAsync(Guid streamId, DateTimeOffset? due, CancellationToken cancellationToken = default) {
      log.Add(due is null ? "clear" : "defer");
      Moved.Add((streamId, due));
      return Task.CompletedTask;
    }
  }

  private sealed class Store(List<string> log, string status = ArchiveStatus.ARCHIVED) : IArchiveStore {
    public List<ArchiveBatch> Written { get; } = [];

    public Task<ArchiveWriteResult> WriteAsync(ArchiveBatch batch, CancellationToken cancellationToken = default) {
      log.Add("archive");
      Written.Add(batch);
      return Task.FromResult(new ArchiveWriteResult(status, 1, 1, null, 1));
    }

    public async IAsyncEnumerable<ArchivedEvent> ReadAsync(Guid streamId, long fromVersion = 0, [EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }

    public Task<ArchiveRestoreResult> RestoreAsync(Guid streamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<long> DeleteAsync(Guid streamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  private sealed class Purger(List<string> log) : IStreamPurger {
    public List<StreamPurgeRequest> Requests { get; } = [];

    public Task<StreamPurgeReport> PurgeAsync(StreamPurgeRequest request, CancellationToken cancellationToken = default) {
      log.Add("purge");
      Requests.Add(request);
      return Task.FromResult(new StreamPurgeReport(request.PurgeId, false, []));
    }
  }

  private sealed class Hook(List<string> log, DestructionResult? decision = null, Exception? throws = null, Exception? afterThrows = null) : IDestructionHook {
    public DestructionContext? Seen { get; private set; }

    public ValueTask<DestructionResult> OnBeforeDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      Seen = context;
      log.Add("before");
      return throws is null ? ValueTask.FromResult(decision ?? DestructionResult.Proceed()) : ValueTask.FromException<DestructionResult>(throws);
    }

    public ValueTask OnAfterDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      log.Add("after");
      return afterThrows is null ? ValueTask.CompletedTask : ValueTask.FromException(afterThrows);
    }
  }

  private sealed class Compactor(List<string> log) : ICompactionHandler {
    public ValueTask CompactAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      log.Add("compact");
      return ValueTask.CompletedTask;
    }
  }

  private sealed class Runner(List<string> log, Exception? throws = null) : IPerspectiveRunner {
    public Type PerspectiveType => typeof(object);

    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) {
      log.Add($"snapshot:{perspectiveName}:{lastProcessedEventId}");
      return throws is null ? Task.CompletedTask : Task.FromException(throws);
    }
  }

  private sealed class Registry(IReadOnlyDictionary<string, IPerspectiveRunner> runners) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) =>
      runners.TryGetValue(perspectiveName, out var runner) ? runner : null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class ListLogger : ILogger<StreamDispositionStep> {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
  }

  private sealed record Harness(List<string> Log, Ledger Ledger, Store Store, Purger Purger, ListLogger Logger, IServiceProvider Services) {
    public Task RunAsync() => new StreamDispositionStep(new FakeTimeProvider(_now), Logger).RunAsync(Services, CancellationToken.None);
  }

  private static Harness _harness(StreamDispositionReadiness readiness = StreamDispositionReadiness.Ready, IDestructionHook? hook = null,
      Func<List<string>, IDestructionHook>? hookFor = null, string storeStatus = ArchiveStatus.ARCHIVED, bool withCompactor = false,
      IPerspectiveRunner? runner = null, IReadOnlyList<StreamCursorPosition>? cursors = null) {
    var log = new List<string>();
    var ledger = new Ledger(log, new DueStreamDisposition(_stream, readiness)) { Cursors = cursors ?? [new("Orders", _cursorEvent)] };
    var store = new Store(log, storeStatus);
    var purger = new Purger(log);
    var services = new ServiceCollection();
    services.AddSingleton<IStreamDispositionLedger>(ledger);
    services.AddSingleton<IArchiveStore>(store);
    services.AddSingleton<IStreamPurger>(purger);
    services.AddSingleton(Options.Create(new StreamLifecycleOptions { FinalizeGrace = TimeSpan.FromDays(2), SweepBatchSize = 25 }));
    services.AddSingleton(Options.Create(new MaintenanceWorkerOptions { DestructionRetryBackoffSeconds = BACKOFF_SECONDS }));
    services.AddSingleton<IPerspectiveRunnerRegistry>(new Registry(new Dictionary<string, IPerspectiveRunner> { ["Orders"] = runner ?? new Runner(log) }));
    var theHook = hook ?? hookFor?.Invoke(log);
    if (theHook is not null) {
      services.AddSingleton(theHook);
    }
    if (withCompactor) {
      services.AddSingleton<ICompactionHandler>(new Compactor(log));
    }
    return new Harness(log, ledger, store, purger, new ListLogger(), services.BuildServiceProvider());
  }

  [Test]
  public async Task EachRun_SchedulesWithTheGraceAndBatchSize_OnTheHostsClock_AndTakesAtMostABatchAsync() {
    var h = _harness(StreamDispositionReadiness.Done);

    await h.RunAsync();

    await Assert.That(h.Ledger.Scheduled).IsEquivalentTo([(TimeSpan.FromDays(2), 25, _now)]);
    await Assert.That(h.Ledger.DueLimit).IsEqualTo(25);
  }

  [Test]
  public async Task AReadyStream_GetsItsFinalSnapshot_ThenTheHook_ThenIsArchivedWhole_ThenItsScheduleIsClearedAsync() {
    var h = _harness(hookFor: log => new Hook(log));

    await h.RunAsync();

    await Assert.That(h.Log).IsEquivalentTo([$"snapshot:Orders:{_cursorEvent}", "before", "archive", "clear", "after"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the final snapshot is durable before anything leaves the hot store, and the default disposition is Archive");
    await Assert.That(h.Store.Written).IsEquivalentTo([ArchiveBatch.WholeStream(_stream)]);
    await Assert.That(h.Ledger.Moved).IsEquivalentTo([(_stream, (DateTimeOffset?)null)]);
  }

  [Test]
  public async Task TheHook_IsToldTheReason_TheStream_AndThatArchiveIsDeclaredAsync() {
    Hook? hook = null;
    var h = _harness(hookFor: log => hook = new Hook(log));

    await h.RunAsync();

    await Assert.That(hook!.Seen!.Reason).IsEqualTo(DestructionReason.FinalizeGraceElapsed);
    await Assert.That(hook.Seen.Granularity).IsEqualTo(DestructionGranularity.Stream);
    await Assert.That(hook.Seen.StreamId).IsEqualTo(_stream);
    await Assert.That(hook.Seen.DeclaredDefault).IsEqualTo(Disposition.Archive);
  }

  [Test]
  [Arguments(StreamDispositionReadiness.Blocked)]
  [Arguments(StreamDispositionReadiness.Held)]
  public async Task ABlockedOrHeldStream_IsDeferredForTheBackoff_AndNothingElseRunsAsync(StreamDispositionReadiness readiness) {
    var h = _harness(readiness, hookFor: log => new Hook(log));

    await h.RunAsync();

    await Assert.That(h.Log).IsEquivalentTo(["defer"]);
    await Assert.That(h.Ledger.Moved).IsEquivalentTo([(_stream, (DateTimeOffset?)_now.AddSeconds(BACKOFF_SECONDS))]);
  }

  [Test]
  public async Task AStreamThatIsDone_OnlyHasItsScheduleClearedAsync() {
    var h = _harness(StreamDispositionReadiness.Done, hookFor: log => new Hook(log));

    await h.RunAsync();

    await Assert.That(h.Log).IsEquivalentTo(["clear"]);
  }

  [Test]
  public async Task AHookThatCancels_DefersForTheBackoff_AndOneThatDefers_MovesTheDueTimeToItsInstantAsync() {
    var later = _now.AddDays(3);
    var canceled = _harness(hookFor: log => new Hook(log, DestructionResult.Canceled));
    var deferred = _harness(hookFor: log => new Hook(log, DestructionResult.Defer(later)));

    await canceled.RunAsync();
    await deferred.RunAsync();

    await Assert.That(canceled.Ledger.Moved).IsEquivalentTo([(_stream, (DateTimeOffset?)_now.AddSeconds(BACKOFF_SECONDS))])
      .Because("a veto defers: a sealed stream is meant to end, so it is offered again after the backoff");
    await Assert.That(deferred.Ledger.Moved).IsEquivalentTo([(_stream, (DateTimeOffset?)later)]);
    await Assert.That(canceled.Store.Written).IsEmpty();
    await Assert.That(deferred.Store.Written).IsEmpty();
  }

  [Test]
  public async Task AHookThatThrows_Defers_AndNothingIsDisposedAsync() {
    var h = _harness(hookFor: log => new Hook(log, throws: new InvalidOperationException("hook down")));

    await h.RunAsync();

    await Assert.That(h.Store.Written).IsEmpty();
    await Assert.That(h.Ledger.Moved).IsEquivalentTo([(_stream, (DateTimeOffset?)_now.AddSeconds(BACKOFF_SECONDS))]);
    await Assert.That(h.Logger.Entries.Exists(e => e.Level == LogLevel.Warning && e.Message.Contains(_stream.ToString(), StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task APostDestructionHookThatThrows_IsLogged_AndTheDispositionStaysDoneAsync() {
    var h = _harness(hookFor: log => new Hook(log, afterThrows: new InvalidOperationException("notify down")));

    await h.RunAsync();

    await Assert.That(h.Ledger.Moved).IsEquivalentTo([(_stream, (DateTimeOffset?)null)])
      .Because("the archive committed; a failed notification cannot undo it, so the schedule is still cleared");
    await Assert.That(h.Logger.Entries.Exists(e => e.Level == LogLevel.Warning && e.Message.Contains("PostDestruction", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task Cancellation_Propagates_AndDefersNothingAsync() {
    var h = _harness(hookFor: log => new Hook(log, throws: new OperationCanceledException()));
    var afterArchive = _harness(hookFor: log => new Hook(log, afterThrows: new OperationCanceledException()));

    await Assert.That(h.RunAsync).Throws<OperationCanceledException>();
    await Assert.That(afterArchive.RunAsync).Throws<OperationCanceledException>();
    await Assert.That(h.Ledger.Moved).IsEmpty().Because("a shutdown is not a failure to defer");
  }

  [Test]
  public async Task AFinalSnapshotThatFails_Defers_AndNothingIsDisposedAsync() {
    var h = _harness(runner: new Runner([], new InvalidOperationException("snapshot store down")), hookFor: log => new Hook(log));

    await h.RunAsync();

    await Assert.That(h.Log).DoesNotContain("before");
    await Assert.That(h.Store.Written).IsEmpty();
    await Assert.That(h.Ledger.Moved).IsEquivalentTo([(_stream, (DateTimeOffset?)_now.AddSeconds(BACKOFF_SECONDS))]);
  }

  [Test]
  public async Task APerspectiveThisHostCannotRun_OrACursorAtNoEvent_IsNotSnapshotted_AndTheStreamStillGoesAsync() {
    var h = _harness(cursors: [new("Elsewhere", _cursorEvent), new("Orders", null), new("Orders", _cursorEvent)]);

    await h.RunAsync();

    await Assert.That(h.Log).IsEquivalentTo([$"snapshot:Orders:{_cursorEvent}", "archive", "clear"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("with no snapshot at a parked cursor a later NewGeneration append falls back to AutoRestore, so nothing is lost");
  }

  [Test]
  public async Task NoRunnerRegistry_TakesNoSnapshot_AndNoHook_ProceedsWithTheArchiveAsync() {
    var log = new List<string>();
    var ledger = new Ledger(log, new DueStreamDisposition(_stream, StreamDispositionReadiness.Ready));
    var store = new Store(log);
    var services = new ServiceCollection();
    services.AddSingleton<IStreamDispositionLedger>(ledger);
    services.AddSingleton<IArchiveStore>(store);
    services.AddSingleton(Options.Create(new StreamLifecycleOptions()));
    services.AddSingleton(Options.Create(new MaintenanceWorkerOptions()));

    await new StreamDispositionStep(new FakeTimeProvider(_now), new ListLogger()).RunAsync(services.BuildServiceProvider(), CancellationToken.None);

    await Assert.That(log).IsEquivalentTo(["archive", "clear"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(ledger.Scheduled[0].Grace).IsEqualTo(TimeSpan.FromDays(7)).Because("the default FinalizeGrace is seven days");
    await Assert.That(ledger.Scheduled[0].Limit).IsEqualTo(500).Because("the default SweepBatchSize is 500");
  }

  [Test]
  public async Task AnArchiveTheStoreRefuses_Defers_AndLogsItsStatusAsync() {
    var h = _harness(storeStatus: ArchiveStatus.BLOCKED, hookFor: log => new Hook(log));

    await h.RunAsync();

    await Assert.That(h.Log).DoesNotContain("after").Because("nothing was destroyed, so nothing is announced");
    await Assert.That(h.Ledger.Moved).IsEquivalentTo([(_stream, (DateTimeOffset?)_now.AddSeconds(BACKOFF_SECONDS))]);
    await Assert.That(h.Logger.Entries.Exists(e => e.Message.Contains(ArchiveStatus.BLOCKED, StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task Delete_PurgesTheStream_SayingWhoAndWhyAsync() {
    var h = _harness(hookFor: log => new Hook(log, DestructionResult.Proceed(Disposition.Delete)));

    await h.RunAsync();

    await Assert.That(h.Store.Written).IsEmpty();
    await Assert.That(h.Purger.Requests).Count().IsEqualTo(1);
    await Assert.That(h.Purger.Requests[0].StreamIds).IsEquivalentTo([_stream]);
    await Assert.That(h.Purger.Requests[0].RequestedBy).IsEqualTo(StreamDispositionStep.STEP_NAME);
    await Assert.That(h.Log).Contains("after");
  }

  [Test]
  public async Task Compact_RunsTheHandler_ThenPurges_AndWithNoHandler_DefersAndLogsAsync() {
    var withHandler = _harness(withCompactor: true, hookFor: log => new Hook(log, DestructionResult.Proceed(Disposition.Compact)));
    var without = _harness(hookFor: log => new Hook(log, DestructionResult.Proceed(Disposition.Compact)));

    await withHandler.RunAsync();
    await without.RunAsync();

    await Assert.That(withHandler.Log).IsEquivalentTo([$"snapshot:Orders:{_cursorEvent}", "before", "compact", "purge", "clear", "after"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(without.Purger.Requests).IsEmpty();
    await Assert.That(without.Ledger.Moved).IsEquivalentTo([(_stream, (DateTimeOffset?)_now.AddSeconds(BACKOFF_SECONDS))]);
    await Assert.That(without.Logger.Entries.Exists(e => e.Level == LogLevel.Warning && e.Message.Contains(nameof(ICompactionHandler), StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task CryptoShred_Defers_AndLogsAsync() {
    var h = _harness(hookFor: log => new Hook(log, DestructionResult.Proceed(Disposition.CryptoShred)));

    await h.RunAsync();

    await Assert.That(h.Store.Written).IsEmpty();
    await Assert.That(h.Purger.Requests).IsEmpty();
    await Assert.That(h.Ledger.Moved).IsEquivalentTo([(_stream, (DateTimeOffset?)_now.AddSeconds(BACKOFF_SECONDS))]);
    await Assert.That(h.Logger.Entries.Exists(e => e.Level == LogLevel.Warning && e.Message.Contains("CryptoShred", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task FromServices_UsesTheHostsClockAndLogger_OrTheSystemClockAndNoLogAsync() {
    var ledger = new Ledger([]);
    var hosted = new ServiceCollection()
      .AddSingleton<TimeProvider>(new FakeTimeProvider(_now))
      .AddLogging()
      .AddSingleton<IStreamDispositionLedger>(ledger)
      .AddSingleton(Options.Create(new StreamLifecycleOptions()))
      .AddSingleton(Options.Create(new MaintenanceWorkerOptions()))
      .BuildServiceProvider();
    var bare = new ServiceCollection().BuildServiceProvider();

    var fromHost = StreamDispositionStep.FromServices(hosted);
    var fromNothing = StreamDispositionStep.FromServices(bare);
    await fromHost.RunAsync(hosted, CancellationToken.None);

    await Assert.That(ledger.Scheduled[0].Now).IsEqualTo(_now).Because("the host's registered clock decides the schedule");
    await Assert.That(fromHost.Name).IsEqualTo("stream-disposition");
    await Assert.That(fromNothing.Name).IsEqualTo("stream-disposition");
    await Assert.That(() => StreamDispositionStep.FromServices(null!)).Throws<ArgumentNullException>();
  }
}
