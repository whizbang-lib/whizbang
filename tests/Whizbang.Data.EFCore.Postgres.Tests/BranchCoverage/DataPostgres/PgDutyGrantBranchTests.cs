// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.DataPostgres;

/// <summary>
/// A duty grant that has already found its session gone answers every later "still held?" from
/// memory. Asking the dead connection again would log the loss again on every check of a long-running
/// duty loop, and could only ever answer no.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgDutyElector.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class PgDutyGrantBranchTests : EFCoreTestBase {

  [Test]
  [Timeout(60000)]
  public async Task VerifyStillHeld_AfterTheLossWasDetected_AnswersFromMemoryAsync(CancellationToken cancellationToken) {
    var instance = new ServiceInstanceProvider(Guid.NewGuid(), "grant-svc", "grant-host", processId: 1);
    await using (var ctx = CreateDbContext()) {
      var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
      await coordinator.RecordHeartbeatAsync(
        new HeartbeatRequest(instance.InstanceId, instance.ServiceName, instance.HostName, 1), cancellationToken);
    }
    var logger = new GrantLossLogger();
    var elector = new PgDutyElector(
      Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
      instance,
      logger);
    var attempt = await elector.TryAcquireAsync("branch-grant-lost", cancellationToken);
    await Assert.That(attempt.Grant).IsNotNull();

    // End the holder's session from outside, as a crash or failover would.
    await using (var admin = new NpgsqlConnection(ConnectionString)) {
      await admin.OpenAsync(cancellationToken);
      await using var kill = new NpgsqlCommand(
        "SELECT pg_terminate_backend(pid) FROM pg_stat_activity "
        + "WHERE datname = current_database() AND pid <> pg_backend_pid()", admin);
      _ = await kill.ExecuteNonQueryAsync(cancellationToken);
    }

    var first = await attempt.Grant!.VerifyStillHeldAsync(cancellationToken);
    var lossesAfterFirst = logger.WarningOrAboveCount;
    var second = await attempt.Grant.VerifyStillHeldAsync(cancellationToken);

    await Assert.That(first).IsFalse();
    await Assert.That(second).IsFalse();
    await Assert.That(lossesAfterFirst).IsGreaterThanOrEqualTo(1)
      .Because("the first check finds the session gone and reports the loss");
    await Assert.That(logger.WarningOrAboveCount).IsEqualTo(lossesAfterFirst)
      .Because("the second check must not touch the dead connection, so it reports nothing new");

    await attempt.Grant.DisposeAsync();
  }

  private sealed class GrantLossLogger : ILogger<PgDutyElector> {
    private int _warnings;

    public int WarningOrAboveCount => Volatile.Read(ref _warnings);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel >= LogLevel.Warning) {
        Interlocked.Increment(ref _warnings);
      }
    }
  }
}
