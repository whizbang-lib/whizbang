// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2323: the slim stored form leaves out what the row already holds in its own columns. The message id is left out
/// when it equals <c>wh_event_store.event_id</c>, and the slim hop's scope when it is the scope value alone and equals
/// <c>wh_event_store.scope</c>; anything the row does not hold is kept. Every write path stores the trimmed form, history
/// conversion trims rows written before it, and reconstruction puts both back. Every test here runs with the trim gate
/// open; <c>StoredMetadataTrimGateSqlTests</c> covers when it opens.
/// </summary>
/// <docs>data/event-store#row-values</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/537_StoredMetadataLeavesOutWhatTheRowHolds.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class StoredMetadataRowValuesSqlTests : EFCoreTestBase {
  private const string ORDER_PLACED = "MyApp.Orders.OrderPlaced, MyApp";
  private static readonly JsonSerializerOptions _json = JsonContextRegistry.CreateCombinedOptions();
  private static readonly DateTimeOffset _t0 = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
  private static readonly PerspectiveScope _scope = new() { TenantId = "tenant-a", UserId = "user-a" };

  // ===== The write paths =====

  [Test]
  public async Task OutboxChain_LeavesOutTheMessageIdAndTheHopScopeTheRowHolds_AndNothingElseAsync() {
    await using var conn = await _openAsync();
    var hop = _hop(ScopeDelta.FromPerspectiveScope(_scope));

    var id = (await _emitOutboxAsync(conn, (hop, _scope, null)))[0];

    await Assert.That(await _scalarAsync<bool>(conn, """
      SELECT b.metadata = jsonb_set(_wh_slim_event_metadata(s.metadata, s.route_id) - 'MessageId', '{Hops,0}',
                                    (_wh_slim_event_metadata(s.metadata, s.route_id) -> 'Hops' -> 0) - 'sc')
      FROM wh_event_body b JOIN wh_event_metadata_staging s ON s.event_id = b.event_id WHERE b.event_id = @e
      """, ("e", id))).IsTrue()
      .Because("the row is the slim form less exactly the message id and the hop scope, every other key as before");
    await Assert.That(await _scalarAsync<int>(conn, """
      SELECT pg_column_size(_wh_slim_event_metadata(s.metadata, s.route_id)) - pg_column_size(b.metadata)
      FROM wh_event_body b JOIN wh_event_metadata_staging s ON s.event_id = b.event_id WHERE b.event_id = @e
      """, ("e", id))).IsGreaterThan(0).Because("so the stored metadata of a typical event shrinks");
  }

  [Test]
  public async Task OutboxChain_KeepsWhatTheRowDoesNotHoldAsync() {
    await using var conn = await _openAsync();
    var otherId = (Guid)TrackedGuid.New();
    var roles = ScopeDelta.CreateDelta(null, new ScopeContext {
      Scope = _scope,
      Roles = new HashSet<string>(["reader"]),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
    });
    var principalsOnly = new PerspectiveScope { AllowedPrincipals = ["group:sales"] };

    var ids = await _emitOutboxAsync(conn,
      (_hop(ScopeDelta.FromPerspectiveScope(_scope)), _scope, otherId),
      (_hop(ScopeDelta.FromPerspectiveScope(_scope)), new PerspectiveScope { TenantId = "tenant-b", UserId = "user-a" }, null),
      (_hop(roles), _scope, null),
      (_hop(ScopeDelta.CreateDelta(null, new ScopeContext {
        Scope = principalsOnly,
        Roles = new HashSet<string>(),
        Permissions = new HashSet<Permission>(),
        SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
        Claims = new Dictionary<string, string>(),
      })), principalsOnly, null),
      (_hop(null), _scope, null));

    await Assert.That(await _textAsync(conn, ids[0], "MessageId")).IsEqualTo(otherId.ToString())
      .Because("a message id that differs from the row's event id is not the row's, so it is kept");
    await Assert.That(await _hasHopScopeAsync(conn, ids[1])).IsTrue().Because("a hop scope that differs from the scope column is kept");
    await Assert.That(await _hasHopScopeAsync(conn, ids[2])).IsTrue().Because("a hop scope carrying roles holds more than the column");
    await Assert.That(await _hasHopScopeAsync(conn, ids[3])).IsTrue()
      .Because("a scope that names nothing a reader restores (allowed principals alone) is kept, or reading would lose it");
    await Assert.That(await _hasHopScopeAsync(conn, ids[4])).IsFalse().Because("control: a hop without a scope gains none");
    await Assert.That(await _textAsync(conn, ids[1], "MessageId")).IsNull().Because("control: the message id is still left out there");
  }

  [Test]
  [Arguments("ap-ex-and-markers")]
  [Arguments("system")]
  public async Task OutboxChain_AScopeColumnInItsWrittenForm_MatchesTheHopScopeItEqualsAsync(string shape) {
    await using var conn = await _openAsync();
    var scope = shape == "system"
      ? new PerspectiveScope { IsSystem = true }
      : new PerspectiveScope {
        TenantId = "tenant-a",
        UserId = "user-a",
        CustomerId = "customer-a",
        OrganizationId = "org-a",
        AllowedPrincipals = ["group:sales"],
        Extensions = [new ScopeExtension("region", "eu-west"), new ScopeExtension("none", null)],
      };

    var id = (await _emitOutboxAsync(conn, (_hop(ScopeDelta.FromPerspectiveScope(scope)), scope, null)))[0];

    await Assert.That(await _hasHopScopeAsync(conn, id)).IsFalse()
      .Because("the column (empty lists, null values) and the hop (neither) are the same scope, so the hop's copy is left out");
  }

  [Test]
  public async Task DirectAppend_LeavesOutTheMessageIdInEitherDriversForm_AndTheScopeTheColumnHoldsAsync() {
    await using var conn = await _openAsync();
    var hops = StoredMetadataCorpus.HopsJson([_hop(ScopeDelta.FromPerspectiveScope(_scope))]);
    var efCore = (Guid)TrackedGuid.New();
    var dapper = (Guid)TrackedGuid.New();
    var unscoped = (Guid)TrackedGuid.New();
    var scopeJson = _scopeJson(_scope);

    await _appendAsync(conn, efCore, $$"""{"MessageId":"{{efCore}}","Hops":{{hops}}}""", scopeJson);
    await _appendAsync(conn, dapper, $$"""{"message_id":"{{dapper}}","correlation_id":"c","hops":{{hops}}}""", scopeJson);
    await _appendAsync(conn, unscoped, $$"""{"MessageId":"{{unscoped}}","Hops":{{hops}}}""", null);

    await Assert.That(await _textAsync(conn, efCore, "MessageId")).IsNull();
    await Assert.That(await _hasHopScopeAsync(conn, efCore)).IsFalse();
    await Assert.That(await _textAsync(conn, dapper, "message_id")).IsNull().Because("the Dapper store's key is the row's event id too");
    await Assert.That(await _textAsync(conn, dapper, "correlation_id")).IsEqualTo("c").Because("its other keys are kept as written");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT NOT (metadata -> 'hops' -> 0 ? 'sc') FROM wh_event_body WHERE event_id = @e", ("e", dapper)))
      .IsTrue().Because("its hop list (hops) is trimmed too");
    await Assert.That(await _hasHopScopeAsync(conn, unscoped)).IsTrue().Because("a row stored without a scope column keeps its hop scope");
  }

  [Test]
  public async Task FullRows_KeepEverythingAsync() {
    await using var conn = await _openAsync();
    await _settingAsync(conn, "debug_mode", "true");

    var id = (await _emitOutboxAsync(conn, (_hop(ScopeDelta.FromPerspectiveScope(_scope)), _scope, null)))[0];

    await Assert.That(await _textAsync(conn, id, "MessageId")).IsEqualTo(id.ToString()).Because("a full row is the metadata as written");
    await Assert.That(await _hasHopScopeAsync(conn, id)).IsTrue();
  }

  // ===== History =====

  [Test]
  public async Task Convert_ARowWrittenBeforeSlimMetadata_BecomesTheTrimmedFormAsync() {
    await using var conn = await _openAsync();
    var id = (Guid)TrackedGuid.New();
    var full = $$"""{"MessageId":"{{id}}","Hops":{{StoredMetadataCorpus.HopsJson([_hop(ScopeDelta.FromPerspectiveScope(_scope))])}}}""";
    await _settingAsync(conn, "metadata_write_form", "Full");
    await _appendAsync(conn, id, full, _scopeJson(_scope));
    await _executeAsync(conn, "DELETE FROM wh_settings WHERE setting_key = 'metadata_write_form'");
    await _executeAsync(conn, "UPDATE wh_event_body SET metadata = metadata - 'rid' WHERE event_id = @e", ("e", id));

    var (scanned, affected) = await _convertAsync(conn);

    await Assert.That(affected).IsEqualTo(1L);
    await Assert.That(scanned).IsEqualTo(1L);
    await Assert.That(await _textAsync(conn, id, "MessageId")).IsNull();
    await Assert.That(await _hasHopScopeAsync(conn, id)).IsFalse();
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT metadata ? 'rid' FROM wh_event_body WHERE event_id = @e", ("e", id))).IsTrue();
  }

  [Test]
  public async Task Convert_ASlimRowWrittenBeforeTheTrim_IsTrimmed_AndItsRouteIsNotCountedAgainAsync() {
    await using var conn = await _openAsync();
    var id = (Guid)TrackedGuid.New();
    var full = $$"""{"MessageId":"{{id}}","Hops":{{StoredMetadataCorpus.HopsJson([_hop(ScopeDelta.FromPerspectiveScope(_scope))])}}}""";
    await _appendAsync(conn, id, full, _scopeJson(_scope));
    await _executeAsync(conn, "SELECT * FROM wh_distill_event_metadata(1000)");
    await _executeAsync(conn, """
      UPDATE wh_event_body SET metadata = _wh_slim_event_metadata(@full::jsonb, (metadata ->> 'rid')::uuid) WHERE event_id = @e
      """, ("e", id), ("full", full));
    var occurrences = await _scalarAsync<long>(conn, "SELECT sum(occurrences)::bigint FROM wh_routes");
    await Assert.That(await _textAsync(conn, id, "MessageId")).IsEqualTo(id.ToString()).Because("control: the row is in the old slim form");

    var (_, affected) = await _convertAsync(conn);
    var (_, again) = await _convertAsync(conn, reset: true);

    await Assert.That(affected).IsEqualTo(1L).Because("a slim row still holding what its columns hold is trimmed");
    await Assert.That(await _textAsync(conn, id, "MessageId")).IsNull();
    await Assert.That(await _hasHopScopeAsync(conn, id)).IsFalse();
    await Assert.That(await _scalarAsync<long>(conn, "SELECT sum(occurrences)::bigint FROM wh_routes")).IsEqualTo(occurrences)
      .Because("its route was counted when it was written; trimming it counts nothing");
    await Assert.That(again).IsEqualTo(0L).Because("a trimmed row is passed over, whatever the cursor says");
  }

  [Test]
  public async Task Convert_AFullRowWithItsRouteId_IsNotTouchedAsync() {
    await using var conn = await _openAsync();
    var id = (Guid)TrackedGuid.New();
    await _settingAsync(conn, "metadata_write_form", "Full");
    await _appendAsync(conn, id, $$"""{"MessageId":"{{id}}","Hops":{{StoredMetadataCorpus.HopsJson([_hop(ScopeDelta.FromPerspectiveScope(_scope))])}}}""",
      _scopeJson(_scope));
    await _executeAsync(conn, "DELETE FROM wh_settings WHERE setting_key = 'metadata_write_form'");
    var before = await _scalarAsync<string>(conn, "SELECT ctid::text || xmin::text || metadata::text FROM wh_event_body WHERE event_id = @e", ("e", id));

    var (scanned, affected) = await _convertAsync(conn);

    await Assert.That(scanned).IsEqualTo(1L);
    await Assert.That(affected).IsEqualTo(0L).Because("a row written full stays full: it is the metadata as written");
    await Assert.That(await _scalarAsync<string>(conn, "SELECT ctid::text || xmin::text || metadata::text FROM wh_event_body WHERE event_id = @e", ("e", id)))
      .IsEqualTo(before);
  }

  // ===== Reconstruction =====

  [Test]
  public async Task TraceMetadata_PutsTheMessageIdAndTheScopeBack_FromTheRowAndFromItsTraceAsync() {
    await using var conn = await _openAsync();
    var ef = (Guid)TrackedGuid.New();
    var dapper = (Guid)TrackedGuid.New();
    var hops = StoredMetadataCorpus.HopsJson([_hop(ScopeDelta.FromPerspectiveScope(_scope))]);
    await _appendAsync(conn, ef, $$"""{"MessageId":"{{ef}}","Hops":{{hops}}}""", _scopeJson(_scope));
    await _appendAsync(conn, dapper, $$"""{"message_id":"{{dapper}}","hops":{{hops}}}""", _scopeJson(_scope));
    await _executeAsync(conn, "SELECT * FROM wh_distill_event_metadata(1000)");

    var (tracedSource, traced) = await _traceAsync(conn, ef);
    await _executeAsync(conn, "DELETE FROM wh_traces");
    var (rowSource, row) = await _traceAsync(conn, ef);
    var (_, dapperRow) = await _traceAsync(conn, dapper);

    await Assert.That(tracedSource).IsEqualTo("trace");
    await Assert.That(traced.GetProperty("MessageId").GetGuid()).IsEqualTo(ef).Because("the trace's answer names its message id");
    await Assert.That(rowSource).IsEqualTo("row");
    await Assert.That(row.GetProperty("MessageId").GetGuid()).IsEqualTo(ef).Because("so does the row's");
    await Assert.That(row.GetProperty("Hops")[0].GetProperty("sc").GetProperty("v").GetProperty("Sc").GetProperty("t").GetString())
      .IsEqualTo("tenant-a").Because("and the row's hop has its scope back, from the scope column");
    await Assert.That(dapperRow.GetProperty("message_id").GetGuid()).IsEqualTo(dapper)
      .Because("a row in the Dapper store's form gets its message id back under that store's key");
  }

  // ===== Helpers =====

  private static MessageHop _hop(ScopeDelta? scope) => new() {
    Type = HopType.Current,
    ServiceInstance = StoredMetadataCorpus.Orders,
    Timestamp = StoredMetadataCorpus.Sent,
    CorrelationId = StoredMetadataCorpus.Correlation,
    CausationId = StoredMetadataCorpus.Cause,
    Scope = scope,
  };

  private static string _scopeJson(PerspectiveScope scope) => JsonSerializer.Serialize(scope, _json.GetTypeInfo(typeof(PerspectiveScope)));

  /// <summary>Opens a connection on a database whose trim gate is open (StoredMetadataTrimGateSqlTests covers the gate).</summary>
  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await _executeAsync(connection, """
      INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES ('metadata_trim_row_values', 'true', 'boolean')
      ON CONFLICT (setting_key) DO NOTHING
      """);
    return connection;
  }

  private static Task<string?> _textAsync(NpgsqlConnection conn, Guid id, string key) =>
    _scalarAsync<string?>(conn, "SELECT metadata ->> @k FROM wh_event_body WHERE event_id = @e", ("e", id), ("k", key));

  private static Task<bool> _hasHopScopeAsync(NpgsqlConnection conn, Guid id) =>
    _scalarAsync<bool>(conn, "SELECT metadata -> 'Hops' -> 0 ? 'sc' FROM wh_event_body WHERE event_id = @e", ("e", id));

  private static async Task<(string Source, JsonElement Metadata)> _traceAsync(NpgsqlConnection conn, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT source, metadata::text FROM wh_trace_metadata(ARRAY[@e]::uuid[])";
    cmd.Parameters.AddWithValue("e", id);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    using var doc = JsonDocument.Parse(reader.GetString(1));
    return (reader.GetString(0), doc.RootElement.Clone());
  }

  private static async Task<(long Scanned, long Affected)> _convertAsync(NpgsqlConnection conn, bool reset = false) {
    if (reset) {
      await _executeAsync(conn, "DELETE FROM wh_metadata_history_conversion");
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT rows_scanned, rows_affected FROM wh_convert_event_metadata_history(100)";
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt64(0), reader.GetInt64(1));
  }

  /// <summary>
  /// Stores one event per entry through the outbox emit chain, each with its hop, its scope column and, when given, a
  /// message id in its envelope other than its row's event id. Returns the event ids in order.
  /// </summary>
  private static async Task<Guid[]> _emitOutboxAsync(NpgsqlConnection conn, params (MessageHop Hop, PerspectiveScope Scope, Guid? EnvelopeId)[] events) {
    var instanceId = (Guid)TrackedGuid.New();
    var ids = new List<Guid>();
    foreach (var (hop, scope, envelopeId) in events) {
      var id = (Guid)TrackedGuid.New();
      ids.Add(id);
      await _executeAsync(conn, """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
           status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
        VALUES
          (@id, 'row-values', @type, 'env',
           jsonb_build_object('id', @envelopeId::text, 'p', jsonb_build_object('n', 1), 'h', @hops::jsonb),
           '{}'::jsonb, @scope::jsonb, 1, 0, @inst, @lease, 0, @sid, true, 0, @now)
        """,
        ("id", id), ("envelopeId", envelopeId ?? id), ("type", ORDER_PLACED), ("hops", StoredMetadataCorpus.HopsJson([hop])),
        ("scope", _scopeJson(scope)), ("inst", instanceId), ("lease", _t0.AddMinutes(5)), ("sid", (Guid)TrackedGuid.New()), ("now", _t0));
    }
    await using var emit = conn.CreateCommand();
    emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, @lease, @now, 10000)";
    emit.Parameters.AddWithValue(nameof(ids), NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids.ToArray());
    emit.Parameters.AddWithValue("inst", instanceId);
    emit.Parameters.AddWithValue("lease", _t0.AddMinutes(5));
    emit.Parameters.AddWithValue("now", _t0);
    _ = await emit.ExecuteScalarAsync();
    return [.. ids];
  }

  private static async Task _appendAsync(NpgsqlConnection conn, Guid id, string metadataJson, string? scopeJson) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT wh_append_event(@id, @sid, @sid, split_part(@type, ',', 1), @type, '{"amount": 1}'::jsonb, @meta::jsonb, @scope::jsonb, @at, 0)
      """;
    cmd.Parameters.AddWithValue(nameof(id), id);
    cmd.Parameters.AddWithValue("sid", (Guid)TrackedGuid.New());
    cmd.Parameters.AddWithValue("type", ORDER_PLACED);
    cmd.Parameters.AddWithValue("meta", metadataJson);
    cmd.Parameters.AddWithValue("scope", NpgsqlDbType.Text, (object?)scopeJson ?? DBNull.Value);
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
