// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres;
using LeasedGrant = Whizbang.Data.EFCore.Postgres.Tests.Migrations.MigratorDutyTests.LeasedGrant;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The phases before the schema lock run only on the instance holding the migrator role (#2127): a waiter whose
/// migrator is gone votes for the role before it does the work, and does it only once it holds the role.
/// </summary>
/// <remarks>
/// Each test scripts what the waits and votes answer and asserts the order of what happened, because the defect this
/// guards against (two replicas both scanning and rewriting) and the correct behavior reach the same final state.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/MigratorPreLockPhase.cs</code-under-test>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
[Category("Unit")]
[Category("Shard2")]
public class MigratorPreLockPhaseTests {
  private const string SCHEMA = "public";
  private static readonly TimeSpan _interval = TimeSpan.FromSeconds(4);

  private sealed class RecordingLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
  }

  /// <summary>Scripted waits and votes, and the order everything happened in.</summary>
  private sealed class Script {
    public List<string> Events { get; } = [];
    public Queue<Func<bool>> Waits { get; } = new();
    public Queue<SchemaStaging> Elections { get; } = new();
    public Func<bool>? WorkCheck { get; set; }

    public PreLockPhaseSteps Steps => new(
      (stage, _) => {
        Events.Add("wait:" + stage);
        return Task.FromResult(Waits.Dequeue()());
      },
      _ => {
        Events.Add("elect");
        return Task.FromResult(Elections.Dequeue());
      },
      (attempt, _) => {
        Events.Add("pause:" + attempt);
        return Task.CompletedTask;
      },
      _ => {
        Events.Add("work" + (WorkCheck is { } check ? ":" + check() : string.Empty));
        return Task.CompletedTask;
      });
  }

  private static SchemaStaging _won(IDutyGrant grant) => new(SchemaStage.Migrator, grant, "won");
  private static SchemaStaging _lost => new(SchemaStage.Waiter, null, "role 'migrator' is held by instance x");

  [Test]
  public async Task TheRoleHolder_WaitsForTheSchemaLock_ThenDoesTheWork_WithoutVotingAgainAsync() {
    var grant = new LeasedGrant(_interval);
    await using var duty = new MigratorDuty(new FakeTimeProvider());
    await duty.HoldAsync(_won(grant));
    var script = new Script { WorkCheck = () => duty.IsKeptAlive };
    script.Waits.Enqueue(() => true);
    var logger = new RecordingLogger();

    await MigratorPreLockPhase.RunAsync(duty, script.Steps, SCHEMA, logger);

    await Assert.That(string.Join(" > ", script.Events)).IsEqualTo("wait:Migrator > work:True")
      .Because("the holder does the work, under the keepalive its election started");
    await Assert.That(grant.Count).IsEqualTo(1)
      .Because("it confirms it still holds the role before it starts");
    await Assert.That(logger.Entries).Contains(e => e.Level == LogLevel.Information && e.Message.Contains("holds the migrator role"));
  }

  [Test]
  public async Task AnUnstagedInstance_DoesTheWork_AsEveryInstanceDidBeforeAnElectionExistedAsync() {
    await using var duty = new MigratorDuty(new FakeTimeProvider());
    var script = new Script();
    script.Waits.Enqueue(() => true);

    await MigratorPreLockPhase.RunAsync(duty, script.Steps, SCHEMA);

    await Assert.That(string.Join(" > ", script.Events)).IsEqualTo("wait:Unstaged > work");
  }

  [Test]
  public async Task AWaiter_WhoseMigratorFinished_DoesNothingAsync() {
    await using var duty = new MigratorDuty(new FakeTimeProvider());
    await duty.HoldAsync(_lost);
    var script = new Script();
    script.Waits.Enqueue(() => false);

    await MigratorPreLockPhase.RunAsync(duty, script.Steps, SCHEMA);

    await Assert.That(string.Join(" > ", script.Events)).IsEqualTo("wait:Waiter");
  }

  [Test]
  public async Task AWaiter_WhoseMigratorIsGone_VotesForTheRole_AndWorksOnlyOnceItHoldsItAsync() {
    var grant = new LeasedGrant(_interval);
    await using var duty = new MigratorDuty(new FakeTimeProvider());
    await duty.HoldAsync(_lost);
    var script = new Script { WorkCheck = () => duty.Stage == SchemaStage.Migrator && duty.IsKeptAlive };
    script.Waits.Enqueue(() => true);
    script.Waits.Enqueue(() => true);
    script.Elections.Enqueue(_won(grant));
    var logger = new RecordingLogger();

    await MigratorPreLockPhase.RunAsync(duty, script.Steps, SCHEMA, logger);

    await Assert.That(string.Join(" > ", script.Events)).IsEqualTo("wait:Waiter > elect > wait:Migrator > work:True")
      .Because("the takeover holds the role, kept alive, before it touches a table");
    await Assert.That(duty.Grant).IsSameReferenceAs(grant);
    await Assert.That(logger.Entries).Contains(e => e.Level == LogLevel.Information && e.Message.Contains("voting for the migrator role"));
  }

  [Test]
  public async Task AWaiter_ThatLosesTheVote_WaitsForTheWinner_AndNeverDoesTheWorkAsync() {
    await using var duty = new MigratorDuty(new FakeTimeProvider());
    await duty.HoldAsync(_lost);
    var script = new Script();
    script.Waits.Enqueue(() => true);
    script.Waits.Enqueue(() => false);
    script.Elections.Enqueue(_lost);
    var logger = new RecordingLogger();

    await MigratorPreLockPhase.RunAsync(duty, script.Steps, SCHEMA, logger);

    await Assert.That(string.Join(" > ", script.Events)).IsEqualTo("wait:Waiter > elect > wait:Waiter")
      .Because("only the role holder runs the pre-lock phases; a replica that lost the vote waits for the one that won");
    await Assert.That(logger.Entries).Contains(e => e.Level == LogLevel.Information && e.Message.Contains("Another instance won the migrator role"));
  }

  [Test]
  public async Task RepeatedLostVotes_ArePaced_SoAWaiterNeverSpinsAsync() {
    var grant = new LeasedGrant(_interval);
    await using var duty = new MigratorDuty(new FakeTimeProvider());
    await duty.HoldAsync(_lost);
    var script = new Script();
    for (var i = 0; i < 4; i++) {
      script.Waits.Enqueue(() => true);
    }
    script.Elections.Enqueue(_lost);
    script.Elections.Enqueue(_lost);
    script.Elections.Enqueue(_won(grant));

    await MigratorPreLockPhase.RunAsync(duty, script.Steps, SCHEMA);

    await Assert.That(string.Join(" > ", script.Events)).IsEqualTo("wait:Waiter > elect > wait:Waiter > pause:1 > elect > wait:Waiter > pause:2 > elect > wait:Migrator > work");
  }

  [Test]
  public async Task AWaiter_ThatCannotWatchItsMigrator_VotesRatherThanWorkingWithoutTheRoleAsync() {
    await using var duty = new MigratorDuty(new FakeTimeProvider());
    await duty.HoldAsync(_lost);
    var script = new Script();
    script.Waits.Enqueue(() => throw new InvalidOperationException("watch failed"));
    script.Waits.Enqueue(() => false);
    script.Elections.Enqueue(_lost);
    var logger = new RecordingLogger();

    await MigratorPreLockPhase.RunAsync(duty, script.Steps, SCHEMA, logger);

    await Assert.That(string.Join(" > ", script.Events)).IsEqualTo("wait:Waiter > elect > wait:Waiter");
    await Assert.That(logger.Entries).Contains(e => e.Level == LogLevel.Warning && e.Message.Contains("Could not wait"));
  }

  [Test]
  public async Task TheRoleHolder_ThatCannotWatchTheSchemaLock_StillDoesTheWorkAsync() {
    var grant = new LeasedGrant(_interval);
    await using var duty = new MigratorDuty(new FakeTimeProvider());
    await duty.HoldAsync(_won(grant));
    var script = new Script();
    script.Waits.Enqueue(() => throw new InvalidOperationException("probe failed"));

    await MigratorPreLockPhase.RunAsync(duty, script.Steps, SCHEMA);

    await Assert.That(string.Join(" > ", script.Events)).IsEqualTo("wait:Migrator > work");
  }

  [Test]
  public async Task AMigratorThatNoLongerHoldsTheRole_GivesItUp_AndVotesAgainBeforeWorkingAsync() {
    var lapsed = new LeasedGrant(_interval) { Answer = false };
    var regained = new LeasedGrant(_interval);
    await using var duty = new MigratorDuty(new FakeTimeProvider());
    await duty.HoldAsync(_won(lapsed));
    var script = new Script();
    script.Waits.Enqueue(() => true);
    script.Waits.Enqueue(() => true);
    script.Elections.Enqueue(_won(regained));
    var logger = new RecordingLogger();

    await MigratorPreLockPhase.RunAsync(duty, script.Steps, SCHEMA, logger);

    await Assert.That(string.Join(" > ", script.Events)).IsEqualTo("wait:Migrator > elect > wait:Migrator > work");
    await Assert.That(lapsed.Disposed).IsTrue();
    await Assert.That(duty.Grant).IsSameReferenceAs(regained);
    await Assert.That(logger.Entries).Contains(e => e.Level == LogLevel.Warning && e.Message.Contains("no longer holds the migrator role"));
  }

  [Test]
  public async Task AVoteThatCannotBeHeld_FallsBackToTheLockAlone_AndDoesTheWorkAsync() {
    await using var duty = new MigratorDuty(new FakeTimeProvider());
    await duty.HoldAsync(_lost);
    var script = new Script();
    script.Waits.Enqueue(() => true);
    script.Waits.Enqueue(() => true);
    script.Elections.Enqueue(new SchemaStaging(SchemaStage.Unstaged, null, "the elector could not be asked"));

    await MigratorPreLockPhase.RunAsync(duty, script.Steps, SCHEMA);

    await Assert.That(string.Join(" > ", script.Events)).IsEqualTo("wait:Waiter > elect > wait:Unstaged > work");
  }

  [Test]
  public async Task Cancellation_DuringAWait_IsNotTreatedAsAnEndedWaitAsync() {
    await using var duty = new MigratorDuty(new FakeTimeProvider());
    await duty.HoldAsync(_lost);
    var script = new Script();
    script.Waits.Enqueue(() => throw new OperationCanceledException());

    await Assert.That(async () => await MigratorPreLockPhase.RunAsync(duty, script.Steps, SCHEMA))
      .Throws<OperationCanceledException>();
    await Assert.That(string.Join(" > ", script.Events)).IsEqualTo("wait:Waiter");
  }

  [Test]
  public async Task MissingArguments_AreRefusedAsync() {
    await using var duty = new MigratorDuty(new FakeTimeProvider());
    await Assert.That(async () => await MigratorPreLockPhase.RunAsync(null!, new Script().Steps, SCHEMA))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await MigratorPreLockPhase.RunAsync(duty, null!, SCHEMA))
      .Throws<ArgumentNullException>();
  }

  [Test]
  [Arguments(1, 250)]
  [Arguments(2, 500)]
  [Arguments(3, 1000)]
  [Arguments(6, 5000)]
  [Arguments(50, 5000)]
  public async Task ReelectionDelay_FollowsTheDeferralBackoffAsync(int attempt, int milliseconds) {
    await Assert.That(MigratorPreLockPhase.ReelectionDelay(attempt)).IsEqualTo(TimeSpan.FromMilliseconds(milliseconds));
  }
}
