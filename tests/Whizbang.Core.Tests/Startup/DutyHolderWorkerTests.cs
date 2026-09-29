using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Tests.Observability;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The acquisition hook (#966, requirement 7): the holder loop keeps or wins each role that has
/// duty work, and runs the owed work when it holds it. Its passes renew the lease, so a stuck pass
/// is a lapsed role; completion is fenced, so a holder that lost the role cannot mark work done.
/// Every test drives the pass directly or through deterministic wake signals: nothing sleeps.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/DutyHolderWorker.cs</code-under-test>
[Category("Startup")]
public class DutyHolderWorkerTests {
  private const string ROLE = StartupDuties.MAINTAINER;

  private sealed class Grant(long epoch = 1) : IDutyGrant {
    public Queue<bool> Verifications { get; } = new();
    public bool Disposed { get; private set; }
    public string Duty => ROLE;
    public DateTimeOffset AcquiredAt => DateTimeOffset.UnixEpoch;
    public long? Epoch => epoch;
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) =>
      Task.FromResult(!Disposed && (Verifications.Count == 0 || Verifications.Dequeue()));
    public ValueTask DisposeAsync() {
      Disposed = true;
      return ValueTask.CompletedTask;
    }
  }

  private sealed class Elector : IDutyElector {
    public Queue<Func<DutyAttempt>> Answers { get; } = new();
    public int Attempts { get; private set; }
    public TaskCompletionSource<int> Attempted { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
      Attempts++;
      var answer = Answers.Count > 0 ? Answers.Dequeue() : () => DutyAttempt.Lost(DutyRefusal.Contended, "held elsewhere");
      try {
        return Task.FromResult(answer());
      } finally {
        Attempted.TrySetResult(Attempts);
      }
    }
  }

  private sealed class Store : IPendingDutyWorkStore {
    public List<PendingDutyWork> Owed { get; } = [];
    public List<string> Owes { get; } = [];
    public List<string> Completed { get; } = [];
    public List<(string Key, string Error)> Failures { get; } = [];
    public Func<PendingDutyWork, DutyWorkCompletion> Completion { get; set; } = _ => DutyWorkCompletion.Completed;
    public bool FailureRecorded { get; set; } = true;
    public Func<Exception>? ListThrows { get; set; }
    public Func<Exception>? RecordThrows { get; set; }

    public Task OweAsync(string role, string workKey, CancellationToken cancellationToken) {
      Owes.Add(workKey);
      return Task.CompletedTask;
    }
    public Task<IReadOnlyList<PendingDutyWork>> ListOwedAsync(string role, CancellationToken cancellationToken) =>
      ListThrows is { } ex ? Task.FromException<IReadOnlyList<PendingDutyWork>>(ex()) : Task.FromResult<IReadOnlyList<PendingDutyWork>>([.. Owed]);
    public Task<DutyWorkCompletion> CompleteAsync(PendingDutyWork work, IDutyGrant grant, CancellationToken cancellationToken) {
      if (RecordThrows is { } ex) {
        return Task.FromException<DutyWorkCompletion>(ex());
      }
      Completed.Add(work.WorkKey);
      return Task.FromResult(Completion(work));
    }
    public Task<bool> RecordFailureAsync(PendingDutyWork work, IDutyGrant grant, string failure, CancellationToken cancellationToken) {
      if (RecordThrows is { } ex) {
        return Task.FromException<bool>(ex());
      }
      Failures.Add((work.WorkKey, failure));
      return Task.FromResult(FailureRecorded);
    }
  }

  private sealed class Handler(string key, Func<CancellationToken, ValueTask<DutyWorkResult>> run, string role = ROLE) : IDutyWorkHandler {
    public int Runs { get; private set; }
    public string Role => role;
    public string WorkKey => key;
    public ValueTask<DutyWorkResult> RunAsync(IDutyGrant grant, CancellationToken cancellationToken) {
      Runs++;
      return run(cancellationToken);
    }
  }

  private sealed class Notify : ISharedNotifyConnection {
    public INotifySubscription? Subscription { get; private set; }
    public bool Unsubscribed { get; private set; }
    public IDisposable Subscribe(INotifySubscription subscription) {
      Subscription = subscription;
      return new Handle(this);
    }
    private sealed class Handle(Notify owner) : IDisposable {
      public void Dispose() => owner.Unsubscribed = true;
    }
  }

  private static PendingDutyWork _owed(string key, bool due = true) =>
    new(ROLE, key, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0, null, due);

  private static Handler _done(string key) => new(key, _ => ValueTask.FromResult(DutyWorkResult.Done()));

  private static DutyHolderWorker _worker(Elector elector, Store store, IEnumerable<IDutyWorkHandler> handlers,
      ISharedNotifyConnection? notify = null, RoleAssignmentMetrics? metrics = null, TimeProvider? time = null) =>
    new(elector, store, handlers, Options.Create(new RoleAssignmentOptions()), NullLogger<DutyHolderWorker>.Instance,
      notify, metrics, time);

  [Test]
  public async Task Roles_AreTheManagedRolesThatHaveHandlers_AndTheFirstHandlerForAKeyWinsAsync() {
    var first = _done("Rewrite");
    var second = _done("Rewrite");
    var worker = _worker(new Elector(), new Store(),
      [first, second, new Handler("Migrate", _ => ValueTask.FromResult(DutyWorkResult.Done()), StartupDuties.MIGRATOR)]);

    await Assert.That(worker.Roles).IsEquivalentTo([ROLE])
      .Because("the migrator is never held by assignment, so its handler is ignored");

    var elector = new Elector();
    var grant = new Grant();
    elector.Answers.Enqueue(() => DutyAttempt.Granted(grant));
    var store = new Store();
    store.Owed.Add(_owed("Rewrite"));
    await _worker(elector, store, [first, second]).RunOnceAsync(CancellationToken.None);
    await Assert.That(first.Runs).IsEqualTo(1);
    await Assert.That(second.Runs).IsEqualTo(0);
  }

  [Test]
  public async Task Pass_WhileAnotherInstanceHolds_RunsNothingAsync() {
    var handler = _done("Rewrite");
    var store = new Store();
    store.Owed.Add(_owed("Rewrite"));
    var worker = _worker(new Elector(), store, [handler]);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(worker.Holds(ROLE)).IsFalse();
    await Assert.That(handler.Runs).IsEqualTo(0).Because("only the holder runs owed duty work");
  }

  [Test]
  public async Task BecomingTheHolder_RunsTheDueOwedWorkItHasHandlersFor_AndCompletesItAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new RoleAssignmentMetrics(new WhizbangMetrics(factory));
    var elector = new Elector();
    elector.Answers.Enqueue(() => DutyAttempt.Granted(new Grant()));
    var store = new Store();
    store.Owed.AddRange([_owed("Rewrite"), _owed("BackingOff", due: false), _owed("FromANewerVersion")]);
    var rewrite = _done("Rewrite");
    var backingOff = _done("BackingOff");
    var worker = _worker(elector, store, [rewrite, backingOff], metrics: metrics);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(worker.Holds(ROLE)).IsTrue();
    await Assert.That(store.Completed).IsEquivalentTo(["Rewrite"]);
    await Assert.That(backingOff.Runs).IsEqualTo(0).Because("a failed attempt backs off before the holder retries it");
    var runs = ProbeMeterReader.ReadSeries(factory.CreatedMeters[0], "whizbang.roles.work_runs").Single(r => r.Tags.Count > 0);
    await Assert.That(runs.Tags[RoleAssignmentMetrics.OUTCOME_TAG]).IsEqualTo(RoleAssignmentMetrics.OUTCOME_COMPLETED);
  }

  [Test]
  public async Task LaterPasses_KeepTheRoleByVerifying_WithoutVotingAgainAsync() {
    var elector = new Elector();
    elector.Answers.Enqueue(() => DutyAttempt.Granted(new Grant()));
    var worker = _worker(elector, new Store(), [_done("Rewrite")]);

    await worker.RunOnceAsync(CancellationToken.None);
    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(elector.Attempts).IsEqualTo(1).Because("the pass verifies, which renews the lease from this loop");
  }

  [Test]
  public async Task AGrantThatNoLongerVerifies_IsDropped_AndThePassVotesAgainAsync() {
    var lost = new Grant();
    lost.Verifications.Enqueue(false);
    var elector = new Elector();
    elector.Answers.Enqueue(() => DutyAttempt.Granted(lost));
    var worker = _worker(elector, new Store(), [_done("Rewrite")]);
    await worker.RunOnceAsync(CancellationToken.None);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(lost.Disposed).IsTrue();
    await Assert.That(elector.Attempts).IsEqualTo(2);
    await Assert.That(worker.Holds(ROLE)).IsFalse();
  }

  [Test]
  public async Task AVoteThatThrows_IsRetriedOnTheNextPassAsync() {
    var elector = new Elector();
    elector.Answers.Enqueue(() => throw new InvalidOperationException("database unreachable"));
    elector.Answers.Enqueue(() => DutyAttempt.Granted(new Grant()));
    var worker = _worker(elector, new Store(), [_done("Rewrite")]);

    await worker.RunOnceAsync(CancellationToken.None);
    await Assert.That(worker.Holds(ROLE)).IsFalse();
    await worker.RunOnceAsync(CancellationToken.None);
    await Assert.That(worker.Holds(ROLE)).IsTrue();
  }

  [Test]
  public async Task AListThatThrows_RunsNothing_AndKeepsTheRoleAsync() {
    var elector = new Elector();
    elector.Answers.Enqueue(() => DutyAttempt.Granted(new Grant()));
    var handler = _done("Rewrite");
    var worker = _worker(elector, new Store { ListThrows = () => new InvalidOperationException("down") }, [handler]);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(handler.Runs).IsEqualTo(0);
    await Assert.That(worker.Holds(ROLE)).IsTrue();
  }

  [Test]
  public async Task WorkThatIsNotDone_OrThrows_StaysOwed_WithItsFailureRecordedAsync() {
    var elector = new Elector();
    elector.Answers.Enqueue(() => DutyAttempt.Granted(new Grant()));
    var store = new Store();
    store.Owed.AddRange([_owed("Failing"), _owed("Throwing"), _owed("Silent"), _owed("Later")]);
    var later = _done("Later");
    var worker = _worker(elector, store, [
      new Handler("Failing", _ => ValueTask.FromResult(DutyWorkResult.NotDone("table locked"))),
      new Handler("Throwing", _ => throw new InvalidOperationException("boom")),
      new Handler("Silent", _ => ValueTask.FromResult(new DutyWorkResult(DutyWorkStatus.NotDone))),
      later]);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(store.Failures).IsEquivalentTo([
      ("Failing", "table locked"), ("Throwing", "InvalidOperationException: boom"), ("Silent", "not done")]);
    await Assert.That(later.Runs).IsEqualTo(1).Because("one piece of work failing does not hold up the rest");
  }

  [Test]
  public async Task DeferredWork_IsNeitherCompletedNorFailedAsync() {
    var elector = new Elector();
    elector.Answers.Enqueue(() => DutyAttempt.Granted(new Grant()));
    var store = new Store();
    store.Owed.Add(_owed("Rewrite"));
    var worker = _worker(elector, store, [new Handler("Rewrite", _ => ValueTask.FromResult(DutyWorkResult.Deferred("starting")))]);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(store.Completed).IsEmpty();
    await Assert.That(store.Failures).IsEmpty();
  }

  [Test]
  [Arguments(DutyWorkCompletion.OwedAgain, 2)]
  [Arguments(DutyWorkCompletion.Fenced, 1)]
  public async Task ACompletionThatIsOwedAgainContinues_AndOneThatIsFencedStopsThePassAsync(DutyWorkCompletion completion, int expectedRuns) {
    var elector = new Elector();
    elector.Answers.Enqueue(() => DutyAttempt.Granted(new Grant()));
    var store = new Store { Completion = _ => completion };
    store.Owed.AddRange([_owed("First"), _owed("Second")]);
    var first = _done("First");
    var second = _done("Second");
    var worker = _worker(elector, store, [first, second]);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(first.Runs + second.Runs).IsEqualTo(expectedRuns);
  }

  [Test]
  public async Task AFailureTheFenceRefuses_StopsThePassAsync() {
    var elector = new Elector();
    elector.Answers.Enqueue(() => DutyAttempt.Granted(new Grant()));
    var store = new Store { FailureRecorded = false };
    store.Owed.AddRange([_owed("Failing"), _owed("Later")]);
    var later = _done("Later");
    var worker = _worker(elector, store, [new Handler("Failing", _ => ValueTask.FromResult(DutyWorkResult.NotDone("x"))), later]);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(later.Runs).IsEqualTo(0).Because("a holder that lost the role stops running its work");
    await Assert.That(worker.Holds(ROLE)).IsFalse().Because("the fence's refusal is proof the role is gone");
  }

  [Test]
  public async Task AnOutcomeThatCannotBeRecorded_LeavesTheWorkOwed_AndThePassContinuesAsync() {
    var elector = new Elector();
    elector.Answers.Enqueue(() => DutyAttempt.Granted(new Grant()));
    var store = new Store { RecordThrows = () => new InvalidOperationException("down") };
    store.Owed.AddRange([_owed("First"), _owed("Second")]);
    var second = _done("Second");
    var worker = _worker(elector, store, [_done("First"), second]);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(second.Runs).IsEqualTo(1);
  }

  [Test]
  public async Task LosingTheRoleBetweenTwoPiecesOfWork_StopsBeforeTheSecondAsync() {
    var grant = new Grant();
    grant.Verifications.Enqueue(true);    // before First
    grant.Verifications.Enqueue(false);   // before Second
    var elector = new Elector();
    elector.Answers.Enqueue(() => DutyAttempt.Granted(grant));
    var store = new Store();
    store.Owed.AddRange([_owed("First"), _owed("Second")]);
    var second = _done("Second");
    var worker = _worker(elector, store, [_done("First"), second]);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(second.Runs).IsEqualTo(0).Because("each unit of exclusive work is preceded by a verify");
  }

  [Test]
  public async Task CancellationDuringAPass_PropagatesFromEachStageAsync() {
    // Each stage cancels the pass's own token while it is in flight, as a host stopping would.
    static OperationCanceledException stop(CancellationTokenSource cts) {
      cts.Cancel();
      return new OperationCanceledException(cts.Token);
    }

    static async Task assertCanceledAsync(Func<CancellationTokenSource, (Elector, Store, IDutyWorkHandler)> arrange) {
      using var cts = new CancellationTokenSource();
      var (elector, store, handler) = arrange(cts);
      await Assert.That(async () => await _worker(elector, store, [handler]).RunOnceAsync(cts.Token))
        .Throws<OperationCanceledException>();
    }

    static Elector granting() {
      var elector = new Elector();
      elector.Answers.Enqueue(() => DutyAttempt.Granted(new Grant()));
      return elector;
    }

    static Store owing(Store store) {
      store.Owed.Add(_owed("Rewrite"));
      return store;
    }

    await assertCanceledAsync(cts => {
      var elector = new Elector();
      elector.Answers.Enqueue(() => throw stop(cts));
      return (elector, new Store(), _done("Rewrite"));
    });
    await assertCanceledAsync(cts => (granting(), new Store { ListThrows = () => stop(cts) }, _done("Rewrite")));
    await assertCanceledAsync(cts => (granting(), owing(new Store()), new Handler("Rewrite", _ => throw stop(cts))));
    await assertCanceledAsync(cts => (granting(), owing(new Store { RecordThrows = () => stop(cts) }), _done("Rewrite")));

    using var already = new CancellationTokenSource();
    await already.CancelAsync();
    await Assert.That(async () => await _worker(granting(), new Store(), [_done("Rewrite")]).RunOnceAsync(already.Token))
      .Throws<OperationCanceledException>().Because("the pass gate honors the token too");
  }

  [Test]
  public async Task TheHostedLoop_PassesOnItsInterval_WakesOnARelease_AndReleasesOnStopAsync() {
    var time = new FakeTimeProvider();
    var notify = new Notify();
    var grant = new Grant();
    var elector = new Elector();
    elector.Answers.Enqueue(() => DutyAttempt.Lost(DutyRefusal.Contended, "held elsewhere"));
    elector.Answers.Enqueue(() => DutyAttempt.Lost(DutyRefusal.Contended, "held elsewhere"));
    elector.Answers.Enqueue(() => DutyAttempt.Granted(grant));
    var worker = _worker(elector, new Store(), [_done("Rewrite")], notify, time: time);

    await worker.StartAsync(CancellationToken.None);
    await Assert.That(await elector.Attempted.Task).IsEqualTo(1);

    // The pass cadence runs on the injected clock.
    elector.Attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    time.Advance(new RoleAssignmentOptions().RenewInterval);
    await Assert.That(await elector.Attempted.Task).IsEqualTo(2);

    // A release of some other role is not ours to act on; a release of ours re-votes at once.
    elector.Attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    notify.Subscription!.OnNotification("some-other-role");
    await Assert.That(elector.Attempts).IsEqualTo(2);
    await Assert.That(notify.Subscription.ChannelName).IsEqualTo(DutyHolderWorker.RELEASE_CHANNEL);
    notify.Subscription.OnNotification(ROLE);
    await Assert.That(await elector.Attempted.Task).IsEqualTo(3);
    await Assert.That(worker.Holds(ROLE)).IsTrue();

    await worker.StopAsync(CancellationToken.None);
    await Assert.That(grant.Disposed).IsTrue().Because("a graceful stop releases the role, so the next holder takes it at once");
    await Assert.That(notify.Unsubscribed).IsTrue();
    worker.Dispose();
  }

  [Test]
  public async Task TheHostedLoop_StoppingMidPass_EndsTheLoop_WithoutANotifyConnectionAsync() {
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var elector = new Elector();
    elector.Answers.Enqueue(() => DutyAttempt.Granted(new Grant()));
    var store = new Store();
    store.Owed.Add(_owed("Slow"));
    var slow = new Handler("Slow", async ct => {
      entered.TrySetResult();
      await Task.Delay(Timeout.Infinite, ct);
      return DutyWorkResult.Done();
    });
    var worker = _worker(elector, store, [slow]);

    await worker.StartAsync(CancellationToken.None);
    await entered.Task;
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(worker.Holds(ROLE)).IsFalse().Because("stopping released what the loop held");
    worker.Dispose();
  }

  [Test]
  public async Task Constructor_RefusesNullsAsync() {
    var options = Options.Create(new RoleAssignmentOptions());
    var logger = NullLogger<DutyHolderWorker>.Instance;
    await Assert.That(() => new DutyHolderWorker(null!, new Store(), [], options, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new DutyHolderWorker(new Elector(), null!, [], options, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new DutyHolderWorker(new Elector(), new Store(), null!, options, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new DutyHolderWorker(new Elector(), new Store(), [], null!, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new DutyHolderWorker(new Elector(), new Store(), [], options, null!)).Throws<ArgumentNullException>();
  }
}
