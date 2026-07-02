using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Collective;

/// <summary>
/// Best-effort ensurer for the btree <em>expression indexes</em> a collective apply's WHERE needs (§7). The
/// composed predicate filters jsonb columns with <c>-&gt;&gt;</c> equality / <c>IN</c> — including the
/// <c>scope-&gt;&gt;'t'</c> tenant envelope added on every apply — which a <c>gin(data)</c>/<c>gin(scope)</c>
/// index cannot serve, so without a matching btree expression index every batch's keyset SELECT seq-scans the
/// whole table. <see cref="CollectivePredicateSqlCompiler{TModel}"/> records exactly the
/// <see cref="ReferencedJsonPath"/>s it emitted; this ensurer turns each into
/// <c>CREATE INDEX IF NOT EXISTS … ((&lt;expr&gt;))</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Once per process.</strong> A static guard set dedupes attempts so the DDL runs at most once per
/// (table, expression) per process — the first apply pays the (idempotent) <c>IF NOT EXISTS</c>; every
/// subsequent apply short-circuits with no round-trip.
/// </para>
/// <para>
/// <strong>PgBouncer-safe.</strong> A plain single-statement <c>CREATE INDEX IF NOT EXISTS</c> (its own implicit
/// transaction) — not <c>CONCURRENTLY</c>, which spans multiple transactions and breaks under PgBouncer
/// transaction pooling (the very pooling mode behind the production zombie-query issue). It takes a brief
/// <c>SHARE</c> lock while building; that one-time cost is far cheaper than a perpetual seq scan.
/// </para>
/// <para>
/// <strong>Non-fatal.</strong> An index is an optimization, not correctness — a failure (missing CREATE
/// privilege, lock timeout) is logged at Warning and the path is marked attempted so it neither retries-storms
/// nor fails the apply. A fresh process (pod restart) re-attempts.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
public static partial class CollectiveIndexEnsurer {
  // Guard key = "<table>|<columnExpression>". Value is unused (set semantics via ConcurrentDictionary).
  private static readonly ConcurrentDictionary<string, byte> _attempted = new(StringComparer.Ordinal);

  private const int POSTGRES_IDENT_MAX = 63;

  /// <summary>
  /// Ensure a btree expression index exists for each referenced jsonb path not already attempted this process.
  /// </summary>
  /// <param name="dbContext">Context whose connection runs the DDL.</param>
  /// <param name="paths">The paths the compiler recorded for this apply's WHERE.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Index creation is a best-effort optimization; any failure is logged with context (L35) and must not fail the apply.")]
  public static async Task EnsureAsync(
      DbContext dbContext,
      IReadOnlyList<ReferencedJsonPath> paths,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(paths);
    if (paths.Count == 0) {
      return;
    }

    var logger = dbContext.GetService<ILoggerFactory>()?.CreateLogger("Whizbang.Collective.IndexEnsurer");
    // Include the database in the guard key: in production a process serves one database (so this is the same
    // once-per-process behavior), but it keeps the guard correct if a process ever targets several databases
    // (e.g. a per-test database), so an index ensured in db A never suppresses ensuring it in db B.
    var dbName = dbContext.Database.GetDbConnection().Database ?? string.Empty;

    foreach (var path in paths) {
      var guardKey = dbName + "|" + path.Table + "|" + path.ColumnExpression;
      if (!_attempted.TryAdd(guardKey, 0)) {
        continue;  // already attempted (created or failed) this process for this database.
      }

      var indexName = _indexName(path.Table, path.ColumnExpression);
      // Table + expression are internal-derived identifiers (perspective table name, jsonb path), never user
      // input — no interpolation-injection surface. Parameterizing DDL identifiers isn't supported by Postgres.
      var ddl = "CREATE INDEX IF NOT EXISTS \"" + indexName + "\" ON \"" + path.Table +
        "\" ((" + path.ColumnExpression + "))";
      try {
        await dbContext.Database.ExecuteSqlRawAsync(ddl, cancellationToken).ConfigureAwait(false);
        if (logger is not null) {
          LogIndexEnsured(logger, indexName, path.Table, path.ColumnExpression);
        }
      } catch (OperationCanceledException) {
        // Cancellation is not an index failure — let the caller's token propagate, and allow a retry later.
        _attempted.TryRemove(guardKey, out _);
        throw;
      } catch (Exception ex) {
        if (logger is not null) {
          LogIndexEnsureFailed(logger, indexName, path.Table, path.ColumnExpression, ex);
        }
      }
    }
  }

  // "wh_per_draft_job" + "data->>'Status'" → "idx_wh_per_draft_job_data_status", clamped to Postgres's 63-char
  // identifier limit with a stable hash suffix when it would overflow (so distinct long paths never collide).
  private static string _indexName(string table, string columnExpression) {
    var expr = _sanitize(columnExpression);
    var full = "idx_" + table + "_" + expr;
    if (full.Length <= POSTGRES_IDENT_MAX) {
      return full;
    }
    var hash = _stableHash(table + "|" + columnExpression).ToString("x8", CultureInfo.InvariantCulture);
    var prefixBudget = POSTGRES_IDENT_MAX - 1 - hash.Length;  // room for '_' + hash
    return string.Concat(full.AsSpan(0, prefixBudget), "_", hash);
  }

  private static string _sanitize(string columnExpression) {
    // data->>'Status' → data_status ; scope->>'t' → scope_t
    var sb = new StringBuilder(columnExpression.Length);
    var lastUnderscore = false;
    foreach (var ch in columnExpression) {
      var c = char.ToLowerInvariant(ch);
      if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) {
        sb.Append(c);
        lastUnderscore = false;
      } else if (!lastUnderscore) {
        sb.Append('_');
        lastUnderscore = true;
      }
    }
    return sb.ToString().Trim('_');
  }

  private static uint _stableHash(string s) {
    // FNV-1a 32 — process-stable (not string.GetHashCode, which is randomized per process).
    const uint offset = 2166136261u;
    const uint prime = 16777619u;
    var hash = offset;
    foreach (var b in Encoding.UTF8.GetBytes(s)) {
      hash ^= b;
      hash *= prime;
    }
    return hash;
  }

  // ── LoggerMessage source-gen partials ────────────────────────────────
  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Ensured collective expression index {IndexName} ON {Table} (({ColumnExpression}))")]
  private static partial void LogIndexEnsured(ILogger logger, string IndexName, string Table, string ColumnExpression);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "Could not ensure collective expression index {IndexName} ON {Table} (({ColumnExpression})); apply proceeds without it (statement_timeout still bounds the scan) — create it manually if the seq scan is costly")]
  private static partial void LogIndexEnsureFailed(ILogger logger, string IndexName, string Table, string ColumnExpression, Exception exception);
}
