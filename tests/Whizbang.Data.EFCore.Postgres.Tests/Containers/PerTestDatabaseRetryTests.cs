using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Containers;

/// <summary>
/// The policy that gets a test its own database while every sibling fixture is asking for one too.
/// </summary>
/// <remarks>
/// <para>
/// Creating a scratch database is not a quiet operation: it copies a template under a lock, so
/// concurrent creators serialize and a loser fails outright. A fixture that issues the statement
/// once turns that into a test failure in a few milliseconds, inside <c>[Before(Test)]</c>, before
/// any assertion has run, which reads as a defect in whatever test happened to be holding the slot.
/// </para>
/// <para>
/// These are unit tests over the policy rather than the statements, so they cost no database: the
/// attempt is a delegate, the clock is fake, and the failures are the exceptions a server actually
/// returns. What matters is that a retry happens for the states worth retrying, does not happen for
/// anything else, and that an attempt which secretly succeeded is not turned into an error.
/// </para>
/// </remarks>
/// <docs>contributors/ai-agent-guide</docs>
[Category("Unit")]
[Category("Shard1")]
public class PerTestDatabaseRetryTests {
  /// <summary>The template being read by another creator, which is the contention this absorbs.</summary>
  private static PostgresException _objectInUse() =>
    new("source database \"template1\" is being accessed by other users", "ERROR", "ERROR", "55006");

  /// <summary>Too many clients: the other way a shard full of fixtures loses a creation.</summary>
  private static PostgresException _tooManyClients() =>
    new("too many clients already", "FATAL", "FATAL", "53300");

  /// <summary>The database is already there, which means an earlier attempt of ours won.</summary>
  private static PostgresException _duplicateDatabase() =>
    new("database already exists", "ERROR", "ERROR", "42P04");

  /// <summary>A defect, which no number of attempts improves.</summary>
  private static PostgresException _syntaxError() =>
    new("syntax error", "ERROR", "ERROR", "42601");

  private static async Task<int> _runAsync(
      FakeTimeProvider time, Func<int, Exception?> failureForAttempt, int stopAfter = 10) {
    var attempts = 0;
    var run = PerTestDatabaseFactory.RunWithRetryAsync(
      () => {
        attempts++;
        var failure = attempts > stopAfter ? null : failureForAttempt(attempts);
        return failure is null ? Task.CompletedTask : Task.FromException(failure);
      },
      PerTestDatabaseFactory.IsAlreadyCreated,
      time,
      CancellationToken.None);

    // The policy waits on the fake clock between attempts, so the clock is stepped until it is done
    // rather than any real time passing.
    while (!run.IsCompleted) {
      time.Advance(TimeSpan.FromSeconds(1));
      await Task.Yield();
    }

    await run;
    return attempts;
  }

  /// <summary>A creation that loses the template race is tried again and succeeds.</summary>
  [Test]
  public async Task ContentionOnTheTemplateIsRetriedUntilItSucceedsAsync() {
    var time = new FakeTimeProvider();

    var attempts = await _runAsync(time, _ => _objectInUse(), stopAfter: 2);

    await Assert.That(attempts).IsEqualTo(3)
      .Because("two creators lost the template race and the third attempt got it, which is the "
             + "whole point: the fixture asks again rather than failing the test");
  }

  /// <summary>A server out of connection slots is the same class of answer, and is also retried.</summary>
  /// <remarks>
  /// This one the shared classifier already recognizes, through its insufficient-resources class.
  /// Asserted so the reliance on that stays visible: were the classifier to stop covering it, the
  /// flake would come back and this is the test that would say why.
  /// </remarks>
  [Test]
  public async Task AServerOutOfClientSlotsIsRetriedAsync() {
    var time = new FakeTimeProvider();

    await Assert.That(TransientDatabaseFailure.IsTransient(_tooManyClients())).IsTrue()
      .Because("the worker-loop classifier owns this state, and the fixture policy defers to it");
    var attempts = await _runAsync(time, _ => _tooManyClients(), stopAfter: 1);

    await Assert.That(attempts).IsEqualTo(2);
  }

