// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2106: the invariant every stream digest writer keeps. After any write, each stored row of the named streams equals
/// a fold of the events, computed here independently of the helper under test: the 128-bit hash lanes and the count
/// (unchanged since 087, so a refactor that moves a single bit fails), and the id lanes (the XOR of each event id's two
/// 64-bit halves), which every row a writer creates from scratch carries; a row whose id lanes are NULL was not computed
/// yet and keeps NULL until the backfill fills it.
/// </summary>
/// <docs>resilience/stream-integrity#one-digest-fold</docs>
internal static class StoredDigestInvariant {
  // The fold the stored rows must equal, written out by hand on purpose: it is the oracle, not the helper.
  // #2364: a superseding key counts once, as its slot (md5 of stream, group and key), through its oldest held copy, in
  // that copy's type bucket (or the type it inherited from a deleted older copy).
  private const string TRUTH = """
    SELECT COALESCE(es.origin_service_id, '00000000-0000-0000-0000-000000000000'::uuid) AS origin_service_id,
           COALESCE(es.scope->>'t', '') AS scope_tenant,
           CASE WHEN es.supersede_key IS NULL THEN es.event_type ELSE COALESCE(es.supersede_slot_type, es.event_type) END AS event_type,
           es.stream_id,
           bit_xor(hashtextextended(x.id::text, 0)) AS digest_lo,
           bit_xor(hashtextextended(x.id::text, 1)) AS digest_hi,
           COUNT(*)::int AS event_count,
           bit_xor(('x' || encode(substring(uuid_send(x.id) FROM 1 FOR 8), 'hex'))::bit(64)::bigint) AS id_lo,
           bit_xor(('x' || encode(substring(uuid_send(x.id) FROM 9 FOR 8), 'hex'))::bit(64)::bigint) AS id_hi
    FROM wh_event_store es
    CROSS JOIN LATERAL (SELECT CASE WHEN es.supersede_key IS NULL THEN es.event_id
                                    ELSE md5(es.stream_id::text || '|' || COALESCE(es.supersede_group, es.event_type)
                                             || '|' || es.supersede_key)::uuid END AS id) x
    WHERE es.stream_id = ANY(@sids::uuid[])
      AND COALESCE(es.flags, 0) & 8 = 0
      AND COALESCE(es.flags, 0) & 32 = 0
      AND split_part(es.event_type, ',', 1) <> ALL(@exempt::text[])
      AND NOT (es.supersede_key IS NOT NULL AND EXISTS (
        SELECT 1 FROM wh_event_store o
        WHERE o.stream_id = es.stream_id
          AND COALESCE(o.supersede_group, o.event_type) = COALESCE(es.supersede_group, es.event_type)
          AND o.supersede_key = es.supersede_key
          AND o.event_id <> es.event_id
          AND CASE WHEN o.origin_service_id IS NOT NULL AND o.origin_service_id = es.origin_service_id
                        AND o.origin_commit_sequence IS NOT NULL AND es.origin_commit_sequence IS NOT NULL
                   THEN o.origin_commit_sequence < es.origin_commit_sequence
                   ELSE o.version < es.version END))
    GROUP BY 1, 2, 3, 4
    """;

  // #2363: a stored bucket is its live events plus what cuts carried; its live part is what equals the fold. A bucket
  // whose live part is empty (its whole stream was cut) has no events to compare with and is left out.
  private const string STORED = """
    SELECT origin_service_id, scope_tenant, event_type, stream_id,
           digest_lo # COALESCE(carried_lo, 0) AS digest_lo, digest_hi # COALESCE(carried_hi, 0) AS digest_hi,
           event_count - COALESCE(carried_count, 0) AS event_count,
           id_lo # COALESCE(carried_id_lo, 0) AS id_lo, id_hi # COALESCE(carried_id_hi, 0) AS id_hi
    FROM wh_stream_digests
    WHERE stream_id = ANY(@sids::uuid[])
      AND NOT (event_count - COALESCE(carried_count, 0) = 0
               AND digest_lo # COALESCE(carried_lo, 0) = 0 AND digest_hi # COALESCE(carried_hi, 0) = 0)
    """;

