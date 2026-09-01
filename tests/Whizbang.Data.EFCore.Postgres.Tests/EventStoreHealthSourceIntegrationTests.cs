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

  // Drives the driver's OWN probe rather than a copy of it. This file used to keep a local
  // reimplementation of the SELECT 1, which meant a change to the real probe — a different
  // statement, a different failure shape — would leave these tests passing against the old one.
  private static ConnectivityHealthSource _source(NpgsqlDataSource dataSource, LifecyclePhase phase)
    => ConnectivityHealthSource.AlwaysRequired(
        "event-store",
        ct => PostgresDriverExtensions.PingEventStoreAsync(dataSource, ct),
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

  [Test]
  public async Task TheProbeAnswersAgainstALiveDatabaseAsync() {
    // The probe itself, not the health wrapper around it: a SELECT 1 through the same data
    // source the rest of the driver uses.
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);

    var reachable = await PostgresDriverExtensions.PingEventStoreAsync(dataSource, CancellationToken.None);

    await Assert.That(reachable).IsTrue();
  }

  [Test]
  public async Task TheProbeFailsRatherThanHangingWhenTheServerIsUnreachableAsync() {
    // The probe is deliberately a bare SELECT 1 rather than a count, so a migration in flight
    // cannot make it time out. What it must not do is swallow a genuine connection failure —
    // returning true there would report an unreachable database as healthy.
    await using var dataSource = NpgsqlDataSource.Create(
      "Host=127.0.0.1;Port=1;Username=nobody;Password=nobody;Database=nothing;Timeout=1");

    await Assert.That(async () =>
      await PostgresDriverExtensions.PingEventStoreAsync(dataSource, CancellationToken.None))
      .ThrowsException()
      .Because("a swallowed connection failure would report an unreachable database as healthy");
  }

  [Test]
  public async Task TheProbeHonorsCancellationAsync() {
    // Health probes run on a timer during shutdown too; one that ignored its token would hold
    // the host open on a connection attempt.
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    await Assert.That(async () =>
      await PostgresDriverExtensions.PingEventStoreAsync(dataSource, cts.Token))
      .ThrowsException();
  }
}
