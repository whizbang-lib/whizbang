// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The generated schema initializer keeps the migrator's grant alive from the election to the end of the pass, and runs
/// the phases before the schema lock only on the instance holding the role (#2127).
/// </summary>
/// <remarks>
/// <para>
/// The gravity test: at every stage the initializer reports after it won the role, and after every step it commits,
/// the test advances the initializer's clock by one keepalive tick and waits for the keepalive's verify of the grant.
/// A phase that ran outside the keepalive would never see that verify, and the test would not finish. Once the
/// initializer returns, the grant is released and no tick renews it.
/// </para>
/// <para>
/// The takeover tests script the votes. A waiter whose migrator is gone may run the pre-lock phases only once a vote
/// grants it the role, so the stage that opens those phases has to arrive after that vote and never before it.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/MigratorPreLockPhase.cs</code-under-test>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
[Category("Integration")]
[Category("Shard4")]
public class MigratorKeepaliveStartupTests : EFCoreTestBase {
  private static readonly TimeSpan _renewInterval = TimeSpan.FromSeconds(10);
  private static readonly TimeSpan _tick = TimeSpan.FromSeconds(5);

  /// <summary>How a check names the moment the pre-lock phases begin.</summary>
  private const string PRE_LOCK = "pre-lock phases";

  private sealed class Pod : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "keepalive-svc";
    public string HostName => "keepalive-host";
    public int ProcessId => 13;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  /// <summary>A leased grant that reports each verify.</summary>
  private sealed class LeasedGrant : IDutyGrant {
    private readonly Channel<int> _verifies = Channel.CreateUnbounded<int>();
    private int _count;
    public string Duty => StartupDuties.MIGRATOR;
    public DateTimeOffset AcquiredAt => DateTimeOffset.UnixEpoch;
    public TimeSpan? RenewInterval => _renewInterval;
    public int Count => Volatile.Read(ref _count);
    public bool Disposed { get; private set; }
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) {
      _verifies.Writer.TryWrite(Interlocked.Increment(ref _count));
      return Task.FromResult(true);
    }
    public void Drain() {
      while (_verifies.Reader.TryRead(out _)) {
        // each verify already reported is dropped: the next one read is the keepalive's answer to the next tick
      }
    }
    public ValueTask<int> NextVerifyAsync(CancellationToken cancellationToken) => _verifies.Reader.ReadAsync(cancellationToken);
    public ValueTask DisposeAsync() {
      Disposed = true;
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>An elector whose votes are scripted: contended until the given vote, granted from it on.</summary>
  private sealed class ScriptedElector(int grantedFromVote, LeasedGrant grant) : IDutyElector {
    private int _votes;
    public int Votes => Volatile.Read(ref _votes);
    public bool HasGranted => Votes >= grantedFromVote;
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
      var vote = Interlocked.Increment(ref _votes);
      return Task.FromResult(vote >= grantedFromVote
        ? DutyAttempt.Granted(grant)
        : DutyAttempt.Lost(DutyRefusal.Contended, "role 'migrator' is held by another instance"));
    }
  }

  /// <summary>
  /// At each check, advances the initializer's clock one tick and waits for the keepalive's verify. Nothing else can
  /// verify meanwhile: the initializer is held in this check until it returns.
  /// </summary>
  private sealed class KeepaliveCheck(FakeTimeProvider time, LeasedGrant grant, ScriptedElector elector) {
    public List<string> Checked { get; } = [];
    public List<int> PreLockVotes { get; } = [];

    public async ValueTask CheckAsync(string where, CancellationToken cancellationToken) {
      if (!elector.HasGranted || grant.Disposed) {
        return;
      }
      grant.Drain();
      time.Advance(_tick);
      await grant.NextVerifyAsync(cancellationToken);
      lock (Checked) {
        Checked.Add(where);
      }
    }
  }

  /// <summary>The instance's stage, as the reporter keeps it; each stage after the election is a keepalive check.</summary>
  private sealed class CheckingStageReporter(KeepaliveCheck check) : IStartupStageReporter {
    public StartupStageSnapshot? Current { get; private set; }

    public async ValueTask EnterAsync(StartupStage stage, string message, CancellationToken cancellationToken = default) {
      if (Current is null || stage >= Current.Stage) {
        Current = new StartupStageSnapshot(stage, message, false, DateTimeOffset.UnixEpoch);
      }
      await check.CheckAsync($"{stage}: {message}", cancellationToken);
    }

    public ValueTask SkipAsync(StartupStage stage, string reason, CancellationToken cancellationToken = default) {
      Current = new StartupStageSnapshot(stage, reason, true, DateTimeOffset.UnixEpoch);
      return ValueTask.CompletedTask;
    }

    public ValueTask InstanceRegisteredAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  private sealed class CheckingObserver(KeepaliveCheck check, ScriptedElector elector) : ISchemaInitializationObserver {
    public ValueTask OnSchemaStepCommittedAsync(string schema, string stepName, CancellationToken cancellationToken) =>
      check.CheckAsync("step " + stepName, cancellationToken);

    public ValueTask OnPreLockPhasesStartingAsync(string schema, CancellationToken cancellationToken) {
      lock (check.PreLockVotes) {
        check.PreLockVotes.Add(elector.Votes);
      }
      return check.CheckAsync(PRE_LOCK, cancellationToken);
    }
  }

