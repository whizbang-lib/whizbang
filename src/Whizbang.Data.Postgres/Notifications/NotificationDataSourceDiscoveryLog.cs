using Microsoft.Extensions.Logging;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Log surface for <see cref="INotificationDataSource"/> auto-discovery: which source the notification
/// stack ended up with matters when a LISTEN connection later fails to authenticate, so the choice is
/// stated once at resolution rather than inferred from the failure.
/// </summary>
internal static partial class NotificationDataSourceDiscoveryLog {
  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Notification data source: no credential-bearing connection string is configured; reusing the " +
              "application's own NpgsqlDataSource (borrowed — not disposed by the notification stack, and " +
              "used as-is without the dedicated pool sizing). Configure Whizbang:Database:ConnectionStringKey " +
              "or call AddWhizbangNotificationDataSource(...) for a dedicated pool.")]
  public static partial void ReusingApplicationDataSource(ILogger logger);
}
