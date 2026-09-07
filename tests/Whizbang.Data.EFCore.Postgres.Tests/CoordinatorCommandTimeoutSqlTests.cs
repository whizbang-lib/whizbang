using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The coordinator must own the timeout of its own SQL. Its raw commands used to inherit the consumer's
/// connection-string <c>Command Timeout</c> (30 s in one consumer, 5 s for Dapper consumers by default),
/// and under a bulk-import backlog the commit batch's 13-30 s tail crossed it: the batch was cancelled, its
/// completions were dropped, the rows re-claimed as lease expiries, and the poison gate throttled the drain
/// to one row per cycle. <see cref="CoordinatorCommandExtensions.WithCoordinatorTimeout{TCommand}"/> is
/// what every coordinator command creation site composes with.
/// </summary>
[Category("Shard1")]
public class CoordinatorCommandTimeoutSqlTests : EFCoreTestBase {
  private NpgsqlConnection _openWithConnectionStringTimeout(int seconds) {
    var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { CommandTimeout = seconds };
    return new NpgsqlConnection(builder.ConnectionString);
  }

  [Test]
  public async Task WithCoordinatorTimeout_OverridesTheConnectionStringTimeoutAsync() {
    await using var conn = _openWithConnectionStringTimeout(1);
    await conn.OpenAsync();

    await using var plain = conn.CreateCommand();
    await using var owned = conn.CreateCommand().WithCoordinatorTimeout();

    await Assert.That(plain.CommandTimeout).IsEqualTo(1)
      .Because("a raw command inherits the connection string - that is the hole being closed");
    await Assert.That(owned.CommandTimeout).IsEqualTo(CoordinatorCommandExtensions.COORDINATOR_COMMAND_TIMEOUT_SECONDS);
  }

  [Test]
  public async Task WithCoordinatorTimeout_LetsACoordinatorCommandOutlastAShortConnectionStringTimeoutAsync() {
    await using var conn = _openWithConnectionStringTimeout(1);
    await conn.OpenAsync();

    Exception? plainFailure = null;
    try {
      await using var plain = conn.CreateCommand();
      plain.CommandText = "SELECT pg_sleep(2)";
      await plain.ExecuteNonQueryAsync();
    } catch (Exception ex) {
      plainFailure = ex;
    }
    await Assert.That(plainFailure).IsNotNull()
      .Because("with a 1 s connection-string timeout a 2 s command is cancelled - the consumer's setting reaches the framework's SQL");

    await using var owned = conn.CreateCommand().WithCoordinatorTimeout();
    owned.CommandText = "SELECT pg_sleep(2)";
    await owned.ExecuteNonQueryAsync();
  }
}
