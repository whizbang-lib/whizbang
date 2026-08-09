using Npgsql;
using Whizbang.Core.Notifications;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// How a notification component opens its Postgres connections. Prefers the
/// DI-registered <see cref="INotificationDataSource"/> — the only path that
/// works when the consumer configured EF Core via
/// <c>UseNpgsql(NpgsqlDataSource)</c>, where Npgsql strips credentials from
/// every public ConnectionString surface — and falls back to the resolver's
/// connection string otherwise.
/// </summary>
/// <remarks>
/// <see cref="PgSharedNotifyConnection"/> pioneered this dual path; the plan
/// centralizes it so every notification/signal/schedule component resolves
/// connections the same way instead of each building
/// <see cref="NpgsqlConnection"/> from a possibly credential-stripped string.
/// </remarks>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public sealed class NotificationConnectionPlan {
  private NotificationConnectionPlan(
      NpgsqlDataSource? dataSource,
      string? connectionString,
      NotificationConnectionStringResolver.ResolutionSource stringSource) {
    DataSource = dataSource;
    ConnectionString = connectionString;
    StringSource = stringSource;
  }

  /// <summary>The preferred data source, when one is registered and usable.</summary>
  public NpgsqlDataSource? DataSource { get; }

  /// <summary>The fallback connection string, or null when none resolved.</summary>
  public string? ConnectionString { get; }

  /// <summary>Provenance of <see cref="ConnectionString"/> — for diagnostics.</summary>
  public NotificationConnectionStringResolver.ResolutionSource StringSource { get; }

  /// <summary>True when connections will open through <see cref="DataSource"/>.</summary>
  public bool UsesDataSource => DataSource is not null;

  /// <summary>True when either path can open a connection.</summary>
  public bool IsAvailable => DataSource is not null || !string.IsNullOrEmpty(ConnectionString);

  /// <summary>
  /// Builds a plan from the optionally registered notification data source and
  /// the connection-string resolution the component already performs. A wrapper
  /// whose <see cref="INotificationDataSource.DataSource"/> is null (auto-
  /// discovery found nothing usable) is treated like no registration at all.
  /// </summary>
  public static NotificationConnectionPlan Create(
      INotificationDataSource? notificationDataSource,
      NotificationConnectionStringResolver.Resolution resolution) {
    ArgumentNullException.ThrowIfNull(resolution);
    return new NotificationConnectionPlan(
        notificationDataSource?.DataSource,
        resolution.ConnectionString,
        resolution.Source);
  }

  /// <summary>
  /// Opens a connection via the preferred path. Throws
  /// <see cref="InvalidOperationException"/> when <see cref="IsAvailable"/> is false.
  /// </summary>
  public async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default) {
    if (DataSource is not null) {
      return await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    if (string.IsNullOrEmpty(ConnectionString)) {
      throw new InvalidOperationException(
          "No notification connection is available: no INotificationDataSource is registered " +
          "and no connection string resolved. Configure WhizbangNotificationOptions " +
          "(DirectConnectionString or ConnectionStringKey) or register a notification data source.");
    }

    var connection = new NpgsqlConnection(ConnectionString);
    try {
      await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
      return connection;
    } catch {
      await connection.DisposeAsync().ConfigureAwait(false);
      throw;
    }
  }
}
