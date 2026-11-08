// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// What a rebuild keeps of a row's created_at (#2152, #2154): the first folded event's time, or the stream's start once its
/// first event is gone. A row's created_at is the
/// business time of the event that brought it into being. The superseding reaper and a prefix truncate delete the
/// stream's first event outright, and a rebuild that then writes the row afresh (blue-green into an empty shadow table, or
/// in place where the row is missing) dated it at an event that survived. It now takes the stream's recorded start.
/// </summary>
/// <docs>fundamentals/events/superseding-events#what-a-rebuild-keeps</docs>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRowStart.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/BaseUpsertStrategy.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/Templates/PerspectiveRunnerTemplate.cs</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class RebuildCreatedAtIntegrationTests : EFCoreTestBase {
  private const string PERSPECTIVE = "Whizbang.Data.EFCore.Postgres.Tests.Perspectives.RebuildBalancePerspective";
  private const string MODEL = "Whizbang.Data.EFCore.Postgres.Tests.Perspectives.RebuildBalanceModel";
  private const string TABLE = "wh_per_rebuild_balance";
  private static readonly DateTimeOffset _mar1 = new(2020, 3, 1, 9, 0, 0, TimeSpan.Zero);
  private static readonly DateTimeOffset _jun5 = new(2020, 6, 5, 9, 0, 0, TimeSpan.Zero);
  private static readonly DateTimeOffset _sep9 = new(2020, 9, 9, 9, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task RebuildBlueGreen_AfterTheFirstEventIsReaped_DatesTheRowAtTheStreamsStartAsync() {
    var stream = Guid.CreateVersion7();
    await using var sp = _services();
    await _appendAsync(sp, stream, new RebuildBalanceRestatedEvent { StreamId = stream, Account = "a", Balance = 10m }, _mar1);
    await _appendAsync(sp, stream, new RebuildBalanceRestatedEvent { StreamId = stream, Account = "a", Balance = 20m }, _jun5);
    await _reapAsync(stream);
    await _registerTableAsync();

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    var row = await _rowAsync(stream);
    await Assert.That(row.Balance).IsEqualTo(20m).Because("the surviving event still decides the row's data");
    await Assert.That(row.CreatedAt).IsEqualTo(_mar1.UtcDateTime)
      .Because("the stream began on Mar 1; its first event was reaped, and the rebuilt row is dated at the stream's "
        + "recorded start, not at the Jun 5 event that survived");
  }

  [Test]
  public async Task RebuildInPlace_ARowItInserts_AfterTheFirstEventIsReaped_DatesTheRowAtTheStreamsStartAsync() {
    var stream = Guid.CreateVersion7();
    await using var sp = _services();
    await _appendAsync(sp, stream, new RebuildBalanceRestatedEvent { StreamId = stream, Account = "a", Balance = 10m }, _mar1);
    await _appendAsync(sp, stream, new RebuildBalanceRestatedEvent { StreamId = stream, Account = "a", Balance = 20m }, _jun5);
    await _reapAsync(stream);

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildInPlaceAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    await Assert.That((await _rowAsync(stream)).CreatedAt).IsEqualTo(_mar1.UtcDateTime)
      .Because("an in-place rebuild inserts a missing row, and dates it at the stream's start as blue-green does");
  }

  [Test]
  public async Task RebuildInPlace_AnExistingRow_KeepsItsCreatedAtAsync() {
    var stream = Guid.CreateVersion7();
    await using var sp = _services();
    await _appendAsync(sp, stream, new RebuildBalanceRestatedEvent { StreamId = stream, Account = "a", Balance = 10m }, _mar1);
    await _appendAsync(sp, stream, new RebuildBalanceRestatedEvent { StreamId = stream, Account = "a", Balance = 20m }, _jun5);
    await _seedRowAsync(stream, _mar1.UtcDateTime);
    await _reapAsync(stream);

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildInPlaceAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    var row = await _rowAsync(stream);
    await Assert.That(row.Balance).IsEqualTo(20m);
    await Assert.That(row.CreatedAt).IsEqualTo(_mar1.UtcDateTime)
      .Because("an in-place rebuild updates a row that exists, and an update never moves created_at");
  }

  [Test]
  public async Task RebuildBlueGreen_AfterAPrefixTruncate_DatesTheRowAtTheStreamsStartAsync() {
    var stream = Guid.CreateVersion7();
    await using var sp = _services();
    await _appendAsync(sp, stream, new RebuildCreditedEvent { StreamId = stream, Amount = 100m }, _mar1);
    await _appendAsync(sp, stream, new RebuildCreditedEvent { StreamId = stream, Amount = 50m }, _jun5);
    await _appendAsync(sp, stream, new RebuildCreditedEvent { StreamId = stream, Amount = 25m }, _sep9);
    await _truncateAsync(stream, throughVersion: 1);
    await _registerTableAsync();

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    var row = await _rowAsync(stream);
    await Assert.That(row.Balance).IsEqualTo(75m).Because("the truncated credit is gone from the fold");
    await Assert.That(row.CreatedAt).IsEqualTo(_mar1.UtcDateTime)
      .Because("the truncate recorded the business time of the first event it removed");
  }

  [Test]
  public async Task RebuildBlueGreen_AFullHistoryStream_DatesTheRowAtItsFirstEvent_AndUpdatedAtAtItsLastAsync() {
    var stream = Guid.CreateVersion7();
    await using var sp = _services();
    await _appendAsync(sp, stream, new RebuildCreditedEvent { StreamId = stream, Amount = 100m }, _mar1);
    await _appendAsync(sp, stream, new RebuildCreditedEvent { StreamId = stream, Amount = 50m }, _jun5);
    // A recorded start the stream contradicts: it still holds its first event, so the record must not be used.
    await _executeAsync("UPDATE wh_stream_lifecycle SET first_event_at = @t WHERE stream_id = @s",
      ("t", _sep9.UtcDateTime), ("s", stream));
    await _registerTableAsync();

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    var row = await _rowAsync(stream);
    await Assert.That(row.CreatedAt).IsEqualTo(_mar1.UtcDateTime)
      .Because("#2154: the row is dated at the first event folded into it, not the last event of the write that inserts "
        + "it (Jun 5), and not the ledger's record, which is consulted only once the first event is gone (Sep 9)");
    await Assert.That(row.UpdatedAt).IsEqualTo(_jun5.UtcDateTime)
      .Because("updated_at is the last folded event's time");
  }

  [Test]
  public async Task RebuildInPlace_AFullHistoryStream_InsertsTheRowDatedAtItsFirstEventAsync() {
    var stream = Guid.CreateVersion7();
    await using var sp = _services();
    await _appendAsync(sp, stream, new RebuildCreditedEvent { StreamId = stream, Amount = 100m }, _mar1);
    await _appendAsync(sp, stream, new RebuildCreditedEvent { StreamId = stream, Amount = 50m }, _jun5);
    await _appendAsync(sp, stream, new RebuildCreditedEvent { StreamId = stream, Amount = 25m }, _sep9);

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildInPlaceAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    var row = await _rowAsync(stream);
    await Assert.That(row.CreatedAt).IsEqualTo(_mar1.UtcDateTime)
      .Because("#2154: an in-place rebuild that inserts a missing row dates it at its first event, not the Sep 9 event "
        + "its one write ends on");
    await Assert.That(row.UpdatedAt).IsEqualTo(_sep9.UtcDateTime);
  }

  [Test]
  public async Task LiveFirstBatch_OfSeveralEvents_DatesTheRowAtTheFirstAsync() {
    var stream = Guid.CreateVersion7();
    await using var sp = _services();
    await _appendAsync(sp, stream, new RebuildCreditedEvent { StreamId = stream, Amount = 100m }, _mar1);
    await _appendAsync(sp, stream, new RebuildCreditedEvent { StreamId = stream, Amount = 50m }, _jun5);
    await _appendAsync(sp, stream, new RebuildCreditedEvent { StreamId = stream, Amount = 25m }, _sep9);

    await using (var scope = sp.CreateAsyncScope()) {
      var runner = scope.ServiceProvider.GetRequiredService<IPerspectiveRunnerRegistry>().GetRunner(PERSPECTIVE, scope.ServiceProvider)!;
      var completion = await runner.RunAsync(stream, PERSPECTIVE, lastProcessedEventId: null);
      await Assert.That(completion.EventsProcessed).IsEqualTo(3).Because("the live drain folded all three in one batch");
    }

    var row = await _rowAsync(stream);
    await Assert.That(row.Balance).IsEqualTo(175m);
    await Assert.That(row.CreatedAt).IsEqualTo(_mar1.UtcDateTime)
      .Because("#2154: a new row is dated at its first event however the events were batched; dating it at the last "
        + "event of its first batch made created_at depend on batching");
    await Assert.That(row.UpdatedAt).IsEqualTo(_sep9.UtcDateTime);
  }

  // ===== Fixture =====

  private ServiceProvider _services() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddScoped(_ => new WorkCoordinationDbContext(DbContextOptions));
    services.AddScoped<DbContext>(s => s.GetRequiredService<WorkCoordinationDbContext>());
    services.AddScoped<IEventStore>(s => new EFCoreEventStore<WorkCoordinationDbContext>(s.GetRequiredService<WorkCoordinationDbContext>()));
    services.AddScoped<IEventStoreQuery>(s => new EFCoreFilterableEventStoreQuery(s.GetRequiredService<WorkCoordinationDbContext>()));
    services.AddScoped<IPerspectiveStore<RebuildBalanceModel>>(s =>
      new EFCorePostgresPerspectiveStore<RebuildBalanceModel>(s.GetRequiredService<WorkCoordinationDbContext>(), TABLE));
    services.AddScoped<RebuildBalancePerspective>();
    var asm = typeof(RebuildCreatedAtIntegrationTests).Assembly;
    services.AddScoped(asm.GetTypes().Single(t => t.Name == "RebuildBalancePerspectiveRunner"));
    var registryType = asm.GetTypes().Single(t => t.Name == "PerspectiveRunnerRegistry" &&
      t.Namespace == "Whizbang.Data.EFCore.Postgres.Tests.Generated");
    services.AddSingleton(typeof(IPerspectiveRunnerRegistry), registryType);
    services.AddScoped<IPerspectiveCheckpointCompleter>(s =>
      new EFCorePostgresPerspectiveCheckpointCompleter(s.GetRequiredService<WorkCoordinationDbContext>()));
    services.AddSingleton<IPerspectiveTableSwapper>(s =>
      EFCorePerspectiveTableSwapperFactory.Create(s, typeof(WorkCoordinationDbContext))!);
    services.AddSingleton<IPerspectiveRebuilder, PerspectiveRebuilder>();
    return services.BuildServiceProvider();
  }

  private static async Task _appendAsync<TEvent>(IServiceProvider root, Guid stream, TEvent payload, DateTimeOffset at)
      where TEvent : IEvent {
    await using var scope = root.CreateAsyncScope();
    var envelope = new MessageEnvelope<TEvent> {
      MessageId = MessageId.New(),
      Payload = payload,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = at,
        ServiceInstance = new ServiceInstanceInfo { InstanceId = Guid.CreateVersion7(), ServiceName = "svc", HostName = "h", ProcessId = 1 },
      }],
    };
    await scope.ServiceProvider.GetRequiredService<IEventStore>().AppendAsync(stream, envelope);
  }

  // Stamps the commit order and runs the reaper, which removes the stream's first event once a newer one of its key is stored.
  private async Task _reapAsync(Guid stream) {
    await _executeAsync("SELECT stamp_pending_commit_sequences(1000)");
    await _executeAsync("SELECT * FROM wh_reap_superseded_events(5000)");
    await Assert.That(await _scalarAsync("SELECT count(*) FROM wh_event_store WHERE stream_id = @s AND version = 1", ("s", stream)))
      .IsEqualTo(0L).Because("the precondition: the reaper removed the stream's first event");
  }

  private async Task _truncateAsync(Guid stream, long throughVersion) {
    await _executeAsync("SELECT * FROM wh_truncate_stream_prefix(@s, @v)", ("s", stream), ("v", throughVersion));
    await Assert.That(await _scalarAsync("SELECT count(*) FROM wh_event_store WHERE stream_id = @s AND version <= @v",
      ("s", stream), ("v", throughVersion))).IsEqualTo(0L).Because("the precondition: the truncate removed the prefix");
  }

  private async Task _seedRowAsync(Guid stream, DateTime createdAt) {
    await using var context = CreateDbContext();
    var store = new EFCorePostgresPerspectiveStore<RebuildBalanceModel>(context, TABLE);
    await store.UpsertAsync(stream, new RebuildBalanceModel { Id = stream, Balance = 999m }, new PerspectiveScope(), false,
      new PerspectiveMetadata { EventType = "Seed", EventId = Guid.CreateVersion7().ToString(), Timestamp = createdAt });
  }

  private async Task _registerTableAsync() =>
    await _executeAsync("""
      INSERT INTO wh_perspective_registry (clr_type_name, table_name, schema_json, schema_hash, service_name)
      VALUES (@name, @table, '{}'::jsonb, 'h', 'svc') ON CONFLICT DO NOTHING
      """, ("name", MODEL), ("table", TABLE));

  private async Task<(decimal Balance, DateTime CreatedAt, DateTime UpdatedAt)> _rowAsync(Guid id) {
    await using var conn = await _openAsync();
    await using var cmd = new NpgsqlCommand($"SELECT (data->>'Balance')::numeric, created_at, updated_at FROM {TABLE} WHERE id = @id", conn);
    cmd.Parameters.AddWithValue(nameof(id), id);
    await using var reader = await cmd.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue().Because("the rebuild wrote the row");
    return (reader.GetDecimal(0), DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
      DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc));
  }

  private async Task<long> _scalarAsync(string sql, params (string Name, object Value)[] parameters) {
    await using var conn = await _openAsync();
    await using var cmd = _command(conn, sql, parameters);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private async Task _executeAsync(string sql, params (string Name, object Value)[] parameters) {
    await using var conn = await _openAsync();
    await using var cmd = _command(conn, sql, parameters);
    _ = await cmd.ExecuteNonQueryAsync();
  }

  private static NpgsqlCommand _command(NpgsqlConnection conn, string sql, (string Name, object Value)[] parameters) {
    var cmd = new NpgsqlCommand(sql, conn);
    foreach (var (name, value) in parameters) {
      cmd.Parameters.Add(value is DateTime
        ? new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = value }
        : new NpgsqlParameter(name, value));
    }
    return cmd;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }
}
