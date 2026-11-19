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
using Whizbang.Core.Archival;
using Whizbang.Core.Data;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Dapper.Custom;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The scheduled disposition of sealed streams and the NewGeneration append policy (#1734) on the Dapper driver: the
/// driver registers the ledger and the maintenance step, a direct append to a stream archived whole under NewGeneration
/// starts a new generation, and its coordinator resumes a perspective from its parked cursor. The SQL itself is covered by
/// the EF Core <c>StreamDispositionSqlTests</c> and <c>ArchiveNewGenerationSqlTests</c>; these lock this driver's side.
/// </summary>
/// <docs>fundamentals/events/stream-end-of-life#scheduled-disposition</docs>
public class DapperStreamDispositionTests : PostgresTestBase {
  [Test]
  public async Task AddWhizbangPostgres_RegistersTheLedgerAndTheStep_AndTheStepArchivesASealedStreamOnceItsGraceHasPassedAsync() {
    var stream = (Guid)TrackedGuid.New();
    await _eventStore().AppendAsync(stream, new DapperMixedStreamProbeNoted { StreamId = stream });
    var sealedAt = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    await using (var conn = await _openAsync()) {
      _ = await conn.ExecuteAsync("SELECT wh_finalize_stream(@stream, 'ended', @sealedAt)", new { stream, sealedAt });
    }
    var clock = new MovableClock(sealedAt);
    var services = new ServiceCollection().AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), false, (string?)null, null);
    services.AddSingleton<TimeProvider>(clock);
    services.AddLogging();
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();
    var step = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<StreamDispositionStep>().Single();

    await Assert.That(scope.ServiceProvider.GetRequiredService<IStreamDispositionLedger>()).IsTypeOf<PostgresStreamDispositionLedger>();
    await step.RunAsync(scope.ServiceProvider, CancellationToken.None);
    clock.Now = sealedAt.AddDays(7);
    await step.RunAsync(scope.ServiceProvider, CancellationToken.None);

    await using var check = await _openAsync();
    await Assert.That(await check.ExecuteScalarAsync<string>("SELECT state FROM wh_stream_lifecycle WHERE stream_id = @stream", new { stream }))
      .IsEqualTo("Archived").Because("the default grace is seven days and the default disposition archives the whole stream");
  }

  [Test]
  public async Task AppendAsync_NewGeneration_ContinuesAtTheNextVersion_AndTheCoordinatorResumesFromTheParkedCursorAsync() {
    var store = _eventStore();
    var stream = (Guid)TrackedGuid.New();
    await store.AppendAsync(stream, new DapperMixedStreamProbeNoted { StreamId = stream });
    await using var conn = await _openAsync();
    var first = await conn.ExecuteScalarAsync<Guid>("SELECT event_id FROM wh_event_store WHERE stream_id = @stream", new { stream });
    _ = await conn.ExecuteAsync(
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at) VALUES (@stream, 'Probe.P', @first, 0, NOW())",
      new { stream, first });
    _ = await conn.ExecuteAsync(
      "INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_version) " +
      "VALUES (@stream, 'Probe.P', @first, '{}'::jsonb, 1, 1)", new { stream, first });
    var type = await conn.ExecuteScalarAsync<string>("SELECT stream_type FROM wh_stream_lifecycle WHERE stream_id = @stream", new { stream });
    _ = await conn.ExecuteAsync("SELECT wh_sync_stream_type_policies(ARRAY[@type], ARRAY['NewGeneration'])", new { type });
    _ = await new PostgresTableArchiveStore(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync).WriteAsync(ArchiveBatch.WholeStream(stream));

    await store.AppendAsync(stream, new DapperMixedStreamProbeNoted { StreamId = stream });
    var cursor = await _coordinator().GetPerspectiveCursorAsync(stream, "Probe.P");

    await Assert.That(await conn.ExecuteScalarAsync<long>("SELECT version FROM wh_event_store WHERE stream_id = @stream", new { stream })).IsEqualTo(2L);
    await Assert.That(cursor!.LastEventId).IsEqualTo(first)
      .Because("with no live cursor the perspective resumes after the event its parked cursor names");
  }

  [Test]
  public async Task AddWhizbangPostgres_AnEnabledAgePolicyThatPurges_PurgesTheStreamOnceItIsOld_AndNotBeforeAsync() {
    var stream = (Guid)TrackedGuid.New();
    await _eventStore().AppendAsync(stream, new DapperMixedStreamProbeNoted { StreamId = stream });
    var first = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    string type;
    await using (var conn = await _openAsync()) {
      _ = await conn.ExecuteAsync("UPDATE wh_stream_lifecycle SET first_appended_at = @first WHERE stream_id = @stream", new { first, stream });
      type = await conn.ExecuteScalarAsync<string>("SELECT stream_type FROM wh_stream_lifecycle WHERE stream_id = @stream", new { stream }) ?? "";
    }
    var clock = new MovableClock(first.AddDays(30).AddSeconds(-1));
    var services = new ServiceCollection().AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), false, (string?)null, null);
    services.AddSingleton<TimeProvider>(clock);
    services.AddLogging();
    services.Configure<StreamLifecycleOptions>(o => {
      o.Enabled = true;
      o.Types[type] = new StreamTypeLifecycleOptions { MaxAge = TimeSpan.FromDays(30), Action = StreamLifecycleAction.Purge };
    });
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();
    var step = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<StreamDispositionStep>().Single();

    await step.RunAsync(scope.ServiceProvider, CancellationToken.None);
    await using var check = await _openAsync();
    var rowsJustBefore = await check.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_event_store WHERE stream_id = @stream", new { stream });
    clock.Now = first.AddDays(30);
    await step.RunAsync(scope.ServiceProvider, CancellationToken.None);

    await Assert.That(rowsJustBefore).IsEqualTo(1L);
    await Assert.That(await check.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_event_store WHERE stream_id = @stream", new { stream })).IsEqualTo(0L)
      .Because("the Dapper driver registers the ledger, the purger and the step, so an age policy that purges reaches the store");
    await Assert.That(await check.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_stream_lifecycle WHERE stream_id = @stream", new { stream })).IsEqualTo(0L);
  }

  private sealed class MovableClock(DateTimeOffset now) : TimeProvider {
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private DapperWorkCoordinator _coordinator() =>
    new(ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), NullLogger<DapperWorkCoordinator>.Instance);

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
}
