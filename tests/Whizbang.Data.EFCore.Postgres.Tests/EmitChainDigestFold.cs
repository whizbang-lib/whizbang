// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Folds a seeded event into its <c>wh_stream_digests</c> bucket exactly as the emit chain does when it stores one.
/// </summary>
/// <remarks>
/// <para>
/// Integrity tests seed the event store with direct inserts so they can choose commit sequences and arrival times.
/// The emit chain does two things a direct insert does not: it stores the event, and it folds the event into its
/// digest bucket in the same statement (087's <c>digest_folds</c>). A store written only by the first half is one
/// the framework itself calls drift: the integrity sweep reports and heals every such bucket as an unaccounted
/// write path.
/// </para>
/// <para>
/// Epoch closure finds a lane from that bucket (193), so a seed that skips the fold describes a store production
/// does not have. The predicate and the bucket key here are the emit chain's: ephemeral events (flags &amp; 8) and
/// at-most-once occurrences are left out, and the origin is the zero uuid for a locally written event.
/// </para>
/// </remarks>
internal static class EmitChainDigestFold {
  /// <summary>Folds the stored event <paramref name="eventId"/> (and its body, if seeded) into its bucket.</summary>
  public static async Task FoldAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_stream_digests AS d
        (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
      SELECT COALESCE(es.origin_service_id, '00000000-0000-0000-0000-000000000000'::uuid),
             COALESCE(es.scope ->> 't', ''), es.event_type, es.stream_id,
             hashtextextended(es.event_id::text, 0), hashtextextended(es.event_id::text, 1), 1, NOW()
      FROM wh_event_store es
      LEFT JOIN wh_event_body eb ON eb.event_id = es.event_id
      WHERE es.event_id = @event
        AND COALESCE(es.flags, 0) & 8 = 0
        AND COALESCE((eb.metadata ->> 'deliveryGuarantee')::integer, 0) <> 1
      ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO UPDATE SET
        digest_lo = d.digest_lo # EXCLUDED.digest_lo,
        digest_hi = d.digest_hi # EXCLUDED.digest_hi,
        event_count = d.event_count + EXCLUDED.event_count,
        updated_at = EXCLUDED.updated_at
      """;
    cmd.Parameters.AddWithValue("event", eventId);
    await cmd.ExecuteNonQueryAsync();
  }
}
