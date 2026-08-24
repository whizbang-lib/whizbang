namespace Whizbang.Data.Postgres;

/// <summary>
/// The one SQL both Postgres work coordinators use to read durable redelivery observations
/// alongside an inbox store (topology arc phase 8.5, poison detection layer 2).
/// <para>
/// Shared verbatim so Dapper and EF Core cannot drift on the shape poison detection depends on —
/// the same reason the migrations themselves are shared verbatim by both runners.
/// </para>
/// <para>
/// It reads <c>wh_message_deduplication</c> DIRECTLY rather than taking the counts out of
/// <c>store_inbox_messages</c>' result set, so that function's signature and returned rows stay
/// byte-identical to what every existing caller and lock expects: exactly one row per NEWLY stored
/// message. The two statements travel in ONE command, so this still costs no extra round trip —
/// and the dedup row is written by the store either way.
/// </para>
/// </summary>
/// <docs>fundamentals/dispatcher/routing#poison-messages</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerPoisonQuarantineTests.cs</tests>
public static class InboxRedeliveryObservationSql {

#pragma warning disable CA1707 // Repo style: public const fields are ALL_CAPS_SNAKE per editorconfig.
  /// <summary>
  /// Second statement of the store command: aggregates the just-written dedup rows whose
  /// observation count is above one into <c>[{"m":"&lt;uuid&gt;","o":&lt;count&gt;}, …]</c>
  /// (<c>[]</c> when there are none), parsed by
  /// <c>Whizbang.Core.Messaging.InboxRedeliveryObservation.ParseProjection</c>. Takes the message
  /// ids as a <c>uuid[]</c> parameter named <c>observedIds</c>. Head of the query, up to and
  /// excluding the schema-qualified table name.
  /// </summary>
  public const string OBSERVATION_QUERY_PREFIX =
    "SELECT COALESCE(jsonb_agg(jsonb_build_object('m', d.message_id, 'o', d.observation_count, 'a', i.attempts)), '[]'::jsonb)::text FROM ";

  /// <summary>
  /// Middle of the query: joins the inbox so the projection can carry the row's PROCESSING ATTEMPT
  /// count alongside the delivery count. LEFT, deliberately — a message with no inbox row (never
  /// stored, or already processed and removed) must yield null rather than a fabricated zero.
  /// </summary>
  public const string OBSERVATION_QUERY_MIDDLE =
    "wh_message_deduplication d LEFT JOIN ";

  /// <summary>Tail of the observation query, following the second schema-qualified table name.</summary>
  public const string OBSERVATION_QUERY_SUFFIX =
    "wh_inbox i ON i.message_id = d.message_id "
    + "WHERE d.observation_count > 1 AND d.message_id = ANY(@observedIds)";
#pragma warning restore CA1707

  /// <summary>Builds the observation query for a schema prefix (<c>""</c> or <c>"schema."</c>).</summary>
  /// <param name="schemaPrefix">Schema prefix including the trailing dot, or empty.</param>
  /// <returns>The ready-to-execute observation query.</returns>
  public static string ObservationQuery(string schemaPrefix) =>
    OBSERVATION_QUERY_PREFIX + schemaPrefix + OBSERVATION_QUERY_MIDDLE + schemaPrefix + OBSERVATION_QUERY_SUFFIX;
}
