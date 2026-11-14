// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The three redelivery reconcile functions give the same answer and leave the same state without their temporary
/// working tables (#2181) as they did with them (336). Each trial runs a varied bundle through the definition 336
/// shipped, installed beside the current one under another name, and through the current one, from the same state,
/// and compares what each returned and what each left in the event store, the stream digests, the sealed epochs and
/// the settings.
/// </summary>
/// <remarks>
/// <para>
/// A pooled connection is reset (<c>DISCARD ALL</c>) each time the pool hands it out, which drops every temporary
/// table, so a function that made one made it again on almost every call (#2179 found it in the bundle answer). The
/// rewrite gathers the same rows into arrays, and this is the side-by-side check that nothing else changed.
/// </para>
/// <para>
/// Every trial runs inside one transaction under a savepoint that is rolled back after each definition, so both see
/// the same rows and the same <c>now()</c>. The data and the bundles come from a seeded generator, so a run is the same
/// on every machine. A comparison over trials that adopt nothing would prove nothing, so the test also requires the
/// trials to have adopted, refused by the first hop, refused as truncated, and refolded a sealed epoch.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/420_RedeliveryReconcilesKeepNoWorkTable.sql</code-under-test>
/// <docs>resilience/stream-integrity#redelivery-reconciles-keep-no-working-table</docs>
[Category("Integration")]
[Category("Shard5")]
public class RedeliveryReconcileEquivalenceSqlTests : EFCoreTestBase {
  private const string LEGACY_MIGRATION = "336_";
  private const string LEGACY_PREFIX = "_wh_legacy_";
  private const string TENANT_A = "tenant-a";
  private const string TENANT_B = "tenant-b";
  private const string TYPE_A = "Contracts.OrderPlaced, Contracts";
  private const string TYPE_B = "Contracts.OrderShipped, Contracts";
  private const string ORIGIN_NAME = "orders-service";
  private const string OTHER_NAME = "billing-service";
  private const string REFUSED_TRUNCATED = "repair refused: stream prefix truncated";
  private const int EPHEMERAL_FLAG = 8;
  private const int TRIALS = 60;

  private static readonly Guid _origin = new("01900000-0000-7000-8000-000000000a01");
  private static readonly Guid _other = new("01900000-0000-7000-8000-000000000b02");

  /// <summary>What the trials exercised, so a comparison that matched over nothing fails.</summary>
  private sealed class Coverage {
    public long Adopted { get; set; }
    public long RefusedForeign { get; set; }
    public long RefusedTruncated { get; set; }
    public long EpochsChanged { get; set; }
  }

  private sealed record Fixture(List<Guid> Events, List<Guid> Refused, string Baseline);

  [Test]
  public async Task AdoptRedeliveredOrigin_GivesWhatTheWorkTableVersionGaveAsync() {
    await _assertEquivalentAsync("wh_adopt_redelivered_origin", (fixture, random, trial) => {
      var bundle = _bundle(fixture, random, trial);
      return ("{0}($1, $2, $3)", [_uuid(bundle.Origin), _uuids(bundle.Ids), _longs(bundle.Sequences)]);
    }, reportsRefusals: false);
  }

  [Test]
  public async Task ReconcileBundleByFirstHop_GivesWhatTheWorkTableVersionGaveAsync() {
    await _assertEquivalentAsync("wh_reconcile_redelivered_bundle_by_first_hop", (fixture, random, trial) => {
      var bundle = _bundle(fixture, random, trial);
      return ("{0}($1, $2, $3, $4)",
        [_uuid(bundle.Origin), _text(bundle.Name), _uuids(bundle.Ids), _longs(bundle.Sequences)]);
    }, reportsRefusals: true);
  }

  [Test]
  public async Task ReconcileBundles_GivesWhatTheWorkTableVersionGaveAsync() {
    await _assertEquivalentAsync("wh_reconcile_redelivered_bundles", (fixture, random, trial) => {
      var bundles = Enumerable.Range(0, random.Next(1, 5)).Select(i => _bundle(fixture, random, trial + i)).ToList();
      var positions = bundles.SelectMany((b, i) => b.Ids.Select(_ => i + 1)).ToArray();
      return ("{0}($1, $2, $3, $4, $5)", [
        _uuids([.. bundles.Select(b => b.Origin)]),
        new NpgsqlParameter { Value = bundles.Select(b => (object?)b.Name ?? DBNull.Value).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text },
        new NpgsqlParameter { Value = positions, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer },
        _uuids([.. bundles.SelectMany(b => b.Ids)]),
        _longs([.. bundles.SelectMany(b => b.Sequences)]),
      ]);
    }, reportsRefusals: true);
  }

