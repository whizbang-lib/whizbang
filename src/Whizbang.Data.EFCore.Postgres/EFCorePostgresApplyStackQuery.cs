using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Whizbang.Core.Lineage;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The Postgres apply-stack query: aggregates each stream's version-ordered event-type path from
/// event-store pointers (never bodies), run-length collapses it in SQL, and groups identical
/// shapes into <see cref="ApplyPathSignature"/> counts. On-demand and analytical — nothing here
/// runs on the hot path, and the aggregate's size scales with distinct shapes, not streams.
/// </summary>
/// <remarks>
/// The perspective filter resolves through the association registry
/// (<c>wh_message_associations</c>, <c>association_type = 'perspective'</c>) — the same view of
/// "which event types feed this perspective" the reaper's coverage gate uses. The scope filter is
/// JSONB containment on each event's scope. Both narrow the rows that form the paths, so a stream
/// none of whose events survive contributes no path at all.
/// </remarks>
/// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ApplyStackQuerySqlTests.cs</tests>
public sealed class EFCorePostgresApplyStackQuery : IApplyStackQuery {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly Type _dbContextType;

  /// <summary>Creates the query over the consumer's DbContext type, resolved per call from a fresh scope.</summary>
  public EFCorePostgresApplyStackQuery(IServiceScopeFactory scopeFactory, Type dbContextType) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(dbContextType);
    _scopeFactory = scopeFactory;
    _dbContextType = dbContextType;
  }

  // Per-stream collapsed paths, shared by the signature aggregation and the drill-in so the two
  // can never disagree about what a stream's path is. Run-length collapse is gaps-and-islands:
  // a new run starts where the type differs from its predecessor (version order); a run of two or
  // more collapses to one element with the '+' suffix.
  private const string PATHS_CTE = """
    WITH filtered AS (
      SELECT es.stream_id, es.version, es.event_type, es.created_at
      FROM {0}wh_event_store es
      WHERE (@p_perspective IS NULL OR es.event_type IN (
              SELECT ma.normalized_message_type
              FROM {0}wh_message_associations ma
              WHERE ma.association_type = 'perspective' AND ma.target_name = @p_perspective))
        AND (@p_scope IS NULL OR es.scope @> @p_scope::jsonb)
    ),
    runs AS (
      SELECT stream_id, version, event_type, created_at,
             CASE WHEN lag(event_type) OVER (PARTITION BY stream_id ORDER BY version)
                       IS DISTINCT FROM event_type THEN 1 ELSE 0 END AS run_break
      FROM filtered
    ),
    grouped AS (
      SELECT stream_id, event_type, created_at,
             SUM(run_break) OVER (PARTITION BY stream_id ORDER BY version) AS run_no
      FROM runs
    ),
    collapsed AS (
      SELECT stream_id, run_no,
             CASE WHEN COUNT(*) > 1 THEN MIN(event_type) || '+' ELSE MIN(event_type) END AS element,
             MAX(created_at) AS last_at
      FROM grouped
      GROUP BY stream_id, run_no
    ),
    paths AS (
      SELECT stream_id,
             array_agg(element ORDER BY run_no) AS path,
             MAX(last_at) AS head_at
      FROM collapsed
      GROUP BY stream_id
    )
    """;

  /// <inheritdoc />
  public async Task<IReadOnlyList<ApplyPathSignature>> GetPathSignaturesAsync(
      ApplyStackQueryOptions options,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(options);

    return await _withConnectionAsync(async (cmd, prefix) => {
#pragma warning disable S2077 // the schema prefix comes from the EF model, not user input — coordinator pattern
      // The settled/live split: persisted (folded) signatures union into the live computation —
      // but ONLY for the unfiltered whole-store view, because folded shapes carry no perspective
      // or scope identity to filter by. Filtered queries stay live-only.
      var includePersisted = options.PerspectiveName is null && options.ScopeJson is null;
      var tail = includePersisted
        ? """

        SELECT u.path, SUM(u.stream_count)::bigint AS stream_count,
               MIN(u.first_seen) AS first_seen, MAX(u.last_seen) AS last_seen
        FROM (
          SELECT path, COUNT(*) AS stream_count, MIN(head_at) AS first_seen, MAX(head_at) AS last_seen
          FROM paths
          GROUP BY path
          UNION ALL
          SELECT ap.path, ap.stream_count, ap.first_seen, ap.last_seen
          FROM {0}wh_apply_paths ap
        ) u
        GROUP BY u.path
        ORDER BY stream_count DESC, u.path
        LIMIT @p_max
        """
        : """

        SELECT path, COUNT(*) AS stream_count, MIN(head_at) AS first_seen, MAX(head_at) AS last_seen
        FROM paths
        GROUP BY path
        ORDER BY stream_count DESC, path
        LIMIT @p_max
        """;
      cmd.CommandText = string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        PATHS_CTE + tail, prefix);
#pragma warning restore S2077
      _addFilterParameters(cmd, options);
      cmd.Parameters.Add(new NpgsqlParameter("p_max", options.MaxSignatures));

      var signatures = new List<ApplyPathSignature>();
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        signatures.Add(new ApplyPathSignature(
          reader.GetFieldValue<string[]>(0),
          reader.GetInt64(1),
          _asUtc(reader.GetFieldValue<DateTime>(2)),
          _asUtc(reader.GetFieldValue<DateTime>(3))));
      }
      return (IReadOnlyList<ApplyPathSignature>)signatures;
    }, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<Guid>> GetStreamsForPathAsync(
      IReadOnlyList<string> path,
      ApplyStackQueryOptions options,
      int limit,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

    return await _withConnectionAsync(async (cmd, prefix) => {
#pragma warning disable S2077 // the schema prefix comes from the EF model, not user input — coordinator pattern
      cmd.CommandText = string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        PATHS_CTE + """

        SELECT stream_id
        FROM paths
        WHERE path = @p_path
        ORDER BY head_at DESC
        LIMIT @p_limit
        """, prefix);
#pragma warning restore S2077
      _addFilterParameters(cmd, options);
      cmd.Parameters.Add(new NpgsqlParameter("p_path", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = path.ToArray()
      });
      cmd.Parameters.Add(new NpgsqlParameter("p_limit", limit));

      var streams = new List<Guid>();
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        streams.Add(reader.GetGuid(0));
      }
      return (IReadOnlyList<Guid>)streams;
    }, cancellationToken).ConfigureAwait(false);
  }

  private static void _addFilterParameters(System.Data.Common.DbCommand cmd, ApplyStackQueryOptions options) {
    cmd.Parameters.Add(new NpgsqlParameter("p_perspective", NpgsqlTypes.NpgsqlDbType.Text) {
      Value = (object?)options.PerspectiveName ?? DBNull.Value
    });
    cmd.Parameters.Add(new NpgsqlParameter("p_scope", NpgsqlTypes.NpgsqlDbType.Text) {
      Value = (object?)options.ScopeJson ?? DBNull.Value
    });
  }

  private static DateTimeOffset _asUtc(DateTime value) =>
    new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

  private async Task<T> _withConnectionAsync<T>(
      Func<System.Data.Common.DbCommand, string, Task<T>> operation,
      CancellationToken cancellationToken) {
    using var scope = _scopeFactory.CreateScope();
    var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(_dbContextType);

    var schema = dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema();
    var prefix = string.IsNullOrWhiteSpace(schema) || schema == "public" ? "" : $"\"{schema}\".";

    await using var connectionScope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
      (NpgsqlConnection)dbContext.Database.GetDbConnection(), cancellationToken).ConfigureAwait(false);
    await using var cmd = connectionScope.Connection.CreateCommand();
    cmd.CommandTimeout = 30;
    return await operation(cmd, prefix).ConfigureAwait(false);
  }
}
