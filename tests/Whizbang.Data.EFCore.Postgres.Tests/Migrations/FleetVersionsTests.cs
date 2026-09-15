using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Which other releases are alive in the fleet, as the instance registry reports them.
/// </summary>
/// <remarks>
/// <para>
/// A rewrite that changes a stored unit is not safe under a mixed fleet: an older instance keeps
/// writing the old unit into a table the ledger already says is converted, and nothing can tell
/// those rows apart afterward. The migrator cannot refuse to run, because under a rolling update
/// the older instances stay until the newer ones are ready and the newer ones are not ready until
/// the migration finishes. What it can do is say so, loudly, naming the releases it saw.
/// </para>
/// <para>
/// The registry is what every instance heartbeats into; a live instance is one that has done so
/// within the window, and its release is what its heartbeat's metadata says. This instance's own
/// row is ignored, and so is any instance on the same release.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/FleetVersions.cs</code-under-test>
[Category("Integration")]
[Category("Shard4")]
public class FleetVersionsTests : IAsyncDisposable {
  private static readonly Guid _self = Guid.CreateVersion7();
  private static readonly TimeSpan _window = TimeSpan.FromMinutes(2);

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = $"fleet_{Guid.NewGuid():N}";
    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await using var create = new NpgsqlCommand($"CREATE DATABASE {_databaseName}", admin);
      await create.ExecuteNonQueryAsync();
    }
    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
    }.ConnectionString;

    // The bootstrap subset is exactly the registry and the heartbeat function, plus what they need.
    foreach (var (_, sql) in WorkCoordinationDbContextSchemaExtensions.GetBootstrapScripts()) {
      await _executeAsync(sql);
    }
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    if (_databaseName is not null) {
      try {
        await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
      } catch (NpgsqlException) {
        // The container is torn down with the run; a database left behind costs nothing.
      }
    }
    GC.SuppressFinalize(this);
  }

  private async Task _executeAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  /// <summary>Heartbeats an instance in, with the release its heartbeat would carry.</summary>
  private async Task _heartbeatAsync(Guid instance, string? version, TimeSpan age) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(
      "SELECT public.register_instance_heartbeat($1, 'svc', 'host', 1, $2::jsonb, now() - $3, now() + interval '60 seconds')", db);
    command.Parameters.AddWithValue(instance);
    command.Parameters.AddWithValue(version is null ? DBNull.Value : $$"""{"Version":"{{version}}"}""");
    command.Parameters.AddWithValue(age);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<IReadOnlyList<string>> _othersAsync() {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    return await FleetVersions.OtherLiveVersionsAsync(db, "public", _self, "2.0.0", _window);
  }

  /// <summary>An older release still heartbeating is reported, once per release.</summary>
  [Test]
  public async Task AnOlderReleaseStillAliveIsReportedAsync() {
    await _heartbeatAsync(_self, "2.0.0", TimeSpan.Zero);
    await _heartbeatAsync(Guid.CreateVersion7(), "1.9.0", TimeSpan.FromSeconds(10));
    await _heartbeatAsync(Guid.CreateVersion7(), "1.9.0", TimeSpan.FromSeconds(20));
    await _heartbeatAsync(Guid.CreateVersion7(), "1.8.0", TimeSpan.FromSeconds(5));

    var others = await _othersAsync();

    await Assert.That(string.Join(",", others)).IsEqualTo("1.8.0,1.9.0")
      .Because("each other release once, in order, is what an operator needs to read");
  }

  /// <summary>This instance and the instances on the same release are not "other".</summary>
  [Test]
  public async Task TheSameReleaseIsNotReportedAsync() {
    await _heartbeatAsync(_self, "2.0.0", TimeSpan.Zero);
    await _heartbeatAsync(Guid.CreateVersion7(), "2.0.0", TimeSpan.FromSeconds(10));

    await Assert.That((await _othersAsync()).Count).IsEqualTo(0);
  }

  /// <summary>An instance whose last heartbeat is older than the window is gone, not other.</summary>
  [Test]
  public async Task AStaleInstanceIsNotReportedAsync() {
    await _heartbeatAsync(Guid.CreateVersion7(), "1.9.0", _window + TimeSpan.FromSeconds(30));

    await Assert.That((await _othersAsync()).Count).IsEqualTo(0)
      .Because("a row nobody has refreshed within the window is an instance that is no longer there");
  }

  /// <summary>An instance that recorded no release is reported as unknown, because it is not this one.</summary>
  [Test]
  public async Task AnInstanceWithNoRecordedReleaseIsReportedAsUnknownAsync() {
    await _heartbeatAsync(Guid.CreateVersion7(), null, TimeSpan.FromSeconds(10));

    await Assert.That(string.Join(",", await _othersAsync())).IsEqualTo("unknown");
  }

  /// <summary>A registry that is not there yet answers nothing rather than failing the migrator.</summary>
  [Test]
  public async Task AMissingRegistryAnswersNothingAsync() {
    await _executeAsync("DROP TABLE public.wh_service_instances CASCADE");

    await Assert.That((await _othersAsync()).Count).IsEqualTo(0);
  }
}
