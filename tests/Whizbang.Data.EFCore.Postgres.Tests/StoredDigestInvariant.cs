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
  private const string TRUTH = """
    SELECT COALESCE(es.origin_service_id, '00000000-0000-0000-0000-000000000000'::uuid) AS origin_service_id,
           COALESCE(es.scope->>'t', '') AS scope_tenant, es.event_type, es.stream_id,
           bit_xor(hashtextextended(es.event_id::text, 0)) AS digest_lo,
           bit_xor(hashtextextended(es.event_id::text, 1)) AS digest_hi,
           COUNT(*)::int AS event_count,
           bit_xor(('x' || encode(substring(uuid_send(es.event_id) FROM 1 FOR 8), 'hex'))::bit(64)::bigint) AS id_lo,
           bit_xor(('x' || encode(substring(uuid_send(es.event_id) FROM 9 FOR 8), 'hex'))::bit(64)::bigint) AS id_hi
    FROM wh_event_store es
    WHERE es.stream_id = ANY(@sids::uuid[])
      AND COALESCE(es.flags, 0) & 8 = 0
      AND COALESCE(es.flags, 0) & 32 = 0
      AND split_part(es.event_type, ',', 1) <> ALL(@exempt::text[])
    GROUP BY 1, 2, 3, 4
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
      stored AS (SELECT * FROM wh_stream_digests WHERE stream_id = ANY(@sids::uuid[]))
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
      stored AS (SELECT * FROM wh_stream_digests WHERE stream_id = ANY(@sids::uuid[])),
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
