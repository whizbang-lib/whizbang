using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper driver's purge markers (#1027) and operator stream purge (#1030): both <c>AddWhizbangPostgres</c>
/// overloads register the shared Postgres implementations, and they work on a database the Dapper runner migrated.
/// </summary>
/// <docs>operations/infrastructure/purging-streams</docs>
public class DapperStreamPurgeTests : PostgresTestBase {
  private static readonly KeyValuePair<string, string>[] _noEntries = [];

  [Test]
  public async Task AddWhizbangPostgres_RegistersPurgeMarkersAndStreamPurger_BothOverloadsAsync() {
    var entries = new ServiceCollection().AddLogging();
    entries.AddWhizbangPostgres(ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: false, _noEntries, configureOptions: null);
    var schemaSql = new ServiceCollection().AddLogging();
    schemaSql.AddWhizbangPostgres(ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: false, perspectiveSchemaSql: null, configureOptions: null);

    foreach (var services in new[] { entries, schemaSql }) {
      await using var provider = services.BuildServiceProvider();
      await Assert.That(provider.GetRequiredService<IPerspectivePurgeMarkerStore>()).IsTypeOf<PostgresPerspectivePurgeMarkerStore>();
      await Assert.That(provider.GetRequiredService<IStreamPurger>()).IsTypeOf<PostgresStreamPurger>();
    }
  }

  [Test]
  public async Task Purge_OnADapperMigratedDatabase_RemovesTheStream_AndMarksItAsync() {
    var services = new ServiceCollection().AddLogging();
    services.AddWhizbangPostgres(ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: false, _noEntries, configureOptions: null);
    await using var provider = services.BuildServiceProvider();
    var stream = Guid.CreateVersion7();
    var other = Guid.CreateVersion7();
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    foreach (var id in new[] { stream, other }) {
      await connection.ExecuteAsync("""
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
        VALUES (@EventId, @StreamId, @StreamId, 'Purge.Tests', 'Purge.Tests.Happened', 1, NOW())
        """, new { EventId = Guid.CreateVersion7(), StreamId = id });
    }

    var dryRun = await provider.GetRequiredService<IStreamPurger>().PurgeAsync(new StreamPurgeRequest {
      StreamIds = [stream],
      RequestedBy = "operator-1",
      Reason = "orphaned",
      DryRun = true,
    });
    var report = await provider.GetRequiredService<IStreamPurger>().PurgeAsync(new StreamPurgeRequest {
      StreamIds = [stream],
      RequestedBy = "operator-1",
      Reason = "orphaned",
    });

    await Assert.That(dryRun.Totals["wh_event_store"]).IsEqualTo(1);
    await Assert.That(report.Totals["wh_event_store"]).IsEqualTo(1);
    await Assert.That(await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_event_store WHERE stream_id = @stream", new { stream }))
      .IsEqualTo(0);
    await Assert.That(await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_event_store WHERE stream_id = @other", new { other }))
      .IsEqualTo(1);
    var markers = provider.GetRequiredService<IPerspectivePurgeMarkerStore>();
    await Assert.That(await markers.IsPurgedAsync(stream, "any_perspective")).IsTrue();
    await Assert.That(await markers.IsPurgedAsync(other, "any_perspective")).IsFalse();
    await markers.MarkPurgedAsync(other, "a_perspective", purgeEventId: null);
    await Assert.That(await markers.IsPurgedAsync(other, "a_perspective")).IsTrue();
    await markers.ClearAsync(other, "a_perspective");
    await Assert.That(await markers.IsPurgedAsync(other, "a_perspective")).IsFalse();
  }
}
