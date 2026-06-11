using System.Collections.Generic;

namespace Whizbang.Core.Workers;

/// <summary>
/// Configures a dedicated pool of long-lived PostgreSQL connections held by
/// the Whizbang background workers, bypassing pgbouncer for hot-path traffic.
/// </summary>
/// <remarks>
/// <para>
/// The motivating measurement: under steady consume load on dev slot 3
/// (2026-06-10, alpha 0.674.1-alpha.32), <c>DISCARD ALL</c> accounted for
/// <b>22.5 % of slot-3 DB time</b> — every short worker transaction returning
/// to the pgbouncer pool incurs a session-reset round-trip. Pinning the
/// worker connections eliminates that overhead for the loops that run them.
/// </para>
/// <para>
/// The pool is shared across all opted-in workers. <see cref="Size"/> controls
/// the parallelism vs. connection-budget trade-off:
/// <list type="bullet">
///   <item><description><c>Size=1</c> (default) — minimum conn budget. Per pod the total
///     direct PG count is <c>1 (LISTEN) + 1 (this pool) = 2</c>. Workers queue on a single
///     wire; HeartbeatWorker may wait behind a ClaimWorker call (~19 ms).</description></item>
///   <item><description><c>Size=2..4</c> — typical production range. Splits flush traffic
///     from claim traffic so heartbeats don't block claims.</description></item>
///   <item><description>Higher values rarely help; raising <c>Size</c> past the number of
///     tier-1+2 workers wastes a conn.</description></item>
/// </list>
/// </para>
/// <para>
/// Worker eligibility is tier-driven, NOT per-worker config (that surface gets
/// noisy fast at fleet scale). The opinionated tiers come from the measurement
/// that motivated this feature; <see cref="ExcludeWorkers"/> is the escape
/// hatch for the rare "this one worker is misbehaving on the pinned path" case.
/// </para>
/// </remarks>
/// <docs>fundamentals/workers/pinned-connection-pool</docs>
public sealed class WhizbangPinnedPoolOptions {
  /// <summary>
  /// Direct (non-pgbouncer) Npgsql connection string used by the pinned pool.
  /// When null/empty, the feature is a no-op regardless of <see cref="Enabled"/>
  /// — workers stay on the pooled connection (today's behaviour). This matches
  /// the safety pattern used by <c>WhizbangNotificationOptions.DirectConnectionString</c>.
  /// </summary>
  /// <remarks>
  /// Must point at the underlying PostgreSQL server directly (typically port
  /// <c>5432</c>), not the pgbouncer fronting port (typically <c>6432</c>).
  /// Otherwise pinning gives no benefit and may confuse pgbouncer's pool math.
  /// </remarks>
  public string? ConnectionString { get; set; }

  /// <summary>
  /// Master switch. When <c>false</c> (default), the pinned pool is not used
  /// even if <see cref="ConnectionString"/> is set — preserves today's
  /// behaviour. Set to <c>true</c> to opt the configured workers into pinning.
  /// </summary>
  public bool Enabled { get; set; }

  /// <summary>
  /// Number of pinned connections held open by the pool. Workers borrow and
  /// return per transaction; the connection itself stays open and pinned for
  /// the lifetime of the pool.
  /// </summary>
  /// <remarks>
  /// Default <c>1</c>. Raise as the worker fleet grows or if flush latency
  /// starts queuing behind ClaimWorker. See class remarks for sizing
  /// guidance. <c>Size=0</c> is treated as the feature being off.
  /// </remarks>
  public int Size { get; set; } = 1;

  /// <summary>
  /// When <c>true</c> (default), the tier-2 flush workers
  /// (<c>InboxHandlerWorker</c>, <c>OutboxCompletionFlushWorker</c>,
  /// <c>PerspectiveCompletionFlushWorker</c>, <c>FailureFlushWorker</c>)
  /// also borrow from the pinned pool.
  /// </summary>
  /// <remarks>
  /// Set to <c>false</c> to restrict pinning to tier-1 (<c>ClaimWorker</c>,
  /// <c>OutboxPublishWorker</c>, <c>HeartbeatWorker</c>, <c>LeaseRenewalWorker</c>)
  /// when the pool size is tight and flush workers can tolerate pgbouncer
  /// overhead.
  /// </remarks>
  public bool IncludeFlushWorkers { get; set; } = true;

  /// <summary>
  /// Names (CLR type names, without namespace) of workers to explicitly
  /// exclude from the pinned pool even if their tier would otherwise opt
  /// them in. Empty by default.
  /// </summary>
  /// <remarks>
  /// Use sparingly — this exists for the "we're investigating a specific
  /// worker's behaviour on the pinned path" case. Wholesale opt-out should
  /// happen via <see cref="Enabled"/> or <see cref="IncludeFlushWorkers"/>.
  /// </remarks>
  /// <example>
  /// <c>ExcludeWorkers: ["HeartbeatWorker", "LeaseRenewalWorker"]</c>
  /// </example>
  public IList<string> ExcludeWorkers { get; set; } = new List<string>();

  /// <summary>
  /// Per-connection lifetime cap (seconds) before the pool recycles the
  /// connection. Defaults to 1800 (30 minutes). Recycling guards against
  /// long-lived connection-state drift (TLS rekey, server-side parameter
  /// changes) without sacrificing the no-pgbouncer benefit.
  /// </summary>
  public int ConnectionLifetimeSeconds { get; set; } = 1_800;

  /// <summary>
  /// Maximum time (milliseconds) a worker waits to borrow a connection from
  /// the pool before throwing. Defaults to 5000. Workers should never block
  /// here in steady state; a non-zero timeout exists to surface starvation
  /// (pool too small, deadlock, etc.) instead of silently hanging.
  /// </summary>
  public int BorrowTimeoutMilliseconds { get; set; } = 5_000;
}
