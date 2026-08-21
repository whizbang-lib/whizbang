using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.RunControl;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for the Postgres event-store managed-resource health source (wired in
/// <see cref="PostgresDriverExtensions"/> as an <see cref="ConnectivityRequirement.AlwaysRequired"/>
/// <see cref="ConnectivityHealthSource"/> over a <c>SELECT 1</c> probe). Verifies the real probe against
/// live PostgreSQL, and locks the key invariant: a DB fault is <see cref="ComponentState.Faulted"/> even
/// during a migration — never masked — which is why a consumer can delete its naive readiness check.
/// </summary>
/// <tests>src/Whizbang.Data.EFCore.Postgres/PostgresDriverExtensions.cs</tests>
[Category("Integration")]
[Category("Shard4")]
public class EventStoreHealthSourceIntegrationTests : EFCoreTestBase {

  private sealed class FakeLifecycle(LifecyclePhase phase) : IWhizbangLifecycleState {
    public LifecyclePhase Phase { get; } = phase;
    public ValueTask AdvanceToAsync(LifecyclePhase p, CancellationToken cancellationToken) => default;
    public ValueTask FaultAsync(CancellationToken cancellationToken) => default;
  }

  // Mirrors the driver's SELECT 1 probe (PostgresDriverExtensions._pingEventStoreAsync) so this
  // exercises the exact AlwaysRequired wiring against live Postgres.
  private static async ValueTask<bool> _pingAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken) {
    await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT 1";
    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    return true;
  }

  private static ConnectivityHealthSource _source(NpgsqlDataSource dataSource, LifecyclePhase phase)
    => ConnectivityHealthSource.AlwaysRequired(
        "event-store",
        ct => _pingAsync(dataSource, ct),
        new FakeLifecycle(phase),
        "event-store database unreachable");

  [Test]
  public async Task Reachable_WhileRunning_ReportsOperationalAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var health = await _source(dataSource, LifecyclePhase.Running).ReportAsync(CancellationToken.None);
    await Assert.That(health.State).IsEqualTo(ComponentState.Operational);
  }

  [Test]
  public async Task Reachable_WhileMigrating_ReportsOperationalAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var health = await _source(dataSource, LifecyclePhase.Migrating).ReportAsync(CancellationToken.None);
    await Assert.That(health.State).IsEqualTo(ComponentState.Operational);
  }

  [Test]
  public async Task Unreachable_WhileMigrating_ReportsFaulted_NotMaskedAsync() {
    // A dead DB mid-migration is real — the migration needs it — so it must surface, not read healthy.
    await using var dataSource = NpgsqlDataSource.Create(
      "Host=127.0.0.1;Port=1;Username=none;Password=none;Database=none;Timeout=2;Command Timeout=2");
    var health = await _source(dataSource, LifecyclePhase.Migrating).ReportAsync(CancellationToken.None);
    await Assert.That(health.State).IsEqualTo(ComponentState.Faulted);
    await Assert.That(health.Detail).IsEqualTo("event-store database unreachable");
  }
}
