// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// An event this service wrote stays this service's own when the integrity passes that give history received before
/// origins were recorded an origin run over it (#2573). Since the slim stored form (285) the row's first hop records no
/// service, so "the first hop names no service" stopped being evidence that the row was received: a service's own
/// reaction of a type another service owns was re-attributed to that owner by the re-attribution walk, and adopted the
/// origin of a redelivery bundle that carried it. Each row is written here through the real outbox emit chain, the path
/// every local event takes; the first hop's service is recovered from the staged full form or the event's route when the
/// row no longer holds it, and a row written by a route-stamping path that records none at all is this service's own.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/594_EventsWrittenHereStayLocal.sql</code-under-test>
/// <docs>resilience/stream-integrity#events-written-here-stay-local</docs>
[Category("Integration")]
[Category("Shard5")]
public class EventsWrittenHereStayLocalSqlTests : EFCoreTestBase {
  // This service is "orders"; the event type is in a domain another service ("billing-ünïcode") owns, as a reaction to
  // that service's event is when its type lives beside the event it answers.
  private const string REACTION_TYPE = "MyApp.Billing.InvoiceSucceeded, MyApp";
  private const string FINGERPRINT = "fingerprint-2573";
  private const string WRITE_FORM = "metadata_write_form";
  private static readonly DateTimeOffset _t0 = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
  private static readonly Guid _billing = Guid.Parse("0b000000-0000-0000-0000-0000000025b1");
  private static readonly string _billingName = StoredMetadataCorpus.Billing.ServiceName;

  private readonly List<WorkCoordinationDbContext> _contexts = [];

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator() {
    var ctx = CreateDbContext();
    _contexts.Add(ctx);
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
  }

  [After(Test)]
  public async Task DisposeContextsAsync() {
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
  }

  // ── the re-attribution walk ─────────────────────────────────────────────

  [Test]
  public async Task Reattribute_AnEventWrittenHere_WhoseSlimRowRecordsNoService_StaysLocalAsync() {
    await using var conn = await _openAsync();
    await _settingAsync(conn, WRITE_FORM, "Slim");   // nothing staged: the row is all there is
    var reaction = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);

    var result = await _reattributeAsync();