  /// <summary>
  /// An attempt that created the database and then failed to say so is success, not an error.
  /// </summary>
  /// <remarks>
  /// The name is held across attempts precisely so this is recognizable. Were a fresh name minted
  /// per attempt, a creation that succeeded and lost its answer would leak a database and the
  /// retry would build a second one.
  /// </remarks>
  [Test]
  public async Task ADatabaseAnEarlierAttemptCreatedIsNotAnErrorAsync() {
    var time = new FakeTimeProvider();

    var attempts = await _runAsync(time, attempt => attempt == 1 ? _objectInUse() : _duplicateDatabase());

    await Assert.That(attempts).IsEqualTo(2)
      .Because("the second attempt found the database already there, which is the first attempt "
             + "having won after all, so the policy stops rather than reporting a failure");
  }

  /// <summary>A defect is not contention: it is raised on the first attempt.</summary>
  [Test]
  public async Task ADefectIsRaisedWithoutRetryingAsync() {
    var time = new FakeTimeProvider();
    var attempts = 0;

    await Assert.That(async () => await PerTestDatabaseFactory.RunWithRetryAsync(
      () => {
        attempts++;
        return Task.FromException(_syntaxError());
      },
      PerTestDatabaseFactory.IsAlreadyCreated,
      time,
      CancellationToken.None)).Throws<PostgresException>();

    await Assert.That(attempts).IsEqualTo(1)
      .Because("retrying a statement the server will always refuse only delays the real answer");
  }

  /// <summary>Contention that never clears ends as the failure it was, not as a hang.</summary>
  [Test]
  public async Task ContentionThatNeverClearsIsRaisedAfterTheLastAttemptAsync() {
    var time = new FakeTimeProvider();
    var attempts = 0;

    var run = PerTestDatabaseFactory.RunWithRetryAsync(
      () => {
        attempts++;
        return Task.FromException(_objectInUse());
      },
      PerTestDatabaseFactory.IsAlreadyCreated,
      time,
      CancellationToken.None);
    while (!run.IsCompleted) {
      time.Advance(TimeSpan.FromSeconds(1));
      await Task.Yield();
    }

    await Assert.That(async () => await run).Throws<PostgresException>();
    await Assert.That(attempts).IsEqualTo(PerTestDatabaseFactory.MAX_ATTEMPTS)
      .Because("the budget is bounded, and the last failure is the answer the fixture reports");
  }

  /// <summary>What counts as contention, stated directly.</summary>
  [Test]
  public async Task ContentionIsTheClassifiersVerdictPlusTheTemplateRaceAsync() {
    await Assert.That(PerTestDatabaseFactory.IsContention(_objectInUse())).IsTrue()
      .Because("55006 is specific to creating a database and is deliberately not in the "
             + "production classifier, because a worker loop retrying object-in-use is a "
             + "different decision");
    await Assert.That(PerTestDatabaseFactory.IsContention(_tooManyClients())).IsTrue();
    await Assert.That(PerTestDatabaseFactory.IsContention(_syntaxError())).IsFalse();
    await Assert.That(PerTestDatabaseFactory.IsContention(new InvalidOperationException())).IsFalse()
      .Because("a failure that is not the server's is the fixture's own defect");
  }

  /// <summary>Missing arguments are caller errors.</summary>
  [Test]
  public async Task MissingArgumentsAreRefusedAsync() {
    var time = new FakeTimeProvider();

    await Assert.That(async () => await PerTestDatabaseFactory.RunWithRetryAsync(
      null!, PerTestDatabaseFactory.IsAlreadyCreated, time, CancellationToken.None))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await PerTestDatabaseFactory.RunWithRetryAsync(
      () => Task.CompletedTask, null!, time, CancellationToken.None))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await PerTestDatabaseFactory.RunWithRetryAsync(
      () => Task.CompletedTask, PerTestDatabaseFactory.IsAlreadyCreated, null!, CancellationToken.None))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await PerTestDatabaseFactory.CreateAsync(" "))
      .Throws<ArgumentException>();
    await Assert.That(async () => await PerTestDatabaseFactory.DropAsync(" "))
      .Throws<ArgumentException>();
  }
}
