// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
      await Assert.That(provider.GetRequiredService<IFanoutChildRefiler>()).IsTypeOf<PostgresFanoutChildRefiler>();
      await Assert.That(provider.GetRequiredService<IReceivedStreamResequencer>()).IsTypeOf<PostgresReceivedStreamResequencer>();
    }
  }

  /// <summary>The composite-child repair (#1798) runs on a Dapper-migrated database through the driver's registration.</summary>
  [Test]
  public async Task Refile_OnADapperMigratedDatabase_RunsThroughTheDriversRegistrationAsync() {
    var services = new ServiceCollection().AddLogging();
    services.AddWhizbangPostgres(ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: false, _noEntries, configureOptions: null);
    await using var provider = services.BuildServiceProvider();

    var report = await provider.GetRequiredService<IFanoutChildRefiler>().RefileAsync(new FanoutChildRefileRequest {
      RequestedBy = "operator-1",
      Reason = "checking first",
      DryRun = true,
    });

    await Assert.That(report.Scanned).IsEqualTo(0).Because("a fresh database stores no composite children");
    await Assert.That(report.Moves).IsEmpty();
  }

  /// <summary>
  /// The received-stream re-sequence (#1629) runs on a Dapper-migrated database through the driver's registration. A
  /// Dapper read carries no origin, so a replay there folds in stored order: the re-sequence is what puts it right.
  /// </summary>
  [Test]
  public async Task Resequence_OnADapperMigratedDatabase_StoresAReceivedStreamInItsOriginsOrderAsync() {
    var services = new ServiceCollection().AddLogging();
    services.AddWhizbangPostgres(ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: false, _noEntries, configureOptions: null);
    await using var provider = services.BuildServiceProvider();
    var stream = Whizbang.Core.ValueObjects.TrackedGuid.New().Value;
    var origin = Whizbang.Core.ValueObjects.TrackedGuid.New().Value;
    Guid[] ids = [.. new[] { Whizbang.Core.ValueObjects.TrackedGuid.New().Value, Whizbang.Core.ValueObjects.TrackedGuid.New().Value }
      .OrderBy(g => g.ToString("D"), StringComparer.Ordinal)];
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    for (var i = 0; i < 2; i++) {
      // Stored backward: the lower id (minted first by the origin) at the later version.
      await connection.ExecuteAsync("""
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at, origin_service_id)
        VALUES (@EventId, @StreamId, @StreamId, 'Resequence.Tests', 'Resequence.Tests.Happened', @Version, NOW(), @Origin)
        """, new { EventId = ids[i], StreamId = stream, Version = 2 - i, Origin = origin });
    }

    var report = await provider.GetRequiredService<IReceivedStreamResequencer>().ResequenceAsync(new ReceivedStreamResequenceRequest {
      RequestedBy = "operator-1",
      Reason = "streams stored before the receive-order fix",
    });

    await Assert.That(report.Resequenced.Select(s => s.StreamId)).IsEquivalentTo([stream]);
    var order = (await connection.QueryAsync<Guid>(
      "SELECT event_id FROM wh_event_store WHERE stream_id = @StreamId ORDER BY version", new { StreamId = stream })).ToArray();
    await Assert.That(order).IsEquivalentTo(ids, TUnit.Assertions.Enums.CollectionOrdering.Matching);
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
