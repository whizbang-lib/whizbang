#pragma warning disable CA1707

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The row guard end to end: a real maintenance cycle against a real database, with a guard that
/// cleans external resources (a simulated blob store keyed by the row payload) before its rows
/// die. One cycle: the cleanable row's blob is deleted and its row destroyed; the row whose blob
/// delete failed is deferred and survives. Next cycle: the outage is over, the deferred row is
/// re-offered, its blob cleaned, its row destroyed. The invariant throughout: the row outlives
/// the external resource, never the reverse.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>proposals/pre-destruction-seam</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class PerspectiveRowGuardEndToEndTests : EFCoreTestBase {
  private const string TABLE = "wh_per_guard_e2e";
  private const string CLR_TYPE = "Whizbang.Data.EFCore.Postgres.Tests.PerspectiveRowGuardEndToEndTests+GuardedE2EModel";

  private sealed class GuardedE2EModel;

  /// <summary>The simulated external store: blobName → exists, with a togglable outage.</summary>
  private sealed class FakeBlobStore {
    public HashSet<string> Blobs { get; } = [];
    public HashSet<string> OutageKeys { get; } = [];

    public bool TryDelete(string blobName) {
      if (OutageKeys.Contains(blobName)) {
        return false;
      }
      Blobs.Remove(blobName);
      return true;
    }
  }

  /// <summary>
  /// The consumer-shaped guard: reads the blob name from each offered row's payload, deletes the
  /// blob, and proceeds only rows whose blob is VERIFIABLY gone — everything else defers for a
  /// retry backoff (the hold survives this cycle's sweep; the test ages it to cross cycles).
  /// </summary>
  private sealed class BlobCleanupGuard(FakeBlobStore store) : IPerspectiveRowDestructionGuard {
    public IReadOnlyCollection<Type> GuardedModels => [typeof(GuardedE2EModel)];

    public ValueTask<IReadOnlyDictionary<Guid, PerspectiveRowDecision>> OnBeforeReapAsync(
        IReadOnlyList<PerspectiveRowDestructionTarget> targets, CancellationToken cancellationToken = default) {
      var decisions = new Dictionary<Guid, PerspectiveRowDecision>();
      foreach (var target in targets) {
        var blobName = target.Data.GetProperty("blobName").GetString()!;
        decisions[target.RowId] = store.TryDelete(blobName)
          ? PerspectiveRowDecision.Proceed()
          : PerspectiveRowDecision.Defer(DateTimeOffset.UtcNow.AddHours(1));
      }
      return ValueTask.FromResult<IReadOnlyDictionary<Guid, PerspectiveRowDecision>>(decisions);
    }
  }

  private MaintenanceWorker _buildWorker(FakeBlobStore store) {
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    services.AddScoped<IWorkCoordinator>(sp => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      sp.GetRequiredService<WorkCoordinationDbContext>(),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IPerspectiveRowDestructionGuard>(new BlobCleanupGuard(store));
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      NullLogger<MaintenanceWorker>.Instance);
  }

  [Test]
  [Timeout(120000)]
  public async Task GuardedRetention_CleansTheResourceBeforeTheRowDies_EndToEndAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);

    // The perspective exists, is enrolled, and adoption follows the real flow: preview → acknowledge.
    await using (var ddl = new NpgsqlCommand($@"
      CREATE TABLE {TABLE} (
        id UUID NOT NULL PRIMARY KEY,
        data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
        sys_created_at TIMESTAMPTZ, sys_updated_at TIMESTAMPTZ,
        expires_at TIMESTAMPTZ, version INTEGER NOT NULL);
      INSERT INTO wh_perspective_registry
        (clr_type_name, table_name, schema_json, schema_hash, service_name, row_retention_enrolled, row_ttl_seconds)
      VALUES ('{CLR_TYPE}', '{TABLE}', '{{}}'::jsonb, 'h', 'svc', TRUE, 3600);", conn)) {
      await ddl.ExecuteNonQueryAsync(cancellationToken);
    }
    var cleanable = Guid.NewGuid();
    var outage = Guid.NewGuid();
    foreach (var (id, blob) in new[] { (cleanable, "blob-a"), (outage, "blob-b") }) {
      await using var seed = new NpgsqlCommand($@"
        INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, version)
        VALUES (@id, jsonb_build_object('blobName', @b), '{{}}'::jsonb, '{{}}'::jsonb,
                NOW() - INTERVAL '2 days', NOW() - INTERVAL '2 days', 1)", conn);
      seed.Parameters.AddWithValue("id", id);
      seed.Parameters.AddWithValue("b", blob);
      await seed.ExecuteNonQueryAsync(cancellationToken);
    }

    var store = new FakeBlobStore { Blobs = { "blob-a", "blob-b" }, OutageKeys = { "blob-b" } };
    var worker = _buildWorker(store);
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    // Adoption flow: the backlog preview is the number an operator reads before acknowledging.
    var backlog = await coordinator.CountPerspectiveRetentionBacklogAsync(CLR_TYPE, cancellationToken);
    await Assert.That(backlog).IsEqualTo(2L);
    await coordinator.AcknowledgeRetentionEnforcementAsync(CLR_TYPE, cancellationToken);

    // Cycle 1: blob-a cleans and its row dies; blob-b's delete fails, so its row SURVIVES.
    await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker, cancellationToken);

    await Assert.That(await _survivesAsync(conn, cleanable)).IsFalse()
      .Because("the guard verified blob-a gone, so the sweep took the row in the same cycle");
    await Assert.That(store.Blobs).DoesNotContain("blob-a");
    await Assert.That(await _survivesAsync(conn, outage)).IsTrue()
      .Because("blob-b's delete failed — the row must outlive the blob, never the reverse");
    await Assert.That(store.Blobs).Contains("blob-b");

    // The outage ends. Age the hold to the past (the deterministic stand-in for waiting out the
    // backoff) so the next cycle re-offers the row.
    store.OutageKeys.Clear();
    await using (var age = new NpgsqlCommand(
        "UPDATE wh_perspective_row_hold SET hold_until = NOW() - INTERVAL '1 second' WHERE table_name = @t", conn)) {
      age.Parameters.AddWithValue("t", TABLE);
      await age.ExecuteNonQueryAsync(cancellationToken);
    }
    await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker, cancellationToken);

    await Assert.That(await _survivesAsync(conn, outage)).IsFalse()
      .Because("re-offered after the hold lapsed, blob-b cleaned and verified, row destroyed — convergence");
    await Assert.That(store.Blobs).DoesNotContain("blob-b");
  }

  private static async Task<bool> _survivesAsync(NpgsqlConnection conn, Guid id) {
    await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {TABLE} WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    return Convert.ToInt64(
      await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0;
  }
}
