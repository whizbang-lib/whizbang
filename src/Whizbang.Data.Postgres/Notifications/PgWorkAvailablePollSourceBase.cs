using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Postgres-backed <see cref="BasePollSignalSource{TSignal}"/> for the work-available doorbell
/// signals. Concrete subclasses supply a detection query against the corresponding work table
/// (<c>wh_outbox</c>, <c>wh_inbox</c>, <c>wh_perspective_events</c>) and this base handles the
/// connection resolution, parameter binding, and safety-net logging.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Doorbell semantics.</strong> The subclass's SQL is an <c>EXISTS</c>-style detection
/// probe scoped to this pod's <see cref="IServiceInstanceProvider.InstanceId"/> — cheap, indexed,
/// and idempotent. It returns <c>true</c> when the target table has any unclaimed row assigned to
/// this instance; the subscriber (e.g. <c>ClaimWorker</c>) then runs the authoritative claim path.
/// A duplicate signal is harmless — the subscriber always fetches state from the DB.
/// </para>
/// <para>
/// <strong>NOTIFY-down backstop.</strong> When the push transport is healthy the poll runs at a
/// relaxed interval (correctness backstop); if NOTIFY is unavailable the pull source carries the
/// full wake load without any code change on the subscriber side.
/// </para>
/// </remarks>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public abstract partial class PgWorkAvailablePollSourceBase<TSignal>(
  TimeProvider clock,
  TimeSpan interval,
  IOptions<WhizbangNotificationOptions> options,
  IConfiguration configuration,
  IServiceInstanceProvider instanceProvider,
  ILogger logger,
  INotificationConnectionStringFallback? connectionStringFallback = null
) : BasePollSignalSource<TSignal>(clock, interval) where TSignal : ISignal, new() {
  private readonly WhizbangNotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly INotificationConnectionStringFallback? _connectionStringFallback = connectionStringFallback;

  /// <summary>
  /// SQL that returns a single boolean — <c>true</c> if any unclaimed work exists for this
  /// instance, <c>false</c> otherwise. The subclass parameter <c>@instance_id</c> is bound to
  /// <see cref="IServiceInstanceProvider.InstanceId"/> by the base class.
  /// </summary>
  protected abstract string DetectSql { get; }

  /// <inheritdoc />
  protected sealed override async ValueTask<bool> DetectAsync(CancellationToken cancellationToken) {
    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback);
    if (resolution.ConnectionString is null) {
      return false;   // no connection available — nothing to detect; NOTIFY path will drive us when the DB comes back
    }
    await using var conn = new NpgsqlConnection(resolution.ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand(DetectSql, conn);
    cmd.Parameters.AddWithValue("instance_id", _instanceProvider.InstanceId);
    var result = await cmd.ExecuteScalarAsync(cancellationToken);
    return result is bool b && b;
  }

  /// <inheritdoc />
  protected override void OnTickError(Exception ex) {
    // Log at Warning — a single failed poll is not fatal (the NOTIFY push path or the next tick
    // will retry), but sustained failures should be visible in observability.
    LogDetectFailed(_logger, typeof(TSignal).FullName ?? typeof(TSignal).Name, ex);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Work-available poll source {SignalType} detection failed; will retry on next tick")]
  static partial void LogDetectFailed(ILogger logger, string signalType, Exception ex);
}
