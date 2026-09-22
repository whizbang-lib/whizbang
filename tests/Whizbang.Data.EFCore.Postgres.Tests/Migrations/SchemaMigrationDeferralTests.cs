using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// That an instance which lost the race to migrate waits for the winner's result instead of
/// queuing behind it, and takes over when the winner stops existing.
/// </summary>
/// <remarks>
/// <para>
/// Both endings are failures if they happen in the wrong situation, and the two situations look
/// identical from the loser's side: a failed try-lock says only "not yours". Taking over while the
/// migrator works means two instances issuing DDL over the same tables. Waiting on a migrator that
/// has died means the schema never advances and nothing in the fleet ever starts. So every test
/// here fixes which situation it is and asserts the decision, rather than asserting a final state
/// that both decisions could reach.
/// </para>
/// <para>
/// Driven by a fake clock. The waits between polls are part of the contract, so they are measured
/// off that clock rather than slept through, and nothing in this file waits on wall time.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaMigrationDeferral.cs</code-under-test>
/// <docs>operations/infrastructure/migrations</docs>
[Category("Unit")]
[Category("Shard1")]
public class SchemaMigrationDeferralTests {

  private const string SCHEMA = "public";

  /// <summary>A logger that keeps what it was told, so a reported decision is assertable.</summary>
  private sealed class RecordingLogger : ILogger {
    public List<string> Messages { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Scope();
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
      Messages.Add(formatter(state, exception));
    private sealed class Scope : IDisposable { public void Dispose() { } }
  }

  /// <summary>
  /// Runs a deferral, advancing the fake clock until it decides.
  /// </summary>
  /// <remarks>
  /// The clock is stepped rather than the test sleeping, so the outcome does not depend on
  /// scheduling. One millisecond per step because two of these tests assert the length of the waits
  /// themselves, and a coarser step would round them.
  /// </remarks>
  private static async Task<SchemaDeferralOutcome> _deferAsync(
      Func<CancellationToken, Task<bool>> isClean,
      Func<CancellationToken, Task<bool>> isHeld,
      FakeTimeProvider time,
      ILogger? logger = null,
      CancellationToken cancellationToken = default) {
    var run = SchemaMigrationDeferral.DeferAsync(
      isClean, isHeld, time, SCHEMA, logger, cancellationToken);

    while (!run.IsCompleted) {
      time.Advance(TimeSpan.FromMilliseconds(1));
      await Task.Yield();
    }

    return await run;
  }

  /// <summary>
  /// A schema that is already current ends the deferral at once, without asking about the lock.
  /// </summary>
  /// <remarks>
  /// The overwhelmingly common case: by the time a loser rolled its transaction back, the winner
  /// had already committed. It must cost one query, not a wait and not a second round trip, because
  /// this is on the critical path of every replica's cold start.
  /// </remarks>
  [Test]
  public async Task ACurrentSchemaEndsTheDeferralWithoutProbingTheLockAsync() {
    var time = new FakeTimeProvider();
    var start = time.GetUtcNow();
    var lockProbes = 0;

    var outcome = await _deferAsync(
      _ => Task.FromResult(true),
      _ => { lockProbes++; return Task.FromResult(true); },
      time);

    await Assert.That(outcome).IsEqualTo(SchemaDeferralOutcome.AnotherInstanceMigrated);
    await Assert.That(lockProbes).IsEqualTo(0)
      .Because("the schema is current, so who holds the lock cannot change the answer");
    await Assert.That(time.GetUtcNow()).IsEqualTo(start)
      .Because("nothing was waited for, so a cold start pays nothing for this phase");
  }

