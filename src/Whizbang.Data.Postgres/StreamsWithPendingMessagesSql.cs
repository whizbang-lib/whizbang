using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The one query both Postgres work coordinators use to answer "which of these streams still have a
/// message of this type waiting?", shared so the two cannot drift on what counts as waiting.
/// </summary>
/// <remarks>
/// <para>
/// Waiting means an outbox row not yet published, scheduled or not, or an inbox row whose work is not
/// finished, whether unclaimed or being handled now. An inbox row being handled counts because the
/// handler has not yet written whatever it will write next; treating it as gone would open a window
/// in which a chain that is alive reads as ended.
/// </para>
/// <para>
/// Type names match by containment, like the discard sweeps: a stored <c>message_type</c> may carry
/// assembly version metadata or an envelope wrapper around the normalized name. Both branches filter
/// on <c>stream_id</c> first, which the tables index.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/completion-orchestration#stranded-sagas</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamsWithPendingMessagesSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamsWithPendingMessagesTests.cs</tests>
public static class StreamsWithPendingMessagesSql {

  /// <summary>Builds the query for a schema.</summary>
  /// <param name="schema">The schema holding the message tables, unquoted.</param>
  /// <returns>The query, taking <c>@stream_ids</c> (uuid[]) and <c>@type_names</c> (varchar[]).</returns>
  public static string Query(string schema) =>
    $"SELECT o.stream_id FROM \"{schema}\".wh_outbox o "
    + "WHERE o.stream_id = ANY(@stream_ids) AND o.processed_at IS NULL "
    + "AND EXISTS (SELECT 1 FROM unnest(@type_names) AS t(name) WHERE strpos(o.message_type, t.name) > 0) "
    + $"UNION SELECT s.stream_id FROM \"{schema}\".wh_inbox_state s "
    + $"JOIN \"{schema}\".wh_inbox i ON i.message_id = s.message_id "
    + "WHERE s.stream_id = ANY(@stream_ids) AND s.processed_at IS NULL "
    + "AND EXISTS (SELECT 1 FROM unnest(@type_names) AS t(name) WHERE strpos(i.message_type, t.name) > 0)";

  /// <summary>Runs the query on an open connection.</summary>
  /// <param name="command">A command on the connection to use, with its timeout already set.</param>
  /// <param name="schema">The schema holding the message tables, unquoted.</param>
  /// <param name="streamIds">The streams to check.</param>
  /// <param name="messageTypeNames">Normalized type names to look for.</param>
  /// <param name="cancellationToken">Cancels the query.</param>
  /// <returns>The subset of <paramref name="streamIds"/> with such a message waiting.</returns>
  public static async Task<IReadOnlySet<Guid>> ExecuteAsync(
      NpgsqlCommand command, string schema,
      IReadOnlyList<Guid> streamIds, IReadOnlyList<string> messageTypeNames,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(command);
    ArgumentNullException.ThrowIfNull(streamIds);
    ArgumentNullException.ThrowIfNull(messageTypeNames);
    var result = new HashSet<Guid>();
    if (streamIds.Count == 0 || messageTypeNames.Count == 0) {
      return result;
    }
    command.CommandText = Query(schema);
    var ids = PostgresArrayHelper.ToUuidArray([.. streamIds]);
    ids.ParameterName = "stream_ids";
    command.Parameters.Add(ids);
    var names = PostgresArrayHelper.ToVarcharArray([.. messageTypeNames]);
    names.ParameterName = "type_names";
    command.Parameters.Add(names);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      result.Add(reader.GetGuid(0));
    }
    return result;
  }
}
