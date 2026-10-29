// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The stream lifecycle ledger (#1732) on the Dapper driver: a direct append keeps the stream's row, and the driver's
/// maintenance step drives the one-time backfill. The SQL itself is covered by the EF Core
/// <c>StreamLifecycleLedgerSqlTests</c> and <c>StreamLifecycleBackfillSqlTests</c>; these lock this driver's side.
/// </summary>
/// <docs>fundamentals/events/stream-end-of-life#the-stream-lifecycle-ledger</docs>
public class DapperStreamLifecycleLedgerTests : PostgresTestBase {
  private const string VERSION_KEY = "stream_lifecycle_backfill_version";
  private const string CURSOR_KEY = "stream_lifecycle_backfill_cursor";

  [Test]
  public async Task AppendAsync_CreatesAndAdvancesTheStreamsRowAsync() {
    var store = _eventStore();
    var stream = (Guid)TrackedGuid.New();

    await store.AppendAsync(stream, new DapperMixedStreamProbeNoted { StreamId = stream });
    await store.AppendAsync(stream, new DapperMixedStreamProbeNoted { StreamId = stream });

    await using var conn = await _openAsync();
    var lastVersion = await conn.ExecuteScalarAsync<long?>(
      "SELECT last_version FROM wh_stream_lifecycle WHERE stream_id = @stream", new { stream });
    var maxVersion = await conn.ExecuteScalarAsync<long>("SELECT MAX(version) FROM wh_event_store WHERE stream_id = @stream", new { stream });
    await Assert.That(lastVersion).IsEqualTo(maxVersion).Because("the Dapper store appends through wh_append_event, which keeps the ledger");
  }

  [Test]
  public async Task AppendAsync_OfAnEphemeralEvent_CreatesNoRowAsync() {
    var store = _eventStore();
    var stream = (Guid)TrackedGuid.New();

    await store.AppendAsync(stream, new DapperMixedStreamProbeTyping { StreamId = stream });

    await using var conn = await _openAsync();
    await Assert.That(await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_stream_lifecycle WHERE stream_id = @stream", new { stream }))
      .IsEqualTo(0L);
  }

  [Test]
  public async Task TheStep_OnAnUpgradedStore_SeedsEveryStream_AndRecordsItIsDoneAsync() {
    await using var conn = await _openAsync();
    await _seedStreamsAsync(conn, 3);
    await _restartAsync(conn);

    await new DapperStreamLifecycleBackfillMaintenanceStep(ConnectionString, batchSize: 2)
      .RunAsync(new ServiceCollection().BuildServiceProvider(), CancellationToken.None);

    await Assert.That(await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_stream_lifecycle")).IsEqualTo(3L);
    await Assert.That(await conn.ExecuteScalarAsync<string>(
      "SELECT setting_value FROM wh_settings WHERE setting_key = @k", new { k = VERSION_KEY })).IsEqualTo("1");
  }

  [Test]
  public async Task TheStep_StopsAtItsRoundLimitAsync() {
    await using var conn = await _openAsync();
    await _seedStreamsAsync(conn, 3);
    await _restartAsync(conn);

    await new DapperStreamLifecycleBackfillMaintenanceStep(ConnectionString, NullLogger<DapperStreamLifecycleBackfillMaintenanceStep>.Instance, batchSize: 1, maxRoundsPerRun: 1)
      .RunAsync(new ServiceCollection().BuildServiceProvider(), CancellationToken.None);

    await Assert.That(await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_stream_lifecycle")).IsEqualTo(1L);
  }

  [Test]
  public async Task TheStep_RefusesWhatItCannotRunAsync() {
    await Assert.That(() => new DapperStreamLifecycleBackfillMaintenanceStep(" ")).Throws<ArgumentException>();
    await Assert.That(async () => await new DapperStreamLifecycleBackfillMaintenanceStep(ConnectionString)
      .RunAsync(null!, CancellationToken.None)).Throws<ArgumentNullException>();
  }

  /// <summary>Both registrations of the Dapper driver add the step.</summary>
  [Test]
  public async Task AddWhizbangPostgres_RegistersTheStepAsync() {
    var withSql = new ServiceCollection().AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), false, (string?)null, null);
    var withEntries = new ServiceCollection().AddWhizbangPostgres(
      ConnectionString, new JsonSerializerOptions(), false, Array.Empty<KeyValuePair<string, string>>(), null);

    foreach (var services in new[] { withSql, withEntries }) {
      await using var provider = services.BuildServiceProvider();
      await using var scope = provider.CreateAsyncScope();
      var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<DapperStreamLifecycleBackfillMaintenanceStep>().ToList();
      await Assert.That(steps.Count).IsEqualTo(1);
      await Assert.That(steps.Single().Name).IsEqualTo("stream-lifecycle-backfill");
    }
  }

  // ===== Helpers =====

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private DapperPostgresEventStore _eventStore() {
    var jsonOptions = Whizbang.Data.Dapper.Postgres.Tests.Generated.WhizbangJsonContext.CreateOptions();
    return new DapperPostgresEventStore(
      ConnectionFactory,
      Executor,
      jsonOptions,
      new EventEnvelopeJsonbAdapter(jsonOptions),
      new JsonbSizeValidator(NullLogger<JsonbSizeValidator>.Instance),
      new PolicyEngine(),
      null,
      NullLogger<DapperPostgresEventStore>.Instance);
  }

  private static async Task _seedStreamsAsync(NpgsqlConnection conn, int count) {
    for (var i = 0; i < count; i++) {
      var id = (Guid)TrackedGuid.New();
      await conn.ExecuteAsync("""
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at, flags)
        VALUES (@id, @stream, @stream, 'Agg.Dapper', 'Agg.Dapper, Tests', 1, @at, 0);
        INSERT INTO wh_event_body (event_id, event_data, metadata) VALUES (@id, '{"a": 1}'::jsonb, '{}'::jsonb);
        """, new { id, stream = (Guid)TrackedGuid.New(), at = new DateTimeOffset(2026, 4, 1, 9, i, 0, TimeSpan.Zero) });
    }
  }

  private static Task<int> _restartAsync(NpgsqlConnection conn) =>
    conn.ExecuteAsync($"""
      UPDATE wh_settings SET setting_value = '0' WHERE setting_key = '{VERSION_KEY}';
      UPDATE wh_settings SET setting_value = '{Guid.Empty}' WHERE setting_key = '{CURSOR_KEY}';
      """);
}