  /// <summary>
  /// While the lock is held the deferral keeps waiting, and ends only when the schema is current.
  /// </summary>
  /// <remarks>
  /// The property the whole mechanism exists for. Taking over here would put two instances inside
  /// the same DDL, which is the race the lock elects a migrator to prevent, so "did not take over
  /// while the holder was working" is asserted directly rather than inferred from the ending.
  /// </remarks>
  [Test]
  public async Task AHeldLockKeepsTheDeferralWaitingAsync() {
    var time = new FakeTimeProvider();
    var polls = 0;

    var outcome = await _deferAsync(
      _ => Task.FromResult(++polls > 3),
      _ => Task.FromResult(true),
      time);

    await Assert.That(outcome).IsEqualTo(SchemaDeferralOutcome.AnotherInstanceMigrated);
    await Assert.That(polls).IsEqualTo(4)
      .Because("it waited out three polls of a working migrator instead of migrating alongside it");
  }

  /// <summary>
  /// A released lock over a schema that is still behind means the migrator is gone.
  /// </summary>
  /// <remarks>
  /// The crash case, and the reason this is a lock probe rather than a deadline. A pod killed
  /// outright runs no cleanup, but its session ends and the lock goes with it, so its absence is
  /// the signal — available immediately and with nothing to tune. Waiting instead would strand
  /// every survivor on a schema that will never advance.
  /// </remarks>
  [Test]
  public async Task AReleasedLockOverAStaleSchemaTakesOverAsync() {
    var time = new FakeTimeProvider();

    var outcome = await _deferAsync(
      _ => Task.FromResult(false),
      _ => Task.FromResult(false),
      time);

    await Assert.That(outcome).IsEqualTo(SchemaDeferralOutcome.MigratingInstanceGone);
  }

  /// <summary>
  /// A migrator that dies part way through a long wait is noticed then, not only at the start.
  /// </summary>
  /// <remarks>
  /// The realistic crash. A pod that dies immediately is caught by the first poll, but one killed
  /// while a big migration is running has already had waiters settle into the backoff, and by then
  /// the deferral has reported that it is waiting. Detection has to keep working after that point,
  /// or the fleet stays parked on the log line saying it is being patient.
  /// </remarks>
  [Test]
  public async Task AMigratorThatDiesMidWaitIsNoticedAsync() {
    var time = new FakeTimeProvider();
    var logger = new RecordingLogger();
    var lockChecks = 0;

    var outcome = await _deferAsync(
      _ => Task.FromResult(false),
      _ => Task.FromResult(++lockChecks < 4),
      time,
      logger);

    await Assert.That(outcome).IsEqualTo(SchemaDeferralOutcome.MigratingInstanceGone);
    await Assert.That(lockChecks).IsEqualTo(4)
      .Because("it waited out three live checks and acted on the fourth, when the lock went away");
  }

  /// <summary>
  /// A migrator that committed is not mistaken for one that died.
  /// </summary>
  /// <remarks>
  /// A winner releases its lock and marks the schema current in the same instant, so the poll that
  /// lands there sees both at once and the order of the two questions decides the answer. Asking
  /// about the lock first would read "released", conclude the migrator was gone, and take the lock
  /// to redo a pass with nothing in it — on every replica, on every startup.
  /// </remarks>
  [Test]
  public async Task ACommittedMigratorIsNotMistakenForADeadOneAsync() {
    var time = new FakeTimeProvider();

    var outcome = await _deferAsync(
      _ => Task.FromResult(true),
      _ => Task.FromResult(false),
      time);

    await Assert.That(outcome).IsEqualTo(SchemaDeferralOutcome.AnotherInstanceMigrated)
      .Because("the schema is current, so the released lock is a completed migration, not a death");
  }

