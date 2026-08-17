using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Notifications;
using Whizbang.Core.Signals;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Periodically scans <c>wh_service_instances</c> for pods whose lease/heartbeat has expired and,
/// for each newly-detected death, emits a durable <see cref="InstanceDiedSignal"/> on the bus.
/// The signal drives orphan takeover on live pods. Broadcast + Durable delivery means the fast-
/// path NOTIFY reaches subscribers instantly and the durable log carries the signal across NOTIFY
/// drops so failover is never silently lost.
/// </summary>
/// <remarks>
/// <para>
/// The monitor tracks the deaths it has already announced in-process (an id set) so it does not
/// republish the same InstanceDied every tick — the signal itself is idempotent (subscribers
/// fetch state from the DB anyway), but avoiding duplicates keeps observability + wh_signals
/// growth clean.
/// </para>
/// </remarks>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
/// <tests>tests/Whizbang.Core.Tests/Notifications/PgNotificationStackStartupGateTests.cs</tests>
public sealed partial class PgInstanceLifecycleMonitor(
  IOptions<WhizbangNotificationOptions> options,
  IConfiguration configuration,
  ISignalBus signalBus,
  ILogger<PgInstanceLifecycleMonitor> logger,
  INotificationConnectionStringFallback? connectionStringFallback = null,
  INotificationDataSource? notificationDataSource = null,
  Whizbang.Core.Workers.ISchemaReadyGate? schemaReadyGate = null
) : BackgroundService {
  private readonly WhizbangNotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
  private readonly ISignalBus _signalBus = signalBus ?? throw new ArgumentNullException(nameof(signalBus));
  private readonly ILogger<PgInstanceLifecycleMonitor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly INotificationConnectionStringFallback? _connectionStringFallback = connectionStringFallback;
  private readonly INotificationDataSource? _notificationDataSource = notificationDataSource;
  private readonly Whizbang.Core.Workers.ISchemaReadyGate? _schemaReadyGate = schemaReadyGate;

  /// <summary>Monitor tick interval — relaxed since failover latency is bounded by lease expiry.</summary>
  private static readonly TimeSpan _tickInterval = TimeSpan.FromSeconds(5);

  /// <summary>Heartbeat-stale threshold. Matches the notify_instance_owners active-window (30s).</summary>
  private static readonly TimeSpan _staleThreshold = TimeSpan.FromSeconds(30);

  private readonly HashSet<Guid> _announcedDeaths = [];

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    // Death detection reads wh_service_instances — hold at the schema gate so the first
    // tick never scans (or announces takeover from) a table the migration hasn't built yet.
    if (_schemaReadyGate is not null) {
      try {
        await _schemaReadyGate.WaitForReadyAsync(stoppingToken);
      } catch (OperationCanceledException) {
        return;
      }
    }

    while (!stoppingToken.IsCancellationRequested) {
      try {
        await _tickOnceAsync(stoppingToken);
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

  /// <summary>Test hook: run one detection tick without the loop.</summary>
  public Task TickForTestsAsync(CancellationToken cancellationToken) => _tickOnceAsync(cancellationToken);

  private async Task _tickOnceAsync(CancellationToken ct) {
    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback).WithAppliedSearchPath();
    // Prefer the registered notification data source - the only path that
    // works under UseNpgsql(NpgsqlDataSource), where the resolver's fallback
    // string has had its credentials stripped by Npgsql.
    var plan = NotificationConnectionPlan.Create(_notificationDataSource, resolution);
    if (!plan.IsAvailable) {
      return;
    }

    var dead = new List<Guid>();
    await using (var conn = await plan.OpenAsync(ct)) {
      await using var cmd = new NpgsqlCommand(@"
        SELECT instance_id
        FROM wh_service_instances
        WHERE last_heartbeat_at < NOW() - @stale", conn);
      cmd.Parameters.AddWithValue("stale", _staleThreshold);
      await using var reader = await cmd.ExecuteReaderAsync(ct);
      while (await reader.ReadAsync(ct)) {
        dead.Add(reader.GetGuid(0));
      }
    }

    foreach (var deadId in dead) {
      if (_announcedDeaths.Add(deadId)) {
        // First time we've seen this pod dead — announce it. The durable path INSERTs into
        // wh_signals and NOTIFY-broadcasts; subscribers on other pods use the signal to
        // trigger orphan takeover for the dead pod's owned streams.
        try {
          await _signalBus.PublishAsync(new InstanceDiedSignal(), SignalTarget.Broadcast, ct);
          LogInstanceDied(_logger, deadId);
        } catch (OperationCanceledException) {
          throw;
        } catch (Exception ex) {
          _announcedDeaths.Remove(deadId);   // retry on next tick
          LogPublishFailed(_logger, deadId, ex);
        }
      }
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "PgInstanceLifecycleMonitor: tick failed; will retry on next interval")]
  static partial void LogTickFailed(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information,
    Message = "PgInstanceLifecycleMonitor: announced InstanceDiedSignal for {InstanceId}")]
  static partial void LogInstanceDied(ILogger logger, Guid instanceId);

  [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
    Message = "PgInstanceLifecycleMonitor: failed to publish InstanceDiedSignal for {InstanceId}; will retry")]
  static partial void LogPublishFailed(ILogger logger, Guid instanceId, Exception ex);
}
