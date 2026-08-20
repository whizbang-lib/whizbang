using ECommerce.RabbitMQ.Integration.Tests.Fixtures;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ECommerce.RabbitMQ.Integration.Tests.Infrastructure;

/// <summary>
/// Guards the per-test database connection pool against being sized below what a Whizbang host
/// actually needs.
/// </summary>
/// <remarks>
/// <para>
/// A fixture host runs many workers concurrently against one database — PerspectiveWorker,
/// OutboxDrainWorker, InboxDrainWorker, IntegrityCheckpointWorker, FailureFlushWorker — plus the
/// shared LISTEN/NOTIFY connection, which is held open for the host's whole lifetime and never
/// returned to the pool. Npgsql pools are keyed by the exact connection string, so components that
/// share a string share one pool and one <c>MaxPoolSize</c> budget.
/// </para>
/// <para>
/// With the cap lifted, a single fixture host was measured settling at ~18 concurrent connections
/// against its database. The cap was 2. Anything needing a third concurrent connection from that
/// pool blocked in <c>OpenAsync</c> until the connection timeout elapsed and then threw
/// <see cref="TimeoutException"/> — which is exactly how this surfaced in CI, as
/// <c>CoordinatorConnectionScope.AcquireForEfCoreAsync</c> timeouts, a signal-bus doorbell probe
/// that could not round-trip within 5s (dropping every hop onto the polling fallback), and a
/// fixture warm-up dispatch that then could not finish inside its 60s budget. The visible symptom
/// was an unrelated-looking RabbitMQ sanity test failing in its <c>[Before(Test)]</c> hook.
/// </para>
/// <para>
/// This is deliberately a capacity assertion rather than a timeout bump: the pool must be able to
/// serve the concurrency the harness itself creates, otherwise the suite only passes when timing
/// happens to be kind.
/// </para>
/// </remarks>
public sealed class PerTestDatabasePoolCapacityTests {
  /// <summary>
  /// Concurrent connections one fixture host was observed using once the cap no longer bound it.
  /// </summary>
  private const int OBSERVED_HOST_CONNECTION_DEMAND = 18;

  [Test]
  [Timeout(180000)]
  public async Task PerTestConnectionString_ServesOneHostsConcurrentDemandAsync(CancellationToken cancellationToken) {
    await SharedRabbitMqFixtureSource.InitializeAsync(cancellationToken);

    var connectionString = SharedRabbitMqFixtureSource.GetPerTestDatabaseConnectionString();
    var databaseName = new NpgsqlConnectionStringBuilder(connectionString).Database!;
    await _createDatabaseAsync(databaseName, cancellationToken);

    var opened = new List<NpgsqlConnection>();
    try {
      // Open the host's full demand at once, the way its workers do. Each open reports success or
      // failure rather than throwing, so a starved pool is reported as a count instead of a bare
      // TimeoutException — the count is the diagnostic that names the cause.
      var connections = await Task.WhenAll(
        Enumerable.Range(0, OBSERVED_HOST_CONNECTION_DEMAND).Select(async _ => {
          var connection = new NpgsqlConnection(connectionString);
          try {
            await connection.OpenAsync(cancellationToken);
            return connection;
          } catch (Exception) {
            await connection.DisposeAsync();
            return null;
          }
        }));

      opened.AddRange(connections.Where(c => c is not null)!);

      await Assert.That(opened.Count).IsEqualTo(OBSERVED_HOST_CONNECTION_DEMAND)
        .Because($"the per-test pool must serve the {OBSERVED_HOST_CONNECTION_DEMAND} concurrent "
               + "connections a fixture host actually opens; a smaller cap starves the workers and "
               + "surfaces as OpenAsync timeouts, doorbell self-test failure, and warm-up timeouts");
    } finally {
      foreach (var connection in opened) {
        await connection.DisposeAsync();
      }
      await _dropDatabaseAsync(databaseName);
    }
  }

  private static async Task _createDatabaseAsync(string databaseName, CancellationToken ct) {
    await using var admin = new NpgsqlConnection(SharedRabbitMqFixtureSource.PostgresConnectionString);
    await admin.OpenAsync(ct);
    await using var command = admin.CreateCommand();
    command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
    await command.ExecuteNonQueryAsync(ct);
  }

  private static async Task _dropDatabaseAsync(string databaseName) {
    try {
      await using var admin = new NpgsqlConnection(SharedRabbitMqFixtureSource.PostgresConnectionString);
      await admin.OpenAsync(CancellationToken.None);
      await using var command = admin.CreateCommand();
      command.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
      await command.ExecuteNonQueryAsync(CancellationToken.None);
    } catch (PostgresException) {
      // Best-effort teardown; the container is disposable and per-test databases are unique.
    }
  }
}
