using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Tails <c>wh_signals</c> on a periodic interval, delivering any <see cref="SignalDeliveryClass.Durable"/>
/// signal appended since the last tick to the bus. Each pod maintains its own cursor in
/// <c>wh_signal_cursors</c>; new pods initialize their cursor to <c>MAX(wh_signals.id)</c> so they do not
/// replay pre-startup history. Broadcast-scoped durable rows deliver to every pod's tail; instance-scoped
/// durable rows deliver only to their targeted pod.
/// </summary>
/// <remarks>
/// <para>
/// The tail is <em>at-least-once + idempotent</em>: signals are doorbells; the subscriber fetches
/// authoritative state from the database. A duplicate delivery costs one extra state fetch. Cursor
/// advancement uses "advance to the max id we just consumed" — if the loop crashes after dispatch but
/// before cursor UPDATE, the next tick redelivers those rows.
/// </para>
/// </remarks>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
/// <tests>tests/Whizbang.Core.Tests/Notifications/PgNotificationStackStartupGateTests.cs</tests>
public sealed partial class PgDurableSignalTailWorker(
  IOptions<WhizbangNotificationOptions> options,
  IConfiguration configuration,
  IServiceInstanceProvider instanceProvider,
  ISignalSink sink,
  ILogger<PgDurableSignalTailWorker> logger,
  INotificationConnectionStringFallback? connectionStringFallback = null,
  INotificationDataSource? notificationDataSource = null,
  Whizbang.Core.Workers.ISchemaReadyGate? schemaReadyGate = null
) : BackgroundService {
  private readonly WhizbangNotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly ISignalSink _sink = sink ?? throw new ArgumentNullException(nameof(sink));
  private readonly ILogger<PgDurableSignalTailWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly INotificationConnectionStringFallback? _connectionStringFallback = connectionStringFallback;
  private readonly INotificationDataSource? _notificationDataSource = notificationDataSource;
  private readonly Whizbang.Core.Workers.ISchemaReadyGate? _schemaReadyGate = schemaReadyGate;

  /// <summary>Tail interval. Kept modest — the fast path is NOTIFY; this just plugs missed notifies.</summary>
  private static readonly TimeSpan _tickInterval = TimeSpan.FromSeconds(2);

  private Dictionary<string, SignalTypeEntry>? _wireNameToEntry;

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    // The very first act below is INSERTing this pod's cursor into wh_signal_cursors —
    // a table the migration creates. Hold at the schema gate before any of it.
    if (_schemaReadyGate is not null) {
      try {
        await _schemaReadyGate.WaitForReadyAsync(stoppingToken);
      } catch (OperationCanceledException) {
        return;
      }
    }

    _wireNameToEntry = _buildWireMap();

    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback).WithAppliedSearchPath();
    // Prefer the registered notification data source - the only path that
    // works under UseNpgsql(NpgsqlDataSource), where the resolver's fallback
    // string has had its credentials stripped by Npgsql.
    var plan = NotificationConnectionPlan.Create(_notificationDataSource, resolution);
    if (!plan.IsAvailable) {
      LogNoConnectionString(_logger);
      return;
    }

    // Initialize the cursor to the current MAX(id) on first startup so we don't replay pre-startup
    // history. If a cursor row already exists (this pod's second boot), leave it alone.
    try {
      await _initializeCursorAsync(plan, stoppingToken);
    } catch (OperationCanceledException) {
      return;
    } catch (Exception ex) {
      LogInitializeCursorFailed(_logger, ex);
    }

    while (!stoppingToken.IsCancellationRequested) {
      try {
        await _tickOnceAsync(plan, stoppingToken);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        LogTickFailed(_logger, ex);
      }

      try {
        await Task.Delay(_tickInterval, stoppingToken);
      } catch (OperationCanceledException) {
        break;
      }
    }
  }

  private async Task _initializeCursorAsync(NotificationConnectionPlan plan, CancellationToken ct) {
    await using var conn = await plan.OpenAsync(ct);
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_signal_cursors (instance_id, last_delivered_signal_id, updated_at)
      VALUES (@instance_id, COALESCE((SELECT MAX(id) FROM wh_signals), 0), NOW())
      ON CONFLICT (instance_id) DO NOTHING;", conn);
    cmd.Parameters.AddWithValue("instance_id", _instanceProvider.InstanceId);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private async Task _tickOnceAsync(NotificationConnectionPlan plan, CancellationToken ct) {
    var map = _wireNameToEntry;
    if (map is null || map.Count == 0) {
      return;   // no signal types discovered — nothing to dispatch
    }

    await using var conn = await plan.OpenAsync(ct);

    // Fetch rows > cursor scoped to this instance (broadcast OR my instance-target).
    // ORDER BY id caps memory and preserves emission order; batch size prevents runaway
    // reads if the tail falls far behind.
    await using var readCmd = new NpgsqlCommand(@"
      SELECT s.id, s.wire_name
      FROM wh_signals s
      JOIN wh_signal_cursors c ON c.instance_id = @instance_id
      WHERE s.id > c.last_delivered_signal_id
        AND (s.target_instance_id IS NULL OR s.target_instance_id = @instance_id)
      ORDER BY s.id
      LIMIT 500;", conn);
    readCmd.Parameters.AddWithValue("instance_id", _instanceProvider.InstanceId);

    long maxSeenId = 0;
    var dispatched = new List<(long Id, SignalTypeEntry Entry)>();
    await using (var reader = await readCmd.ExecuteReaderAsync(ct)) {
      while (await reader.ReadAsync(ct)) {
        var id = reader.GetInt64(0);
        var wireName = reader.GetString(1);
        if (id > maxSeenId) { maxSeenId = id; }
        if (map.TryGetValue(wireName, out var entry)) {
          dispatched.Add((id, entry));
        }
        // Unknown wire-names on the durable path are still "consumed" (cursor advances past
        // them) — they belong to types this pod doesn't know about, and no local subscriber
        // could handle them anyway. Retain the max-seen so we don't loop.
      }
    }

    foreach (var (_, entry) in dispatched) {
      try {
        var pending = entry.Dispatch(_sink, ct);
        if (!pending.IsCompletedSuccessfully) {
          await pending.ConfigureAwait(false);
        }
      } catch (Exception ex) {
        LogDispatchThrew(_logger, entry.WireName, ex);
      }
    }

    if (maxSeenId > 0) {
      await using var upd = new NpgsqlCommand(@"
        UPDATE wh_signal_cursors
           SET last_delivered_signal_id = GREATEST(last_delivered_signal_id, @max_id),
               updated_at = NOW()
         WHERE instance_id = @instance_id;", conn);
      upd.Parameters.AddWithValue("instance_id", _instanceProvider.InstanceId);
      upd.Parameters.AddWithValue("max_id", maxSeenId);
      await upd.ExecuteNonQueryAsync(ct);
    }
  }

  private static Dictionary<string, SignalTypeEntry> _buildWireMap() {
    var wireMap = new Dictionary<string, SignalTypeEntry>(StringComparer.Ordinal);
    foreach (var entry in SignalTypeRegistry.GetAll()) {
      wireMap[entry.WireName] = entry;
    }
    return wireMap;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "PgDurableSignalTailWorker: no connection string resolved; durable tail disabled")]
  static partial void LogNoConnectionString(ILogger logger);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "PgDurableSignalTailWorker: cursor initialization failed")]
  static partial void LogInitializeCursorFailed(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
    Message = "PgDurableSignalTailWorker: tick failed; will retry on next interval")]
  static partial void LogTickFailed(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
    Message = "PgDurableSignalTailWorker: dispatch for wire-name {WireName} threw; other signals continue")]
  static partial void LogDispatchThrew(ILogger logger, string wireName, Exception ex);
}
