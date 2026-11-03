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

    // #1740: the end-of-life policies.
    public IReadOnlyList<StreamPolicyCandidate> Candidates { get; init; } = [];
    public List<(IReadOnlyList<StreamEndOfLifePolicy> Policies, DateTimeOffset Now, int Limit)> CandidateQueries { get; } = [];
    public StreamDispositionReadiness CandidateReadiness { get; init; } = StreamDispositionReadiness.Ready;
    public List<IReadOnlyList<Guid>> ReadinessQueries { get; } = [];
    public bool SealSucceeds { get; init; } = true;
    public List<(Guid Stream, long LastVersion, string Reason, DateTimeOffset Now)> Seals { get; } = [];
    public List<(Guid Stream, long Through)> Floors { get; } = [];

    public Task<IReadOnlyList<StreamPolicyCandidate>> GetPolicyCandidatesAsync(
        IReadOnlyList<StreamEndOfLifePolicy> policies, DateTimeOffset now, int limit, CancellationToken cancellationToken = default) {
      CandidateQueries.Add((policies, now, limit));
      return Task.FromResult(Candidates);
    }

    public Task<IReadOnlyDictionary<Guid, StreamDispositionReadiness>> GetReadinessAsync(IReadOnlyList<Guid> streamIds, CancellationToken cancellationToken = default) {
      ReadinessQueries.Add(streamIds);
      return Task.FromResult<IReadOnlyDictionary<Guid, StreamDispositionReadiness>>(streamIds.ToDictionary(s => s, _ => CandidateReadiness));
    }

    public Task<bool> SealIfUnchangedAsync(Guid streamId, long lastVersion, string reason, DateTimeOffset now, CancellationToken cancellationToken = default) {
      log.Add("seal");
      Seals.Add((streamId, lastVersion, reason, now));
      return Task.FromResult(SealSucceeds);
    }

    public Task SetTruncatedThroughAsync(Guid streamId, long throughVersion, CancellationToken cancellationToken = default) {
      log.Add("floor");
      Floors.Add((streamId, throughVersion));
      return Task.CompletedTask;
    }
  }

  private sealed class Truncator(List<string> log, string status = "truncated") : IStreamPrefixTruncator {
    public List<(Guid Stream, long Through, Disposition Disposition, DestructionReason Reason)> Calls { get; } = [];

    public Task<StreamTruncateResult> TruncateAsync(Guid streamId, long throughVersion, Disposition disposition = Disposition.Delete, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException("a policy names its reason");

    public Task<StreamTruncateResult> TruncateAsync(Guid streamId, long throughVersion, Disposition disposition, DestructionReason reason, CancellationToken cancellationToken = default) {
      log.Add("truncate");
      Calls.Add((streamId, throughVersion, disposition, reason));
      return Task.FromResult(new StreamTruncateResult(status, 3));
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

  private static Harness _harness(StreamDispositionReadiness readiness = StreamDispositionReadiness.Ready, StreamLifecycleOptions? lifecycle = null,
      DueStreamDisposition[]? due = null, IReadOnlyList<StreamPolicyCandidate>? candidates = null, IDestructionHook? hook = null,
      Func<List<string>, IDestructionHook>? hookFor = null, string storeStatus = ArchiveStatus.ARCHIVED, bool withCompactor = false,
      IPerspectiveRunner? runner = null, IReadOnlyList<StreamCursorPosition>? cursors = null,
      StreamDispositionReadiness candidateReadiness = StreamDispositionReadiness.Ready, bool sealSucceeds = true,
      Func<List<string>, IStreamPrefixTruncator>? truncatorFor = null, IBookClosingHandler? closingHandler = null, IBookCloser? closer = null) {
    var log = new List<string>();
    var ledger = new Ledger(log, due ?? [new DueStreamDisposition(_stream, readiness)]) {
      Cursors = cursors ?? [new("Orders", _cursorEvent)],
      Candidates = candidates ?? [],
      CandidateReadiness = candidateReadiness,
      SealSucceeds = sealSucceeds,
    };
    var store = new Store(log, storeStatus);
    var purger = new Purger(log);
    var services = new ServiceCollection();
    services.AddSingleton<IStreamDispositionLedger>(ledger);
    services.AddSingleton<IArchiveStore>(store);
    services.AddSingleton<IStreamPurger>(purger);
    services.AddSingleton(Options.Create(lifecycle ?? new StreamLifecycleOptions { FinalizeGrace = TimeSpan.FromDays(2), SweepBatchSize = 25 }));
    services.AddSingleton(Options.Create(new MaintenanceWorkerOptions { DestructionRetryBackoffSeconds = BACKOFF_SECONDS }));
    services.AddSingleton<IPerspectiveRunnerRegistry>(new Registry(new Dictionary<string, IPerspectiveRunner> { ["Orders"] = runner ?? new Runner(log) }));
    var theHook = hook ?? hookFor?.Invoke(log);
    if (theHook is not null) {
      services.AddSingleton(theHook);
    }
    if (withCompactor) {
      services.AddSingleton<ICompactionHandler>(new Compactor(log));
    }
    if (truncatorFor is not null) {
      services.AddSingleton(truncatorFor(log));
    }
    if (closingHandler is not null) {
      services.AddSingleton(closingHandler);
    }
    if (closer is not null) {
      services.AddSingleton(closer);
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
    var h = _harness(hookFor: log => new Hook(log), runner: new Runner([], new InvalidOperationException("snapshot store down")));

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
    var h = _harness(hookFor: log => new Hook(log), storeStatus: ArchiveStatus.BLOCKED);

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
    var withHandler = _harness(hookFor: log => new Hook(log, DestructionResult.Proceed(Disposition.Compact)), withCompactor: true);
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
  // ===== #1740 (WS-3 S11): the end-of-life policies =====

  private const string POLICY_TYPE = "Shop.Order";

  private static StreamLifecycleOptions _policies(StreamLifecycleAction action = StreamLifecycleAction.Archive, bool enabled = true) {
    var options = new StreamLifecycleOptions { Enabled = enabled, SweepBatchSize = 25 };
    options.Types[POLICY_TYPE] = new StreamTypeLifecycleOptions { IdleTtl = TimeSpan.FromDays(30), Action = action };
    return options;
  }

  private static StreamPolicyCandidate _candidate(StreamEndOfLifeTrigger trigger = StreamEndOfLifeTrigger.IdleTtl, long? through = null) =>
    new(_stream, POLICY_TYPE, trigger, 12, through);

  [Test]
  public async Task Policies_AreNotEvaluated_UntilEnabled_OrWhenNoTypeSetsATrigger_AndAreAskedWithTheClockAndBatchOnceEnabledAsync() {
    var off = _harness(lifecycle: _policies(enabled: false), due: [], candidates: [_candidate()]);
    var noTrigger = new StreamLifecycleOptions { Enabled = true };
    noTrigger.Types[POLICY_TYPE] = new StreamTypeLifecycleOptions { Action = StreamLifecycleAction.Purge };
    var none = _harness(lifecycle: noTrigger, due: []);
    var on = _harness(lifecycle: _policies(), due: []);

    await off.RunAsync();
    await none.RunAsync();
    await on.RunAsync();

    await Assert.That(off.Ledger.CandidateQueries).IsEmpty()
      .Because("Enabled is false by default: a policy acts on streams nobody sealed, so it runs only once a host turns it on");
    await Assert.That(off.Store.Written).IsEmpty();
    await Assert.That(none.Ledger.CandidateQueries).IsEmpty().Because("with no trigger there is nothing to look for, so the ledger is not read");
    await Assert.That(on.Ledger.CandidateQueries).Count().IsEqualTo(1);
    await Assert.That(on.Ledger.CandidateQueries[0].Now).IsEqualTo(_now);
    await Assert.That(on.Ledger.CandidateQueries[0].Limit).IsEqualTo(25).Because("SweepBatchSize bounds the streams a cycle acts on");
    await Assert.That(on.Ledger.CandidateQueries[0].Policies).IsEquivalentTo(
      [new StreamEndOfLifePolicy(POLICY_TYPE, TimeSpan.FromDays(30), null, null, null, StreamLifecycleAction.Archive)]);
  }

  [Test]
  [Arguments(StreamEndOfLifeTrigger.IdleTtl, DestructionReason.IdleTtlElapsed)]
  [Arguments(StreamEndOfLifeTrigger.MaxAge, DestructionReason.MaxAgeReached)]
  [Arguments(StreamEndOfLifeTrigger.TenantByteCap, DestructionReason.TenantByteCapExceeded)]
  public async Task APickedStream_GetsItsFinalSnapshot_ThenTheHookWithItsTriggersReason_ThenIsArchivedWhole_AndIsNotSealedAsync(
      StreamEndOfLifeTrigger trigger, DestructionReason reason) {
    Hook? hook = null;
    var h = _harness(lifecycle: _policies(), due: [], candidates: [_candidate(trigger)], hookFor: log => hook = new Hook(log));

    await h.RunAsync();

    await Assert.That(h.Log).IsEquivalentTo([$"snapshot:Orders:{_cursorEvent}", "before", "archive", "clear", "after"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("an archive is reversible and its stream may be appended to again under its AppendToArchived policy, so it is not sealed");
    await Assert.That(hook!.Seen!.Reason).IsEqualTo(reason);
    await Assert.That(hook.Seen.DeclaredDefault).IsEqualTo(Disposition.Archive);
    await Assert.That(hook.Seen.StreamId).IsEqualTo(_stream);
    await Assert.That(h.Store.Written).IsEquivalentTo([ArchiveBatch.WholeStream(_stream)]);
    await Assert.That(h.Ledger.ReadinessQueries).Count().IsEqualTo(1);
    await Assert.That(h.Ledger.ReadinessQueries[0]).IsEquivalentTo([_stream]);
  }

  [Test]
  [Arguments(StreamEndOfLifeTrigger.IdleTtl, "IdleTtl")]
  [Arguments(StreamEndOfLifeTrigger.MaxAge, "MaxAge")]
  [Arguments(StreamEndOfLifeTrigger.TenantByteCap, "TenantByteCap")]
  public async Task Purge_TheHookFirst_ThenTheStreamIsSealedIfUnchanged_ThenPurged_SayingWhichTriggerAsync(StreamEndOfLifeTrigger trigger, string named) {
    var h = _harness(lifecycle: _policies(StreamLifecycleAction.Purge), due: [], candidates: [_candidate(trigger)], hookFor: log => new Hook(log));

    await h.RunAsync();

    await Assert.That(h.Log).IsEquivalentTo([$"snapshot:Orders:{_cursorEvent}", "before", "seal", "purge", "clear", "after"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the seal at the version the policy saw closes the stream to appends, so an append between the pick and the purge is never lost");
    await Assert.That(h.Ledger.Seals).Count().IsEqualTo(1);
    await Assert.That(h.Ledger.Seals[0].Stream).IsEqualTo(_stream);
    await Assert.That(h.Ledger.Seals[0].LastVersion).IsEqualTo(12L);
    await Assert.That(h.Ledger.Seals[0].Now).IsEqualTo(_now);
    await Assert.That(h.Ledger.Seals[0].Reason).Contains(named);
    await Assert.That(h.Purger.Requests[0].StreamIds).IsEquivalentTo([_stream]);
    await Assert.That(h.Purger.Requests[0].RequestedBy).IsEqualTo(StreamDispositionStep.STEP_NAME);
    await Assert.That(h.Purger.Requests[0].Reason).Contains(named);
  }

  [Test]
  public async Task Purge_OfAStreamAppendedToSinceItWasPicked_IsLeftAlone_AndNothingIsPurgedAsync() {
    var h = _harness(lifecycle: _policies(StreamLifecycleAction.Purge), due: [], candidates: [_candidate()], hookFor: log => new Hook(log),
      sealSucceeds: false);

    await h.RunAsync();

    await Assert.That(h.Purger.Requests).IsEmpty().Because("the stream came back to life: purging it would lose the new event");
    await Assert.That(h.Log).DoesNotContain("after");
    await Assert.That(h.Ledger.Moved).IsEmpty().Because("nothing is deferred: the next cycle decides afresh whether a trigger still picks it");
  }

  [Test]
  public async Task Compact_RunsTheHandlerAfterTheSeal_ThenPurges_AndWithNoHandler_DefersWithoutSealingAsync() {
    var withHandler = _harness(lifecycle: _policies(StreamLifecycleAction.Compact), due: [], candidates: [_candidate()],
      hookFor: log => new Hook(log), withCompactor: true);
    var without = _harness(lifecycle: _policies(StreamLifecycleAction.Compact), due: [], candidates: [_candidate()], hookFor: log => new Hook(log));

    await withHandler.RunAsync();
    await without.RunAsync();

    await Assert.That(withHandler.Log).IsEquivalentTo([$"snapshot:Orders:{_cursorEvent}", "before", "seal", "compact", "purge", "clear", "after"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(without.Log).DoesNotContain("seal")
      .Because("a stream that cannot be compacted is kept open: sealing it would refuse its appends for nothing");
    await Assert.That(without.Purger.Requests).IsEmpty();
    await Assert.That(without.Ledger.Moved).IsEquivalentTo([(_stream, (DateTimeOffset?)_now.AddSeconds(BACKOFF_SECONDS))]);
  }

  [Test]
  [Arguments(StreamDispositionReadiness.Blocked)]
  [Arguments(StreamDispositionReadiness.Held)]
  public async Task APickedStreamThatIsBlockedOrHeld_IsDeferredForTheBackoff_AndNothingElseRunsAsync(StreamDispositionReadiness readiness) {
    var h = _harness(lifecycle: _policies(StreamLifecycleAction.Purge), due: [], candidates: [_candidate()], hookFor: log => new Hook(log),
      candidateReadiness: readiness);

    await h.RunAsync();

    await Assert.That(h.Log).IsEquivalentTo(["defer"])
      .Because("a deferred stream has a due time, which keeps it out of the next cycles' picks until the backoff ends");
    await Assert.That(h.Ledger.Moved).IsEquivalentTo([(_stream, (DateTimeOffset?)_now.AddSeconds(BACKOFF_SECONDS))]);
  }

  [Test]
  public async Task AHookThatCancels_DefersAPickedStreamForTheBackoff_AndOneThatDefers_UntilItsInstant_NeitherSealsAsync() {
    var later = _now.AddDays(3);
    var canceled = _harness(lifecycle: _policies(StreamLifecycleAction.Purge), due: [], candidates: [_candidate()],
      hookFor: log => new Hook(log, DestructionResult.Canceled));
    var deferred = _harness(lifecycle: _policies(StreamLifecycleAction.Purge), due: [], candidates: [_candidate()],
      hookFor: log => new Hook(log, DestructionResult.Defer(later)));

    await canceled.RunAsync();
    await deferred.RunAsync();

    await Assert.That(canceled.Ledger.Moved).IsEquivalentTo([(_stream, (DateTimeOffset?)_now.AddSeconds(BACKOFF_SECONDS))]);
    await Assert.That(deferred.Ledger.Moved).IsEquivalentTo([(_stream, (DateTimeOffset?)later)]);
    await Assert.That(canceled.Ledger.Seals).IsEmpty().Because("a veto leaves the stream open: the hook is asked before the seal");
    await Assert.That(deferred.Ledger.Seals).IsEmpty();
    await Assert.That(canceled.Purger.Requests).IsEmpty();
  }

  [Test]
  [Arguments(StreamLifecycleAction.Archive, Disposition.Archive)]
  [Arguments(StreamLifecycleAction.Purge, Disposition.Delete)]
  [Arguments(StreamLifecycleAction.Compact, Disposition.Compact)]
  public async Task TheVersionCap_TruncatesThePrefixThroughTheTruncator_WithItsReason_AndRecordsTheCutAsync(
      StreamLifecycleAction action, Disposition disposition) {
    Truncator? truncator = null;
    var h = _harness(lifecycle: _policies(action), due: [], candidates: [_candidate(StreamEndOfLifeTrigger.VersionCap, through: 9)],
      truncatorFor: log => truncator = new Truncator(log));

    await h.RunAsync();

    await Assert.That(truncator!.Calls).IsEquivalentTo([(_stream, 9L, disposition, DestructionReason.VersionCapExceeded)])
      .Because("the truncator owns the prefix's gates, the hook and the compaction; the hook is told the version cap is why");
    await Assert.That(h.Log).IsEquivalentTo(["truncate", "floor"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(h.Ledger.Floors).IsEquivalentTo([(_stream, 9L)]);
    await Assert.That(h.Ledger.ReadinessQueries).IsEmpty().Because("the prefix truncate applies its own consumption gate and holds");
  }

  [Test]
  public async Task TheVersionCap_ATruncateThatDidNotHappen_DefersAndLogsItsStatus_AndNoTruncator_DefersAndLogsAsync() {
    var refused = _harness(lifecycle: _policies(), due: [], candidates: [_candidate(StreamEndOfLifeTrigger.VersionCap, through: 9)],
      truncatorFor: log => new Truncator(log, "blocked"));
    var missing = _harness(lifecycle: _policies(), due: [], candidates: [_candidate(StreamEndOfLifeTrigger.VersionCap, through: 9)]);

    await refused.RunAsync();
    await missing.RunAsync();

    await Assert.That(refused.Ledger.Floors).IsEmpty().Because("only a prefix that left the hot store moves the recorded cut");
    await Assert.That(refused.Ledger.Moved).IsEquivalentTo([(_stream, (DateTimeOffset?)_now.AddSeconds(BACKOFF_SECONDS))]);
    await Assert.That(refused.Logger.Entries.Exists(e => e.Message.Contains("blocked", StringComparison.Ordinal))).IsTrue();
    await Assert.That(missing.Ledger.Moved).IsEquivalentTo([(_stream, (DateTimeOffset?)_now.AddSeconds(BACKOFF_SECONDS))]);
    await Assert.That(missing.Logger.Entries.Exists(e => e.Level == LogLevel.Warning && e.Message.Contains(nameof(IStreamPrefixTruncator), StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task APickedStreamThatFails_IsLoggedAndDeferred_AndTheNextPickStillRunsAsync() {
    var second = new Guid("0192f1a0-0000-7000-8000-0000000000c2");
    var h = _harness(lifecycle: _policies(), due: [],
      candidates: [_candidate(), new StreamPolicyCandidate(second, POLICY_TYPE, StreamEndOfLifeTrigger.VersionCap, 20, 10)],
      hookFor: log => new Hook(log), runner: new Runner([], new InvalidOperationException("snapshot store down")),
      truncatorFor: log => new Truncator(log));

    await h.RunAsync();

    await Assert.That(h.Ledger.Moved).IsEquivalentTo([(_stream, (DateTimeOffset?)_now.AddSeconds(BACKOFF_SECONDS))]);
    await Assert.That(h.Ledger.Floors).IsEquivalentTo([(second, 10L)]).Because("one broken stream never holds back the sweep");
    await Assert.That(h.Logger.Entries.Exists(e => e.Level == LogLevel.Warning && e.Message.Contains(_stream.ToString(), StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task ASealedStreamsDisposition_IsItsTypesAction_AndArchiveForATypeWithNoneAsync() {
    Hook? typed = null;
    Hook? untyped = null;
    var lifecycle = _policies(StreamLifecycleAction.Purge, enabled: false);
    var purged = _harness(lifecycle: lifecycle, due: [new DueStreamDisposition(_stream, StreamDispositionReadiness.Ready, POLICY_TYPE)],
      hookFor: log => typed = new Hook(log));
    var archived = _harness(lifecycle: lifecycle, due: [new DueStreamDisposition(_stream, StreamDispositionReadiness.Ready, "Shop.Cart")],
      hookFor: log => untyped = new Hook(log));

    await purged.RunAsync();
    await archived.RunAsync();

    await Assert.That(typed!.Seen!.DeclaredDefault).IsEqualTo(Disposition.Delete)
      .Because("finalized plus grace is one of the type's triggers, so it runs the type's action, with or without Enabled");
    await Assert.That(purged.Purger.Requests).Count().IsEqualTo(1);
    await Assert.That(untyped!.Seen!.DeclaredDefault).IsEqualTo(Disposition.Archive);
    await Assert.That(archived.Store.Written).Count().IsEqualTo(1);
  }

  // ===== #1741 (WS-3 S12): closing the books by policy =====

  public sealed record ProbeClosed([property: StreamId] Guid LedgerId = default) : IEvent;

  public sealed record ProbeOpened : OpeningBalance;

  private sealed class ClosingHandler(bool prepares = true) : IBookClosingHandler {
    public List<BookClosingContext> Asked { get; } = [];

    public ValueTask<BooksToClose?> PrepareAsync(BookClosingContext context, CancellationToken cancellationToken = default) {
      Asked.Add(context);
      return ValueTask.FromResult(prepares ? BooksToClose.Of(new ProbeClosed(), new ProbeOpened()) : null);
    }
  }

  private sealed class Closer(BookCloseStatus status = BookCloseStatus.Closed) : IBookCloser {
    public List<(Guid Stream, Type Closing, Type Opening)> Calls { get; } = [];

    public Task<BookCloseResult> CloseBooksAsync<TClosing, TOpening>(
        Guid streamId, TClosing closingSummary, TOpening openingBalance, CancellationToken cancellationToken = default)
        where TClosing : IEvent
        where TOpening : OpeningBalance {
      Calls.Add((streamId, typeof(TClosing), typeof(TOpening)));
      return Task.FromResult(new BookCloseResult(status, status == BookCloseStatus.Closed ? Guid.Empty : null));
    }
  }

  [Test]
  [Arguments(StreamEndOfLifeTrigger.IdleTtl, DestructionReason.IdleTtlElapsed)]
  [Arguments(StreamEndOfLifeTrigger.VersionCap, DestructionReason.VersionCapExceeded)]
  public async Task CloseBooks_AsksTheHandlerForTheEvents_ThenTheCloserClosesTheBooks_WithNoHookNoGateAndNoTruncateAsync(
      StreamEndOfLifeTrigger trigger, DestructionReason reason) {
    var handler = new ClosingHandler();
    var closer = new Closer();
    Truncator? truncator = null;
    var h = _harness(lifecycle: _policies(StreamLifecycleAction.CloseBooks), due: [],
      candidates: [_candidate(trigger, through: trigger == StreamEndOfLifeTrigger.VersionCap ? 9 : null)],
      hookFor: log => new Hook(log), truncatorFor: log => truncator = new Truncator(log), closingHandler: handler, closer: closer);

    await h.RunAsync();

    await Assert.That(handler.Asked).IsEquivalentTo([new BookClosingContext(_stream, POLICY_TYPE, reason, 12)])
      .Because("the handler is told the stream, its type, which trigger picked it and the version it was picked at");
    await Assert.That(closer.Calls).IsEquivalentTo([(_stream, typeof(ProbeClosed), typeof(ProbeOpened))]);
    await Assert.That(h.Log).DoesNotContain("before")
      .Because("closing the books destroys nothing: the predecessor is sealed whole, and its own disposition runs the gates and the hook later");
    await Assert.That(truncator!.Calls).IsEmpty().Because("a version cap whose action closes the books closes them instead of cutting the prefix");
    await Assert.That(h.Ledger.ReadinessQueries.SelectMany(q => q)).DoesNotContain(_stream);
    await Assert.That(h.Ledger.Moved).IsEmpty();
  }

  [Test]
  public async Task CloseBooks_AHandlerThatReturnsNothing_DefersTheStream_AndNothingIsClosedAsync() {
    var closer = new Closer();
    var h = _harness(lifecycle: _policies(StreamLifecycleAction.CloseBooks), due: [], candidates: [_candidate()],
      closingHandler: new ClosingHandler(prepares: false), closer: closer);

    await h.RunAsync();

    await Assert.That(closer.Calls).IsEmpty();
    await Assert.That(h.Ledger.Moved).IsEquivalentTo([(_stream, (DateTimeOffset?)_now.AddSeconds(BACKOFF_SECONDS))])
      .Because("a handler that is not ready leaves the stream open, offered again after the backoff");
  }

  [Test]
  public async Task CloseBooks_NoHandlerOrNoCloser_DefersAndLogs_AndARefusedClose_IsLoggedAndLeftForTheNextCycleAsync() {
    var noHandler = _harness(lifecycle: _policies(StreamLifecycleAction.CloseBooks), due: [], candidates: [_candidate()], closer: new Closer());
    var noCloser = _harness(lifecycle: _policies(StreamLifecycleAction.CloseBooks), due: [], candidates: [_candidate()],
      closingHandler: new ClosingHandler());
    var changed = _harness(lifecycle: _policies(StreamLifecycleAction.CloseBooks), due: [], candidates: [_candidate()],
      closingHandler: new ClosingHandler(), closer: new Closer(BookCloseStatus.StreamChanged));

    await noHandler.RunAsync();
    await noCloser.RunAsync();
    await changed.RunAsync();

    await Assert.That(noHandler.Ledger.Moved).IsEquivalentTo([(_stream, (DateTimeOffset?)_now.AddSeconds(BACKOFF_SECONDS))]);
    await Assert.That(noHandler.Logger.Entries.Exists(e => e.Level == LogLevel.Warning && e.Message.Contains(nameof(IBookClosingHandler), StringComparison.Ordinal))).IsTrue();
    await Assert.That(noCloser.Ledger.Moved).IsEquivalentTo([(_stream, (DateTimeOffset?)_now.AddSeconds(BACKOFF_SECONDS))]);
    await Assert.That(noCloser.Logger.Entries.Exists(e => e.Level == LogLevel.Warning && e.Message.Contains(nameof(IBookCloser), StringComparison.Ordinal))).IsTrue();
    await Assert.That(changed.Ledger.Moved).IsEmpty()
      .Because("a stream appended to while its books were closing is picked again only if a trigger still holds next cycle");
    await Assert.That(changed.Logger.Entries.Exists(e => e.Message.Contains(nameof(BookCloseStatus.StreamChanged), StringComparison.Ordinal))).IsTrue();
  }
}