  // ─── The comparison ─────────────────────────────────────────────────────────────────────────────────────────────────

  private async Task _assertEquivalentAsync(
      string function, Func<Fixture, Random, int, (string Call, NpgsqlParameter[] Args)> nextCall, bool reportsRefusals) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _installLegacyAsync(conn, function);
    var random = new Random(2181);
    var fixture = await _seedAsync(conn, random);

    await _execAsync(conn, "BEGIN");
    var coverage = new Coverage();
    var mismatches = new List<string>();
    for (var trial = 0; trial < TRIALS; trial++) {
      var (call, args) = nextCall(fixture, random, trial);
      var counts = function == "wh_adopt_redelivered_origin"
        ? "r.adopted_count, 0, 0"
        : "r.adopted_count, r.refused_foreign_origin_count, r.refused_truncated_count";
      var legacy = await _runAsync(conn, _format(call, LEGACY_PREFIX + function), counts, args);
      var current = await _runAsync(conn, _format(call, function), counts, args);
      if (legacy.Text != current.Text) {
        mismatches.Add($"trial {trial}:\n  336:     {legacy.Text}\n  current: {current.Text}");
      }
      coverage.Adopted += current.Adopted;
      coverage.RefusedForeign += current.RefusedForeign;
      coverage.RefusedTruncated += current.RefusedTruncated;
      if (_epochs(current.State) != _epochs(fixture.Baseline)) {
        coverage.EpochsChanged++;
      }
    }
    await _execAsync(conn, "ROLLBACK");

