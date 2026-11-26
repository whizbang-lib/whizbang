// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2323: the write paths start leaving out what a row holds only once every live instance of the service reads such
/// rows. Each instance's heartbeat says what it reads (<c>stored_form_level</c> in its registry row's metadata; an older
/// release writes none), the maintenance cycle's trim gate step latches <c>metadata_trim_row_values</c> once every live
/// instance qualifies, and the write paths and history conversion read that one flag. The latch is one-way.
/// </summary>
/// <docs>data/event-store#trim-gate</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/537_StoredMetadataLeavesOutWhatTheRowHolds.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/MetadataTrimGateStep.cs</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class StoredMetadataTrimGateSqlTests : EFCoreTestBase {
  private const string ORDER_PLACED = "MyApp.Orders.OrderPlaced, MyApp";
  private static readonly JsonSerializerOptions _json = JsonContextRegistry.CreateCombinedOptions();
  private static readonly DateTimeOffset _t0 = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
  private static readonly PerspectiveScope _scope = new() { TenantId = "tenant-a", UserId = "user-a" };

  // ===== The gate =====

  [Test]
  public async Task Gate_AFreshService_OpensOnlyOnceThisInstanceHasRegisteredWithItsMarkerAsync() {
    await using var conn = await _openAsync();

    var empty = await _gateAsync();
    await _registerThisBuildAsync();
    var registered = await _gateAsync();

    await Assert.That(empty).StartsWith("closed").Because("an empty registry has no instance that says it reads trimmed rows");
    await Assert.That(registered).IsEqualTo("opened").Because("the one live instance is this build, which reads them");
    await Assert.That(await _flagAsync(conn)).IsEqualTo("true");
    await Assert.That(await _scalarAsync<string>(conn,
      "SELECT metadata ->> 'stored_form_level' FROM wh_service_instances LIMIT 1"))
      .IsEqualTo(HeartbeatRequest.STORED_FORM_LEVEL.ToString(System.Globalization.CultureInfo.InvariantCulture))
      .Because("the EF Core driver's heartbeat wrote the marker");
  }

  [Test]
  [Arguments("gone")]
  [Arguments("stale")]
  public async Task Gate_StaysClosedWhileALiveInstanceLacksTheMarker_AndOpensOnceItIsGoneOrStaleAsync(string how) {
    await using var conn = await _openAsync();
    await _registerThisBuildAsync();
    var older = await _registerAnEarlierReleaseAsync(conn);

    var first = await _gateAsync();
    var again = await _gateAsync();
    await Assert.That(await _flagAsync(conn)).IsNull().Because("while closed, no flag is written");
    if (how == "gone") {
      await _executeAsync(conn, "DELETE FROM wh_service_instances WHERE instance_id = @i", ("i", older));
    } else {
      await _executeAsync(conn, "UPDATE wh_service_instances SET last_heartbeat_at = now() - interval '1 hour' WHERE instance_id = @i",
        ("i", older));
    }
    var after = await _gateAsync();

    await Assert.That(first).IsEqualTo("closed (1 of 2 live instances read trimmed rows)");
    await Assert.That(again).IsEqualTo(first).Because("asking again changes nothing while the instance is live");
    await Assert.That(after).IsEqualTo("opened").Because($"an instance that is {how} is not live, so it reads nothing");
    await Assert.That(await _flagAsync(conn)).IsEqualTo("true");
  }

  [Test]
  public async Task Gate_OnceOpen_StaysOpen_WhenAnInstanceWithoutTheMarkerAppearsAsync() {
    await using var conn = await _openAsync();
    await _registerThisBuildAsync();
    await Assert.That(await _gateAsync()).IsEqualTo("opened").Because("control");

    await _registerAnEarlierReleaseAsync(conn);
    var afterFlap = await _gateAsync();
    var id = (await _emitAsync(conn, _scope))[0];

    await Assert.That(afterFlap).IsEqualTo("open").Because("the latch is one-way: trimmed rows already exist");
    await Assert.That(await _flagAsync(conn)).IsEqualTo("true");
    await Assert.That(await _textAsync(conn, id, "MessageId")).IsNull().Because("and the write paths keep trimming");
  }

  [Test]
  public async Task Gate_ASettingWrittenByHandOtherThanTrue_IsNotOpenAsync() {
    await using var conn = await _openAsync();
    await _executeAsync(conn, "INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES ('metadata_trim_row_values', 'false', 'boolean')");

    var id = (await _emitAsync(conn, _scope))[0];

    await Assert.That(await _textAsync(conn, id, "MessageId")).IsEqualTo(id.ToString())
      .Because("only the latched value opens the write paths");
  }

  // ===== The write paths =====

  [Test]
  public async Task Writes_BeforeTheGateOpens_AreUntrimmedAndReadByAnEarlierReader_AndAfterItAreTrimmedAsync() {
    await using var conn = await _openAsync();
    var before = (await _emitAsync(conn, _scope))[0];
    var beforeAppend = (Guid)TrackedGuid.New();
    await _appendAsync(conn, beforeAppend);

    await _registerThisBuildAsync();
    await Assert.That(await _gateAsync()).IsEqualTo("opened");
    var after = (await _emitAsync(conn, _scope))[0];
    var afterAppend = (Guid)TrackedGuid.New();
    await _appendAsync(conn, afterAppend);

    foreach (var id in new[] { before, beforeAppend }) {
      var metadata = await _metadataAsync(conn, id);
      await Assert.That(_readAsAnEarlierRelease(metadata).Value).IsEqualTo(id)
        .Because("a release that requires the message id reads every row written before the gate opened");
      await Assert.That(await _hasHopScopeAsync(conn, id)).IsTrue();
    }
    foreach (var id in new[] { after, afterAppend }) {
      await Assert.That(await _textAsync(conn, id, "MessageId")).IsNull().Because("rows written after the gate opened are trimmed");
      await Assert.That(await _hasHopScopeAsync(conn, id)).IsFalse();
    }
  }

  // ===== History =====

  [Test]
  public async Task Conversion_WaitsForTheGate_ThenTrimsWhatItWroteAndWhatWasSlimAsync() {
    await using var conn = await _openAsync();
    var historic = (Guid)TrackedGuid.New();
    await _settingAsync(conn, "metadata_write_form", "Full");
    await _appendAsync(conn, historic);
    await _executeAsync(conn, "DELETE FROM wh_settings WHERE setting_key = 'metadata_write_form'");
    await _executeAsync(conn, "UPDATE wh_event_body SET metadata = metadata - 'rid' WHERE event_id = @e", ("e", historic));
    var slim = (Guid)TrackedGuid.New();
    await _appendAsync(conn, slim);

    var closed = await _convertAsync(conn);
    await Assert.That(closed.Affected).IsEqualTo(1L).Because("only the row written before slim metadata is converted");
    await Assert.That(await _textAsync(conn, historic, "MessageId")).IsEqualTo(historic.ToString())
      .Because("while the gate is closed a converted row keeps what an earlier release needs");
    await Assert.That(await _textAsync(conn, slim, "MessageId")).IsEqualTo(slim.ToString()).Because("and a slim row is not trimmed");
    await Assert.That(closed.Status).IsEqualTo("ok");
    await Assert.That((await _convertAsync(conn)).Status).IsEqualTo("complete").Because("control: the walk read the table");

    await _registerThisBuildAsync();
    await Assert.That(await _gateAsync()).IsEqualTo("opened");
    var open = await _convertAsync(conn);

    await Assert.That(open.Affected).IsEqualTo(2L).Because("opening the gate starts the walk over, and it trims both rows");
    await Assert.That(await _textAsync(conn, historic, "MessageId")).IsNull();
    await Assert.That(await _textAsync(conn, slim, "MessageId")).IsNull();
    await Assert.That(await _hasHopScopeAsync(conn, slim)).IsFalse();
  }

  // ===== The step =====

  [Test]
  public async Task Step_IsNamed_AndRunsTheGateAsync() {
    await using var conn = await _openAsync();
    await _registerThisBuildAsync();
    var step = new MetadataTrimGateStep(NullLogger<MetadataTrimGateStep>.Instance);

    // No schema: the function is found on the search path rather than schema-qualified.
    await step.RunAsync(_services(schema: null), CancellationToken.None);

    await Assert.That(step.Name).IsEqualTo("metadata-trim-gate");
    await Assert.That(await _flagAsync(conn)).IsEqualTo("true");
    await Assert.That(MetadataTrimGateStep.FromServices(new ServiceCollection().BuildServiceProvider()).Name).IsEqualTo("metadata-trim-gate");
  }

  // ===== Helpers =====

  /// <summary>Reads stored metadata as a release before #2323 did: the message id is required.</summary>
  private static MessageId _readAsAnEarlierRelease(string metadata) {
    using var doc = JsonDocument.Parse(metadata);
    return doc.RootElement.TryGetProperty("MessageId", out var id)
      ? MessageId.From(id.GetGuid())
      : throw new JsonException("missing required properties including: 'MessageId'");
  }

  private ServiceProvider _services(string? schema = "public") =>
    new ServiceCollection()
      .AddSingleton(new PostgresArchiveConnection(async ct => {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
      }, schema))
      .BuildServiceProvider();

  private async Task<string> _gateAsync() =>
    await new MetadataTrimGateStep(NullLogger<MetadataTrimGateStep>.Instance).OpenAsync(_services(), CancellationToken.None);

  /// <summary>Registers an instance of this build through the EF Core driver's heartbeat.</summary>
  private async Task _registerThisBuildAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, _json);
    var accepted = await coordinator.RecordHeartbeatAsync(new HeartbeatRequest((Guid)TrackedGuid.New(), "svc", "this-build", 1));
    await Assert.That(accepted).IsTrue();
  }

  /// <summary>Registers a live instance the way a release before #2323 does: its metadata names no stored form level.</summary>
  private static async Task<Guid> _registerAnEarlierReleaseAsync(NpgsqlConnection conn) {
    var id = (Guid)TrackedGuid.New();
    await _executeAsync(conn, "SELECT record_heartbeat(@i, 'svc', 'earlier-release', 2, '{}'::jsonb)", ("i", id));
    return id;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static MessageHop _hop() => new() {
    Type = HopType.Current,
    ServiceInstance = StoredMetadataCorpus.Orders,
    Timestamp = StoredMetadataCorpus.Sent,
    CorrelationId = StoredMetadataCorpus.Correlation,
    CausationId = StoredMetadataCorpus.Cause,
    Scope = ScopeDelta.FromPerspectiveScope(_scope),
  };

  private static string _scopeJson(PerspectiveScope scope) => JsonSerializer.Serialize(scope, _json.GetTypeInfo(typeof(PerspectiveScope)));

  private static Task<string?> _flagAsync(NpgsqlConnection conn) =>
    _scalarAsync<string?>(conn, "SELECT setting_value FROM wh_settings WHERE setting_key = 'metadata_trim_row_values'");

  private static Task<string?> _textAsync(NpgsqlConnection conn, Guid id, string key) =>
    _scalarAsync<string?>(conn, "SELECT metadata ->> @k FROM wh_event_body WHERE event_id = @e", ("e", id), ("k", key));

  private static Task<string> _metadataAsync(NpgsqlConnection conn, Guid id) =>
    _scalarAsync<string>(conn, "SELECT metadata::text FROM wh_event_body WHERE event_id = @e", ("e", id));

  private static Task<bool> _hasHopScopeAsync(NpgsqlConnection conn, Guid id) =>
    _scalarAsync<bool>(conn, "SELECT metadata -> 'Hops' -> 0 ? 'sc' FROM wh_event_body WHERE event_id = @e", ("e", id));

  private sealed record Converted(long Affected, string Status);

  private static async Task<Converted> _convertAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT rows_affected, status FROM wh_convert_event_metadata_history(100)";
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return new Converted(reader.GetInt64(0), reader.GetString(1));
  }

  private static async Task<Guid[]> _emitAsync(NpgsqlConnection conn, PerspectiveScope scope) {
    var instanceId = (Guid)TrackedGuid.New();
    var id = (Guid)TrackedGuid.New();
    await _executeAsync(conn, """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
         status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
      VALUES
        (@id, 'trim-gate', @type, 'env', jsonb_build_object('id', @id::text, 'p', jsonb_build_object('n', 1), 'h', @hops::jsonb),
         '{}'::jsonb, @scope::jsonb, 1, 0, @inst, @lease, 0, @sid, true, 0, @now)
      """,
      ("id", id), ("type", ORDER_PLACED), ("hops", StoredMetadataCorpus.HopsJson([_hop()])), ("scope", _scopeJson(scope)),
      ("inst", instanceId), ("lease", _t0.AddMinutes(5)), ("sid", (Guid)TrackedGuid.New()), ("now", _t0));
    await using var emit = conn.CreateCommand();
    emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, @lease, @now, 10000)";
    emit.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, new[] { id });
    emit.Parameters.AddWithValue("inst", instanceId);
    emit.Parameters.AddWithValue("lease", _t0.AddMinutes(5));
    emit.Parameters.AddWithValue("now", _t0);
    _ = await emit.ExecuteScalarAsync();
    return [id];
  }

  private static async Task _appendAsync(NpgsqlConnection conn, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT wh_append_event(@id, @sid, @sid, split_part(@type, ',', 1), @type, '{"amount": 1}'::jsonb, @meta::jsonb, @scope::jsonb, @at, 0)
      """;
    cmd.Parameters.AddWithValue(nameof(id), id);
    cmd.Parameters.AddWithValue("sid", (Guid)TrackedGuid.New());
    cmd.Parameters.AddWithValue("type", ORDER_PLACED);
    cmd.Parameters.AddWithValue("meta", $$"""{"MessageId":"{{id}}","Hops":{{StoredMetadataCorpus.HopsJson([_hop()])}}}""");
    cmd.Parameters.AddWithValue("scope", _scopeJson(_scope));
    cmd.Parameters.AddWithValue("at", _t0);
    _ = await cmd.ExecuteScalarAsync();
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    var result = await cmd.ExecuteScalarAsync();
    return result is DBNull or null ? default! : (T)result;
  }

  private static Task _settingAsync(NpgsqlConnection conn, string key, string value) =>
    _executeAsync(conn, """
      INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES (@k, @v, 'string')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """, ("k", key), ("v", value));

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
