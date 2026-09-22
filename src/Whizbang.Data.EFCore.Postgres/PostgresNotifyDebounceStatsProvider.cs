using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Whizbang.Core.Observability;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="INotifyDebounceStatsProvider"/>: a single grouped
/// aggregate over <c>wh_notify_state</c> (migration 137). One cheap indexed read — no scans of
/// business data — grouped by payload kind.
/// </summary>
/// <docs>operations/observability/metrics#notify-debounce</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresNotifyDebounceStatsProviderTests.cs</tests>
public sealed class PostgresNotifyDebounceStatsProvider(
  NpgsqlDataSource dataSource,
  string schema = "public") : INotifyDebounceStatsProvider {

  // The schema is a configured identifier (the model's default schema), not user input, but the
  // table cannot be a bound parameter — so it is composed as a properly quoted identifier
  // (doubling any embedded quote) rather than interpolated raw.
  private readonly string _table =
    "\"" + schema.Replace("\"", "\"\"") + "\".wh_notify_state";

  /// <inheritdoc />
  public async Task<IReadOnlyList<NotifyDebounceKindStats>> GetStatsAsync(CancellationToken ct = default) {
    var results = new List<NotifyDebounceKindStats>();

    await using var connection = await dataSource.OpenConnectionAsync(ct);
    await using var cmd = new NpgsqlCommand(
      $"""
      SELECT payload_kind,
             COALESCE(SUM(fired_count), 0)::bigint,
             COALESCE(SUM(suppressed_count), 0)::bigint,
             COALESCE(MAX(effective_window_ms), 0)::int,
             COALESCE(MAX(rapid_run), 0)::int
      FROM {_table}
      GROUP BY payload_kind
      """, connection);

    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct)) {
      results.Add(new NotifyDebounceKindStats(
        reader.GetString(0),
        reader.GetInt64(1),
        reader.GetInt64(2),
        reader.GetInt32(3),
        reader.GetInt32(4)));
    }
    return results;
  }
}