    await Assert.That(result.Reattributed).IsEqualTo(0).Because("a row this service wrote is its own, whatever its type");
    await Assert.That(await _originOfAsync(conn, reaction)).IsEqualTo(((Guid?)null, (long?)null));
  }

  [Test]
  public async Task Reattribute_AnEventWrittenHere_WhoseStagedFormNamesThisService_IsLeftAsync() {
    await using var conn = await _openAsync();
    var reaction = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);   // the default form stages the full form

    var result = await _reattributeAsync();

    await Assert.That(result).IsEqualTo(new OriginReattributionResult { HopDisagreements = 1, Advanced = true })
      .Because("the staged full form's first hop names this service, not the owner");
    await Assert.That(await _originOfAsync(conn, reaction)).IsEqualTo(((Guid?)null, (long?)null));
  }

  [Test]
  public async Task Reattribute_AnEventWrittenHere_WhoseRouteNamesThisService_IsLeftAsync() {
    await using var conn = await _openAsync();
    var reaction = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    await _distillAsync(conn);   // the staged form is consumed; the route keeps the hop's service

    var result = await _reattributeAsync();

    await Assert.That(result).IsEqualTo(new OriginReattributionResult { HopDisagreements = 1, Advanced = true })
      .Because("the route's first hop names this service, not the owner");
    await Assert.That(await _originOfAsync(conn, reaction)).IsEqualTo(((Guid?)null, (long?)null));
  }

  [Test]
  public async Task Reattribute_HistoryWhoseRouteNamesTheOwner_TakesTheOwnerAsync() {
    // The positive control: history the owner wrote, stored here without an origin, still reaches its owner when only
    // its route remembers who wrote it.
    await using var conn = await _openAsync();
    var received = await _emitHereAsync(conn, StoredMetadataCorpus.Billing);
    await _distillAsync(conn);

    var result = await _reattributeAsync();

    await Assert.That(result.Reattributed).IsEqualTo(1);
    await Assert.That(await _originOfAsync(conn, received)).IsEqualTo(((Guid?)_billing, (long?)null));
  }

  // ── the redelivery reconcile, one bundle ────────────────────────────────

  [Test]
  public async Task Adopt_AnEventWrittenHere_WhoseSlimRowRecordsNoService_IsRefusedAsync() {
    await using var conn = await _openAsync();
    await _settingAsync(conn, WRITE_FORM, "Slim");
    var reaction = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(_billing, _billingName, [reaction], [7]);

    await Assert.That(buckets!.Single().Adopted).IsEqualTo(0).Because("a bundle cannot give an origin to an event written here");
    await Assert.That(buckets!.Single().RefusedForeignOrigin).IsEqualTo(1);
    await Assert.That(await _originOfAsync(conn, reaction)).IsEqualTo(((Guid?)null, (long?)null));
  }

  [Test]
  public async Task Adopt_AnEventWrittenHere_WhoseStagedFormNamesThisService_IsRefusedAsync() {
    await using var conn = await _openAsync();
    var reaction = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(_billing, _billingName, [reaction], [7]);

    await Assert.That(buckets!.Single().RefusedForeignOrigin).IsEqualTo(1);
    await Assert.That(await _originOfAsync(conn, reaction)).IsEqualTo(((Guid?)null, (long?)null));
  }

  [Test]
  public async Task Adopt_HistoryWhoseRouteNamesTheBundlesOrigin_AdoptsAsync() {
    await using var conn = await _openAsync();
    var received = await _emitHereAsync(conn, StoredMetadataCorpus.Billing);
    await _distillAsync(conn);

    var buckets = await _coordinator().AdoptRedeliveredOriginAsync(_billing, _billingName, [received], [7]);

    await Assert.That(buckets!.Single().Adopted).IsEqualTo(1);
    await Assert.That(await _originOfAsync(conn, received)).IsEqualTo(((Guid?)_billing, (long?)7L));
  }

  // ── the redelivery reconcile, a batch ──────────────────────────────────

  [Test]
  public async Task Batch_AnEventWrittenHere_WhoseSlimRowRecordsNoService_IsRefusedAsync() {
    await using var conn = await _openAsync();
    await _settingAsync(conn, WRITE_FORM, "Slim");
    var reaction = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);

    var answers = await _coordinator().ReconcileRedeliveredBundlesAsync([new RedeliveredBundle(_billing, _billingName, [reaction], [7])]);

    await Assert.That(answers!.Single().Single().Adopted).IsEqualTo(0);
    await Assert.That(answers!.Single().Single().RefusedForeignOrigin).IsEqualTo(1);
    await Assert.That(await _originOfAsync(conn, reaction)).IsEqualTo(((Guid?)null, (long?)null));
  }

  [Test]
  public async Task Batch_HistoryWhoseStagedFormNamesTheBundlesOrigin_AdoptsAsync() {
    await using var conn = await _openAsync();
    var received = await _emitHereAsync(conn, StoredMetadataCorpus.Billing);

    var answers = await _coordinator().ReconcileRedeliveredBundlesAsync([new RedeliveredBundle(_billing, _billingName, [received], [7])]);

    await Assert.That(answers!.Single().Single().Adopted).IsEqualTo(1);
    await Assert.That(await _originOfAsync(conn, received)).IsEqualTo(((Guid?)_billing, (long?)7L));
  }

  // ── the first hop's service, wherever it is kept ────────────────────────

  [Test]
  [Arguments("""{"Hops":[{"si":{"sn":" Orders "}}]}""", "orders")]
  [Arguments("""{"hops":[{"si":{"sn":"orders"}}]}""", "orders")]
  [Arguments("""{"Hops":[{"ServiceInstance":{"ServiceName":"Orders"}}]}""", "orders")]
  [Arguments("""{"Hops":[{"si":{"sn":""}}]}""", null)]
  [Arguments("""{"Hops":[]}""", null)]
  [Arguments("{}", null)]
  public async Task FirstHopService_ReadsTheRowsOwnHop_InEveryFormAsync(string metadata, string? expected) {
    await using var conn = await _openAsync();

    var service = await _firstHopServiceAsync(conn, TrackedGuid.New().Value, metadata);

    await Assert.That(service).IsEqualTo(expected);
  }

  [Test]
  public async Task FirstHopService_ForASlimRow_ReadsTheStagedForm_ThenTheRouteAsync() {
    await using var conn = await _openAsync();
    var staged = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);
    var stagedService = await _firstHopServiceAsync(conn, staged, await _bodyMetaAsync(conn, staged));
    await _distillAsync(conn);

    var routedService = await _firstHopServiceAsync(conn, staged, await _bodyMetaAsync(conn, staged));

    await Assert.That(stagedService).IsEqualTo(StoredMetadataCorpus.Orders.ServiceName);
    await Assert.That(routedService).IsEqualTo(StoredMetadataCorpus.Orders.ServiceName)
      .Because("once the staged form is distilled, the route keeps the first hop's service");
  }

  [Test]
  public async Task FirstHopService_ForASlimRowNothingElseRemembers_IsNullAsync() {
    await using var conn = await _openAsync();
    await _settingAsync(conn, WRITE_FORM, "Slim");
    var slim = await _emitHereAsync(conn, StoredMetadataCorpus.Orders);

    var service = await _firstHopServiceAsync(conn, slim, await _bodyMetaAsync(conn, slim));

    await Assert.That(service).IsNull();
  }

  // ── helpers ────────────────────────────────────────────────────────────

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private async Task<OriginReattributionResult> _reattributeAsync() {
    var coordinator = _coordinator();
    var window = (await coordinator.GetOriginReattributionWindowAsync(FINGERPRINT, 100))!;
    return (await coordinator.ReattributeOriginsAsync(new OriginReattributionBatch {
      AfterCommitSequence = window.AfterCommitSequence,
      ThroughCommitSequence = window.ThroughCommitSequence,
      Targets = [new OriginReattributionTarget { EventType = REACTION_TYPE, OwnerServiceId = _billing, OwnerServiceName = _billingName }],
    }))!;
  }

  /// <summary>Stores one event through the outbox emit chain, its first hop written by <paramref name="writer"/>, and
  /// stamps its commit sequence as the stamper would.</summary>
  private static async Task<Guid> _emitHereAsync(NpgsqlConnection conn, ServiceInstanceInfo writer) {
    var instanceId = (Guid)TrackedGuid.New();
    var id = (Guid)TrackedGuid.New();
    var hops = StoredMetadataCorpus.HopsJson([StoredMetadataCorpus.RichHop(writer)]);
    await _executeAsync(conn, """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
         status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
      VALUES
        (@id, 'reaction-topic', @type, 'env',
         jsonb_build_object('id', @id::text, 'p', jsonb_build_object('n', 1), 'h', @hops::jsonb),
         '{}'::jsonb, jsonb_build_object('t', 'tenant-a'),
         1, 0, @inst, @lease, 0, @sid, true, 0, @now)
      """,
      ("id", id), ("type", REACTION_TYPE), ("hops", hops), ("inst", instanceId), ("lease", _t0.AddMinutes(5)),
      ("sid", (Guid)TrackedGuid.New()), ("now", _t0));
    await using (var emit = conn.CreateCommand()) {
      emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, @lease, @now, 10000)";
      emit.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, new[] { id });
      emit.Parameters.AddWithValue("inst", instanceId);
      emit.Parameters.AddWithValue("lease", _t0.AddMinutes(5));
      emit.Parameters.AddWithValue("now", _t0);
      _ = await emit.ExecuteScalarAsync();
    }
    await _executeAsync(conn, "UPDATE wh_event_store SET commit_sequence = nextval('wh_commit_seq') WHERE event_id = @id", ("id", id));
    return id;
  }

  private static Task _distillAsync(NpgsqlConnection conn) => _executeAsync(conn, "SELECT wh_distill_event_metadata(1000)");

  private static Task _settingAsync(NpgsqlConnection conn, string key, string value) =>
    _executeAsync(conn, """
      INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES (@k, @v, 'string')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """, ("k", key), ("v", value));

  private static async Task<string> _bodyMetaAsync(NpgsqlConnection conn, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT metadata::text FROM wh_event_body WHERE event_id = @id";
    cmd.Parameters.AddWithValue(nameof(id), id);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<string?> _firstHopServiceAsync(NpgsqlConnection conn, Guid eventId, string metadata) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT _wh_first_hop_service(@id, @meta::jsonb)";
    cmd.Parameters.AddWithValue("id", eventId);
    cmd.Parameters.AddWithValue("meta", metadata);
    var value = await cmd.ExecuteScalarAsync();
    return value is DBNull ? null : (string?)value;
  }

  private static async Task<(Guid? Origin, long? Sequence)> _originOfAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT origin_service_id, origin_commit_sequence FROM wh_event_store WHERE event_id = $1";
    cmd.Parameters.Add(new NpgsqlParameter { Value = eventId });
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (await reader.IsDBNullAsync(0) ? null : reader.GetGuid(0), await reader.IsDBNullAsync(1) ? null : reader.GetInt64(1));
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