    await Assert.That(mismatches).IsEmpty()
      .Because("the working rows moved from a temporary table into arrays; what the function does must not change");
    await Assert.That(coverage.Adopted).IsGreaterThan(0L).Because("a comparison over bundles that adopt nothing proves nothing");
    await Assert.That(coverage.EpochsChanged).IsGreaterThan(0L).Because("the sealed-epoch refold must be compared too");
    if (reportsRefusals) {
      await Assert.That(coverage.RefusedForeign).IsGreaterThan(0L).Because("the first-hop refusal must be compared too");
      await Assert.That(coverage.RefusedTruncated).IsGreaterThan(0L).Because("the truncated-refusal branch must be compared too");
    }
  }

  private sealed record Outcome(string Text, string State, long Adopted, long RefusedForeign, long RefusedTruncated);

  private static string _format(string call, string function) =>
    string.Format(System.Globalization.CultureInfo.InvariantCulture, call, function);

  /// <summary>One call under a savepoint: what it returned and the state it left, as text, then rolled back.</summary>
  private static async Task<Outcome> _runAsync(NpgsqlConnection conn, string call, string counts, NpgsqlParameter[] args) {
    await _execAsync(conn, "SAVEPOINT trial");
    var returned = new StringBuilder();
    long adopted = 0, foreign = 0, truncated = 0;
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = $"SELECT r::text, {counts} FROM {call} r";
      foreach (var arg in args) {
        cmd.Parameters.Add(arg.Clone());
      }
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        returned.Append(reader.GetString(0)).Append(';');
        adopted += reader.GetInt32(1);
        foreign += reader.GetInt32(2);
        truncated += reader.GetInt32(3);
      }
    }
    var state = await _stateAsync(conn);
    await _execAsync(conn, "ROLLBACK TO SAVEPOINT trial");
    return new Outcome(returned + " => " + state, state, adopted, foreign, truncated);
  }

  private static string _epochs(string state) => state.Split('|')[2];

  /// <summary>Everything the reconcile functions write, as one ordered text.</summary>
  private static async Task<string> _stateAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT concat_ws('|',
        (SELECT string_agg(format('%s:%s:%s', es.event_id, es.origin_service_id, es.origin_commit_sequence), ',' ORDER BY es.event_id)
         FROM wh_event_store es),
        (SELECT string_agg(d::text, ',' ORDER BY d.origin_service_id, d.scope_tenant, d.event_type, d.stream_id)
         FROM wh_stream_digests d),
        COALESCE((SELECT string_agg(e::text, ',' ORDER BY e.origin_service_id, e.scope_tenant, e.event_type, e.epoch_id)
                  FROM wh_digest_epochs e), 'no epochs'),
        (SELECT string_agg(format('%s=%s', s.setting_key, s.setting_value), ',' ORDER BY s.setting_key) FROM wh_settings s))
      """;
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  /// <summary>The definition 336 shipped, renamed so it sits beside the current one.</summary>
  private static async Task _installLegacyAsync(NpgsqlConnection conn, string function) {
    var sql = new PostgresMigrationProvider().GetMigrations().Single(m => m.Name.StartsWith(LEGACY_MIGRATION, StringComparison.Ordinal)).Sql;
    var head = $"CREATE OR REPLACE FUNCTION public.{function}(";
    var start = sql.IndexOf(head, StringComparison.Ordinal);
    var end = sql.IndexOf("$$ LANGUAGE plpgsql;", start, StringComparison.Ordinal) + "$$ LANGUAGE plpgsql;".Length;
    var legacy = sql[start..end].Replace(head, $"CREATE OR REPLACE FUNCTION public.{LEGACY_PREFIX}{function}(", StringComparison.Ordinal);
    await _execAsync(conn, legacy);
  }

  // ─── The data ───────────────────────────────────────────────────────────────────────────────────────────────────────

  private sealed record Bundle(Guid? Origin, string? Name, Guid[] Ids, long?[] Sequences);

  /// <summary>
  /// Streams of one to three events, each stored with no origin, the bundle origin with or without its sequence, or
  /// another origin; some ephemeral (no digest); most with a first hop naming the origin, another service, no service
  /// or the legacy long form. Two sealed lanes (the origin's and the local one) and children the inbox refused as
  /// truncated.
  /// </summary>
  private static async Task<Fixture> _seedAsync(NpgsqlConnection conn, Random random) {
    var events = new List<Guid>();
    for (var s = 0; s < 30; s++) {
      var stream = _guid(random);
      for (var v = 1; v <= random.Next(1, 4); v++) {
        events.Add(await _storeAsync(conn, random, stream, v));
      }
    }
    var refused = new List<Guid>();
    for (var i = 0; i < 4; i++) {
      refused.Add(await _refusedChildAsync(conn, random));
    }
    await _sealAsync(conn, _origin, closedThrough: 2, width: 4);
    await _sealAsync(conn, Guid.Empty, closedThrough: 100000, width: 64);
    await _execAsync(conn, """
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
      VALUES ('integrity_origin_generation', '3', 'integer', 'test seed')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """);
    return new Fixture(events, refused, await _stateAsync(conn));
  }

  private static async Task<Guid> _storeAsync(NpgsqlConnection conn, Random random, Guid stream, int version) {
    var eventId = _guid(random);
    var (origin, sequence) = random.Next(5) switch {
      0 or 1 => ((Guid?)null, (long?)null),
      2 => (_origin, null),
      3 => (_origin, random.Next(1, 20)),
      _ => (_other, random.Next(1, 20)),
    };
    var ephemeral = random.Next(6) == 0;
    var tenant = random.Next(2) == 0 ? TENANT_A : TENANT_B;
    var type = random.Next(2) == 0 ? TYPE_A : TYPE_B;
    await using (var store = conn.CreateCommand()) {
      store.CommandText = """
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
           commit_sequence, created_at, origin_service_id, origin_commit_sequence, flags)
        VALUES ($1, $2, $2, 'Order', $3, jsonb_build_object('t', $4::text), $5, nextval('wh_commit_seq'),
                NOW() - INTERVAL '2 hours', $6, $7, $8)
        """;
      store.Parameters.AddWithValue(eventId);
      store.Parameters.AddWithValue(stream);
      store.Parameters.AddWithValue(type);
      store.Parameters.AddWithValue(tenant);
      store.Parameters.AddWithValue(version);
      store.Parameters.Add(_uuid(origin));
      store.Parameters.Add(new NpgsqlParameter { Value = (object?)sequence ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
      store.Parameters.AddWithValue(ephemeral ? EPHEMERAL_FLAG : 0);
      await store.ExecuteNonQueryAsync();
    }
    if (!ephemeral) {
      await using var fold = conn.CreateCommand();
      fold.CommandText = "SELECT _wh_fold_stream_digests(ARRAY[$1], ARRAY[$2], ARRAY[$3], ARRAY[$4], ARRAY[$5], 'fold', now())";
      fold.Parameters.AddWithValue(origin ?? Guid.Empty);
      fold.Parameters.AddWithValue(tenant);
      fold.Parameters.AddWithValue(type);
      fold.Parameters.AddWithValue(stream);
      fold.Parameters.AddWithValue(eventId);
      await fold.ExecuteNonQueryAsync();
    }
    var hop = random.Next(5) switch {
      0 => _hop(ORIGIN_NAME.ToUpperInvariant()),
      1 => _hop(OTHER_NAME),
      2 => _hop("unknown"),
      3 => $$$"""{"ServiceInstance":{"ServiceName":"{{{ORIGIN_NAME}}}"}}""",
      _ => null,
    };
    if (hop is not null) {
      await using var body = conn.CreateCommand();
      body.CommandText = """
        INSERT INTO wh_event_body (event_id, event_data, metadata)
        VALUES ($1, '{}'::jsonb, jsonb_build_object('MessageId', $1::text, 'Hops', jsonb_build_array($2::jsonb)))
        """;
      body.Parameters.AddWithValue(eventId);
      body.Parameters.AddWithValue(hop);
      await body.ExecuteNonQueryAsync();
    }
    return eventId;
  }

  private static async Task<Guid> _refusedChildAsync(NpgsqlConnection conn, Random random) {
    var id = _guid(random);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, stream_id, scope)
        VALUES ($1, 'TestHandler', $2, '{}'::jsonb, '{}'::jsonb, $3, jsonb_build_object('t', $4::text))
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state (message_id, stream_id, received_at, priority, is_event, attempts, status, failure_reason, error)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 1, 99, $5 FROM m
      """;
    cmd.Parameters.AddWithValue(id);
    cmd.Parameters.AddWithValue(TYPE_A);
    cmd.Parameters.AddWithValue(_guid(random));
    cmd.Parameters.AddWithValue(TENANT_A);
    cmd.Parameters.AddWithValue(REFUSED_TRUNCATED);
    await cmd.ExecuteNonQueryAsync();
    return id;
  }

  private static async Task _sealAsync(NpgsqlConnection conn, Guid lane, long closedThrough, long width) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_digest_epoch_frontiers (origin_service_id, closed_through_epoch, epoch_width, updated_at)
      VALUES ($1, $2, $3, NOW())
      ON CONFLICT (origin_service_id) DO UPDATE SET closed_through_epoch = EXCLUDED.closed_through_epoch,
        epoch_width = EXCLUDED.epoch_width
      """;
    cmd.Parameters.AddWithValue(lane);
    cmd.Parameters.AddWithValue(closedThrough);
    cmd.Parameters.AddWithValue(width);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// One bundle: the origin (mostly the seeded one; sometimes another, the empty id or none), a first-hop name in
  /// any case or none, and one to five events (stored ones, refused children, an unknown id, repeats) with sequences
  /// that are missing, zero or set.
  /// </summary>
  private static Bundle _bundle(Fixture fixture, Random random, int trial) {
    // The first two trials name every stored event and every refused child, under the origin's name and then another
    // service's, so each branch is reached whatever the generator draws after them.
    if (trial < 2) {
      Guid[] all = [.. fixture.Events, .. fixture.Refused];
      return new Bundle(_origin, trial == 0 ? ORIGIN_NAME : OTHER_NAME, all,
        [.. all.Select((_, i) => (long?)(i % 3 == 0 ? null : i + 1))]);
    }
    Guid? origin = random.Next(8) switch {
      0 => _other,
      1 => Guid.Empty,
      2 => null,
      _ => _origin,
    };
    var name = random.Next(6) switch {
      0 => null,
      1 => "",
      2 => "Unknown",
      3 => OTHER_NAME,
      4 => ORIGIN_NAME.ToUpperInvariant(),
      _ => ORIGIN_NAME,
    };
    var count = random.Next(1, 6);
    var ids = new Guid[count];
    var sequences = new long?[count];
    for (var i = 0; i < count; i++) {
      ids[i] = random.Next(10) switch {
        0 => fixture.Refused[random.Next(fixture.Refused.Count)],
        1 => _guid(random),
        2 when i > 0 => ids[i - 1],
        _ => fixture.Events[random.Next(fixture.Events.Count)],
      };
      sequences[i] = random.Next(4) switch {
        0 => null,
        1 => 0,
        _ => random.Next(1, 30),
      };
    }
    return new Bundle(origin, name, ids, sequences);
  }

  private static Guid _guid(Random random) {
    var bytes = new byte[16];
    random.NextBytes(bytes);
    return new Guid(bytes);
  }

  private static string _hop(string service) =>
    $$"""{"ty":0,"si":{"sn":"{{service}}","ii":"00000000-0000-0000-0000-000000000001","hn":"host","pi":1},"ts":"2026-10-28T12:00:00+00:00"}""";

  private static NpgsqlParameter _uuid(Guid? value) =>
    new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid };

  private static NpgsqlParameter _uuids(Guid?[] values) =>
    new() { Value = values.Select(v => (object?)v ?? DBNull.Value).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid };

  private static NpgsqlParameter _uuids(Guid[] values) =>
    new() { Value = values, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid };

  private static NpgsqlParameter _longs(long?[] values) =>
    new() { Value = values, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint };

  private static NpgsqlParameter _text(string? value) =>
    new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }
}