  private async Task _driftAFrameworkMigrationAsync(CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      UPDATE wh_schema_migrations SET content_hash = 'forced-drift'
       WHERE file_name = (SELECT file_name FROM wh_schema_migrations WHERE owner = 'whizbang' ORDER BY file_name DESC LIMIT 1)
      """;
    await Assert.That(await cmd.ExecuteNonQueryAsync(ct)).IsEqualTo(1).Because("the pass has to have a step to run");
  }

  private static (IServiceProvider Services, KeepaliveCheck Check, FakeTimeProvider Time) _scope(
      Pod pod, ScriptedElector elector, LeasedGrant grant) {
    var time = new FakeTimeProvider();
    var check = new KeepaliveCheck(time, grant, elector);
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(pod);
    services.AddSingleton<IDutyElector>(elector);
    services.AddSingleton<TimeProvider>(time);
    services.AddSingleton<IStartupStageReporter>(new CheckingStageReporter(check));
    services.AddSingleton<ISchemaInitializationObserver>(new CheckingObserver(check, elector));
    return (services.BuildServiceProvider(), check, time);
  }

  [Test]
  [Timeout(180000)]
  public async Task EveryPhaseFromElectToTheEndOfMigrate_RunsUnderTheKeepalive_AndNothingRenewsAfterAsync(
      CancellationToken cancellationToken) {
    await _driftAFrameworkMigrationAsync(cancellationToken);
    var grant = new LeasedGrant();
    var elector = new ScriptedElector(grantedFromVote: 1, grant);
    var (services, check, time) = _scope(new Pod(), elector, grant);

    await using (var context = CreateDbContext()) {
      await context.EnsureWhizbangDatabaseInitializedAsync(null, null, services, cancellationToken);
    }

    await Assert.That(check.Checked).Contains(c => c.StartsWith("Elect:", StringComparison.Ordinal));
    await Assert.That(check.Checked).Contains(PRE_LOCK)
      .Because("the stored-form scans, rewrites and rebuild run while the keepalive holds the lease");
    await Assert.That(check.Checked).Contains(c => c.StartsWith("step ", StringComparison.Ordinal))
      .Because("each migration step runs while the keepalive holds the lease");
    await Assert.That(check.Checked).Contains(c => c.StartsWith("Converge:", StringComparison.Ordinal))
      .Because("the grant is held, and kept alive, until the initializer returns");

    await Assert.That(grant.Disposed).IsTrue();
    var verifies = grant.Count;
    time.Advance(TimeSpan.FromHours(1));
    await Assert.That(grant.Count).IsEqualTo(verifies)
      .Because("the keepalive stopped before the grant was released, so a released role is never renewed");
  }

  [Test]
  [Timeout(180000)]
  public async Task AWaiterWhoseMigratorIsGone_RunsThePreLockPhasesOnlyAfterAVoteGrantsItTheRoleAsync(
      CancellationToken cancellationToken) {
    await _driftAFrameworkMigrationAsync(cancellationToken);
    var grant = new LeasedGrant();
    // The first vote loses (another instance holds the role); that instance is then gone, with the role vacant.
    var elector = new ScriptedElector(grantedFromVote: 2, grant);
    var (services, check, _) = _scope(new Pod(), elector, grant);

    await using (var context = CreateDbContext()) {
      await context.EnsureWhizbangDatabaseInitializedAsync(null, null, services, cancellationToken);
    }

    await Assert.That(check.PreLockVotes).IsEquivalentTo([2])
      .Because("a waiter does the migrator's work only once it holds the migrator's role, never on a vacancy alone");
    await Assert.That(check.Checked).Contains(PRE_LOCK)
      .Because("the takeover's grant is kept alive like the first migrator's");
  }

  [Test]
  [Timeout(180000)]
  public async Task TwoReplicas_OnlyAnInstanceHoldingTheRoleRunsThePreLockPhasesAsync(CancellationToken cancellationToken) {
    await _driftAFrameworkMigrationAsync(cancellationToken);
    var pods = new[] { new Pod(), new Pod() };
    var logs = pods.Select(_ => new LineLogger()).ToArray();

    await using var first = CreateDbContext();
    await using var second = CreateDbContext();
    await Task.WhenAll(
      first.EnsureWhizbangDatabaseInitializedAsync(logs[0], null, _realScope(pods[0]), cancellationToken),
      second.EnsureWhizbangDatabaseInitializedAsync(logs[1], null, _realScope(pods[1]), cancellationToken));

    var ranPreLock = logs.Select(l => l.Has("holds the migrator role")).ToArray();
    var elected = logs.Select(l => l.Has("was elected to migrate")).ToArray();
    await Assert.That(ranPreLock.Count(r => r)).IsGreaterThanOrEqualTo(1);
    for (var i = 0; i < pods.Length; i++) {
      await Assert.That(!ranPreLock[i] || elected[i]).IsTrue()
        .Because("a replica runs the pre-lock phases only while it holds the migrator role");
    }
  }

  private IServiceProvider _realScope(Pod pod) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(pod);
    services.AddSingleton<IDutyElector>(new PgRoleElector(
      Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
      Options.Create(new RoleAssignmentOptions()),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
      pod,
      NullLogger<PgRoleElector>.Instance,
      libraryVersion: null));
    return services.BuildServiceProvider();
  }

  private sealed class LineLogger : ILogger {
    private readonly List<string> _lines = [];
    public bool Has(string text) {
      lock (_lines) {
        return _lines.Exists(l => l.Contains(text, StringComparison.Ordinal));
      }
    }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_lines) {
        _lines.Add(formatter(state, exception));
      }
    }
  }
}