  /// <summary>The waits double and then stay at the ceiling.</summary>
  /// <remarks>
  /// Asserted on the pure function rather than by timing the loop, so the shape of the backoff is
  /// pinned exactly and not to within a scheduling margin.
  /// </remarks>
  [Test]
  public async Task TheWaitDoublesUpToTheCeilingAsync() {
    await Assert.That(SchemaMigrationDeferral.NextPollDelay(SchemaMigrationDeferral.PollFloor))
      .IsEqualTo(SchemaMigrationDeferral.PollFloor * 2);
    await Assert.That(SchemaMigrationDeferral.NextPollDelay(SchemaMigrationDeferral.PollCeiling))
      .IsEqualTo(SchemaMigrationDeferral.PollCeiling);
    await Assert.That(SchemaMigrationDeferral.NextPollDelay(SchemaMigrationDeferral.PollCeiling / 2))
      .IsEqualTo(SchemaMigrationDeferral.PollCeiling);
    await Assert.That(SchemaMigrationDeferral.NextPollDelay(TimeSpan.Zero))
      .IsEqualTo(SchemaMigrationDeferral.PollFloor)
      .Because("a zero or negative previous wait must still produce a real one, or the loop spins");
    await Assert.That(SchemaMigrationDeferral.NextPollDelay(TimeSpan.FromTicks(-1)))
      .IsEqualTo(SchemaMigrationDeferral.PollFloor);
  }

  /// <summary>The loop actually waits those lengths between polls.</summary>
  /// <remarks>
  /// The pure function being right does not prove the loop uses it. Read off the fake clock at each
  /// poll, so the assertion is on the intervals the deferral asked for.
  /// </remarks>
  [Test]
  public async Task TheLoopWaitsTheBackoffBetweenPollsAsync() {
    var time = new FakeTimeProvider();
    var stamps = new List<DateTimeOffset>();

    await _deferAsync(
      _ => { stamps.Add(time.GetUtcNow()); return Task.FromResult(stamps.Count >= 4); },
      _ => Task.FromResult(true),
      time);

    await Assert.That(stamps.Count).IsEqualTo(4);
    await Assert.That(stamps[1] - stamps[0]).IsEqualTo(SchemaMigrationDeferral.PollFloor);
    await Assert.That(stamps[2] - stamps[1]).IsEqualTo(SchemaMigrationDeferral.PollFloor * 2);
    await Assert.That(stamps[3] - stamps[2]).IsEqualTo(SchemaMigrationDeferral.PollFloor * 4);
  }

  /// <summary>
  /// A lock probe that fails ends the deferral rather than waiting on an unobservable condition.
  /// </summary>
  /// <remarks>
  /// If we cannot see whether anyone is migrating, waiting is a guess that costs the whole fleet
  /// its startup when it is wrong. Contending for the lock is the guess that cannot strand anything:
  /// it is what every instance did before this existed, and the lock still excludes.
  /// </remarks>
  [Test]
  public async Task AFailedLockProbeContendsForTheLockAsync() {
    var time = new FakeTimeProvider();
    var logger = new RecordingLogger();

    var outcome = await _deferAsync(
      _ => Task.FromResult(false),
      _ => throw new InvalidOperationException("pg_locks unreadable"),
      time,
      logger);

    await Assert.That(outcome).IsEqualTo(SchemaDeferralOutcome.MigratingInstanceGone);
    await Assert.That(logger.Messages.Count).IsGreaterThan(0)
      .Because("a probe this decision depends on failing must not be silent");
  }

  /// <summary>
  /// A cleanliness probe that fails reads as "not current", and the deferral carries on.
  /// </summary>
  /// <remarks>
  /// The tracking tables are themselves created by the migration being waited for, so a failure
  /// here is the expected first answer on a genuinely empty database rather than an error. Treating
  /// it as "current" would let an instance start against a schema that does not exist yet.
  /// </remarks>
  [Test]
  public async Task AFailedCleanlinessProbeIsNotTreatedAsCurrentAsync() {
    var time = new FakeTimeProvider();
    var logger = new RecordingLogger();
    var polls = 0;

    var outcome = await _deferAsync(
      _ => {
        polls++;
        if (polls <= 2) {
          throw new InvalidOperationException("relation \"wh_schema_migrations\" does not exist");
        }
        return Task.FromResult(true);
      },
      _ => Task.FromResult(true),
      time,
      logger);

    await Assert.That(outcome).IsEqualTo(SchemaDeferralOutcome.AnotherInstanceMigrated);
    await Assert.That(polls).IsEqualTo(3)
      .Because("an unanswerable question is not a yes; it waited and asked again");
    await Assert.That(logger.Messages.Count).IsGreaterThan(0);
  }