  /// <summary>The hash lanes and counts of the named streams equal a fold of their events, row for row.</summary>
  /// <returns>The number of stored rows compared.</returns>
  public static async Task<long> AssertHashLanesAgreeAsync(NpgsqlConnection connection, params Guid[] streams) {
    var (differing, stored) = await _compareAsync(connection, streams,
      "origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count");
    await Assert.That(differing).IsEqualTo(0L)
      .Because("every stored digest row must equal a fold of its events; the write left a different hash lane or count");
    await Assert.That(stored).IsGreaterThan(0L).Because("precondition: the scenario wrote digest rows to compare");
    return stored;
  }

  /// <summary>
  /// The id lanes of the named streams: every row that carries them equals a fold of its events, and exactly
  /// <paramref name="filled"/> rows carry them. A row the scenario seeded by hand (no id lanes) keeps NULL: the fold
  /// never turns a partial value into a present one.
  /// </summary>
  public static async Task AssertIdLanesAgreeAsync(NpgsqlConnection connection, int filled, params Guid[] streams) {
    await using var cmd = connection.CreateCommand();
#pragma warning disable S2077 // The oracle is the constant above; every value is bound.
    cmd.CommandText = $"""
      WITH truth AS ({TRUTH}),
      stored AS ({STORED})
      SELECT COUNT(*) FILTER (WHERE (s.id_lo IS NOT NULL OR s.id_hi IS NOT NULL)
                                AND (s.id_lo IS DISTINCT FROM t.id_lo OR s.id_hi IS DISTINCT FROM t.id_hi)),
             COUNT(*) FILTER (WHERE s.id_lo IS NOT NULL OR s.id_hi IS NOT NULL)
      FROM stored s
      LEFT JOIN truth t USING (origin_service_id, scope_tenant, event_type, stream_id)
      """;
#pragma warning restore S2077
    _bind(cmd, streams);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    await Assert.That(reader.GetInt64(0)).IsEqualTo(0L)
      .Because("a row's id lanes are the XOR of its events' id halves, or NULL; never another value");
    await Assert.That(reader.GetInt64(1)).IsEqualTo((long)filled)
      .Because("every row the writer folded from scratch carries its id lanes, and a row seeded without them keeps NULL");
  }

  /// <summary>Both, on rows the scenario wrote entirely through writers: the hash lanes (the lock on today's values),
  /// then id lanes present on every row.</summary>
  public static async Task AssertAgreeAsync(NpgsqlConnection connection, params Guid[] streams) {
    var rows = await AssertHashLanesAgreeAsync(connection, streams);
    await AssertIdLanesAgreeAsync(connection, (int)rows, streams);
  }

  private static void _bind(NpgsqlCommand cmd, Guid[] streams) {
    cmd.Parameters.AddWithValue("sids", streams);
    cmd.Parameters.AddWithValue("exempt", Whizbang.Core.SystemEvents.DigestExemptTypes.TypeNames.ToArray());
  }

  private static async Task<(long Differing, long Stored)> _compareAsync(NpgsqlConnection connection, Guid[] streams, string columns) {
    await using var cmd = connection.CreateCommand();
#pragma warning disable S2077 // The column lists are the two constants above; every value is bound.
    cmd.CommandText = $"""
      WITH truth AS ({TRUTH}),
      stored AS ({STORED}),
      t AS (SELECT {columns} FROM truth),
      s AS (SELECT {columns} FROM stored)
      SELECT (SELECT COUNT(*) FROM ((TABLE t EXCEPT TABLE s) UNION ALL (TABLE s EXCEPT TABLE t)) d),
             (SELECT COUNT(*) FROM stored)
      """;
#pragma warning restore S2077
    _bind(cmd, streams);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt64(0), reader.GetInt64(1));
  }
}
