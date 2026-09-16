using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The one place a worker loop decides what an exception out of one iteration means: a transient
/// database failure is named and waited out, anything else is a defect reported as one, and either
/// way the loop is still running afterward. Every loop shares this so a fix here is a fix
/// everywhere, and so no two loops disagree about what a deadlock is.
/// </summary>
public class WorkerLoopRecoveryTests {
  [Test]
  public async Task Report_ATransientFailure_IsNamedByItsClassificationAsync() {
    TransientDatabaseFailure? classified = null;
    Exception? cause = null;
    var defects = 0;
    var deadlock = FakeDbException.WithSqlState("40P01", message: "deadlock detected");

    WorkerLoopRecovery.Report(deadlock,
      (failure, thrown) => { classified = failure; cause = thrown; },
      _ => defects++);

    await Assert.That(classified!.Reason).IsEqualTo(TransientDatabaseFailure.DEADLOCK);
    await Assert.That(classified.SqlState).IsEqualTo("40P01");
    await Assert.That(ReferenceEquals(cause, deadlock)).IsTrue()
      .Because("the report carries the exception as thrown, so the log keeps the whole chain");
    await Assert.That(defects).IsEqualTo(0);
  }

  [Test]
  public async Task Report_AFailureThatIsNotTheDatabases_IsADefectAsync() {
    var transients = 0;
    Exception? reported = null;
    var defect = new InvalidOperationException("a bug in the batch path");

    WorkerLoopRecovery.Report(defect, (_, _) => transients++, thrown => reported = thrown);

    await Assert.That(ReferenceEquals(reported, defect)).IsTrue();
    await Assert.That(transients).IsEqualTo(0);
  }

  [Test]
  public async Task RecoverAsync_WaitsTheBackoffOnTheLoopsOwnClockAsync() {
    var time = new FakeTimeProvider();
    var recovery = new WorkerLoopRecovery(time);
    await Assert.That(recovery.NextBackoff).IsEqualTo(WorkerLoopRecovery.DEFAULT_FLOOR);

    var recovering = recovery.RecoverAsync(
      FakeDbException.WithSqlState("40P01"), (_, _) => { }, _ => { }, CancellationToken.None);

    await Assert.That(recovering.IsCompleted).IsFalse()
      .Because("a loop that hit a failure must pause before the next attempt");
    time.Advance(WorkerLoopRecovery.DEFAULT_FLOOR);
    await recovering.WaitAsync(TimeSpan.FromSeconds(10));
  }

  [Test]
  public async Task RecoverAsync_ARunOfFailures_GrowsTheWaitToTheCeilingAsync() {
    var time = new FakeTimeProvider();
    var recovery = new WorkerLoopRecovery(time, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(40));
    var waits = new List<TimeSpan>();

    for (var attempt = 0; attempt < 4; attempt++) {
      waits.Add(recovery.NextBackoff);
      var recovering = recovery.RecoverAsync(
        FakeDbException.WithSqlState("40001"), (_, _) => { }, _ => { }, CancellationToken.None);
      time.Advance(TimeSpan.FromMilliseconds(40));
      await recovering.WaitAsync(TimeSpan.FromSeconds(10));
    }

    await Assert.That(waits).IsEquivalentTo([
      TimeSpan.FromMilliseconds(10),
      TimeSpan.FromMilliseconds(20),
      TimeSpan.FromMilliseconds(40),
      TimeSpan.FromMilliseconds(40),
    ]).Because("the wait doubles from the floor and stops at the ceiling, so a database that is "
             + "down is asked about once every ceiling rather than as fast as the loop can spin");
  }

  [Test]
  public async Task Recovered_AGoodIteration_SnapsTheWaitBackToTheFloorAsync() {
    var time = new FakeTimeProvider();
    var recovery = new WorkerLoopRecovery(time, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1));
    var recovering = recovery.RecoverAsync(
      FakeDbException.WithSqlState("40P01"), (_, _) => { }, _ => { }, CancellationToken.None);
    time.Advance(TimeSpan.FromMilliseconds(10));
    await recovering.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(recovery.NextBackoff).IsEqualTo(TimeSpan.FromMilliseconds(20));

    recovery.Recovered();

    await Assert.That(recovery.NextBackoff).IsEqualTo(TimeSpan.FromMilliseconds(10))
      .Because("one good iteration says the failure has passed; the next one must not be punished "
             + "for it");
  }

  [Test]
  public async Task RecoverAsync_Shutdown_EndsTheWaitByCancellationAsync() {
    var time = new FakeTimeProvider();
    var recovery = new WorkerLoopRecovery(time);
    using var cts = new CancellationTokenSource();

    var recovering = recovery.RecoverAsync(
      FakeDbException.WithSqlState("40P01"), (_, _) => { }, _ => { }, cts.Token);
    await cts.CancelAsync();

    await Assert.That(async () => await recovering).Throws<OperationCanceledException>()
      .Because("the host is stopping: the loop's cancellation must come out of the backoff, not be "
             + "waited out");
  }

  [Test]
  public async Task Recovery_RefusesWhatItCannotUseAsync() {
    var time = new FakeTimeProvider();
    var recovery = new WorkerLoopRecovery(time);

    await Assert.That(() => new WorkerLoopRecovery(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => WorkerLoopRecovery.Report(null!, (_, _) => { }, _ => { }))
      .Throws<ArgumentNullException>();
    await Assert.That(() => WorkerLoopRecovery.Report(FakeDbException.WithSqlState("40P01"), null!, _ => { }))
      .Throws<ArgumentNullException>();
    await Assert.That(() => WorkerLoopRecovery.Report(FakeDbException.WithSqlState("40P01"), (_, _) => { }, null!))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await recovery.RecoverAsync(null!, (_, _) => { }, _ => { }, CancellationToken.None))
      .Throws<ArgumentNullException>();
  }
}