  /// <summary>Cancellation during a wait ends the deferral by throwing.</summary>
  /// <remarks>
  /// Host shutdown while waiting out a long migration. Returning an outcome would send the caller
  /// on to run DDL during shutdown.
  /// </remarks>
  [Test]
  public async Task CancellationDuringTheWaitThrowsAsync() {
    var time = new FakeTimeProvider();
    using var cts = new CancellationTokenSource();

    var run = SchemaMigrationDeferral.DeferAsync(
      _ => Task.FromResult(false),
      _ => { cts.Cancel(); return Task.FromResult(true); },
      time,
      SCHEMA,
      NullLogger.Instance,
      cts.Token);

    await Assert.That(async () => {
      while (!run.IsCompleted) {
        time.Advance(TimeSpan.FromMilliseconds(1));
        await Task.Yield();
      }
      await run;
    }).Throws<OperationCanceledException>();
  }

  /// <summary>A token already canceled does no work at all.</summary>
  [Test]
  public async Task AnAlreadyCanceledTokenThrowsBeforeProbingAsync() {
    var time = new FakeTimeProvider();
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();
    var probes = 0;

    await Assert.That(async () => await SchemaMigrationDeferral.DeferAsync(
      _ => { probes++; return Task.FromResult(true); },
      _ => Task.FromResult(true),
      time,
      SCHEMA,
      null,
      cts.Token)).Throws<OperationCanceledException>();

    await Assert.That(probes).IsEqualTo(0);
  }

  /// <summary>Missing probes or clock are caller errors.</summary>
  [Test]
  public async Task MissingArgumentsAreRefusedAsync() {
    var time = new FakeTimeProvider();

    await Assert.That(async () => await SchemaMigrationDeferral.DeferAsync(
      null!, _ => Task.FromResult(true), time, SCHEMA)).Throws<ArgumentNullException>();
    await Assert.That(async () => await SchemaMigrationDeferral.DeferAsync(
      _ => Task.FromResult(true), null!, time, SCHEMA)).Throws<ArgumentNullException>();
    await Assert.That(async () => await SchemaMigrationDeferral.DeferAsync(
      _ => Task.FromResult(true), _ => Task.FromResult(true), null!, SCHEMA))
      .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// A whole fleet deferring to one migrator all stand down, and all notice when it finishes.
  /// </summary>
  /// <remarks>
  /// Two waiters can agree by accident of ordering. Five sharing one migrator is where a mechanism
  /// that only mostly works shows itself: the moment of release is seen by all of them at once,
  /// which is exactly when checking the lock before the schema would send every one of them off to
  /// redo the migration.
  /// </remarks>
  [Test]
  public async Task AFleetDeferringToOneMigratorAllStandDownAsync() {
    var time = new FakeTimeProvider();
    var migrating = true;
    var pollsWhileMigrating = 0;

    // The migrator finishes after the fleet has been round the loop a few times. Released lock and
    // current schema flip together, as a commit makes them, and they flip inside this probe so no
    // waiter can ever observe one without the other.
    Task<bool> isClean(CancellationToken _) {
      if (migrating && Interlocked.Increment(ref pollsWhileMigrating) > 10) {
        Volatile.Write(ref migrating, false);
      }
      return Task.FromResult(!Volatile.Read(ref migrating));
    }

    var fleet = Enumerable.Range(0, 5)
      .Select(_ => SchemaMigrationDeferral.DeferAsync(
        isClean, _ => Task.FromResult(Volatile.Read(ref migrating)), time, SCHEMA))
      .ToArray();

    while (!fleet.All(t => t.IsCompleted)) {
      time.Advance(TimeSpan.FromMilliseconds(1));
      await Task.Yield();
    }

    var outcomes = await Task.WhenAll(fleet);
    await Assert.That(outcomes.All(o => o == SchemaDeferralOutcome.AnotherInstanceMigrated)).IsTrue()
      .Because("not one of them may conclude the migrator died while it was working");
  }
}
