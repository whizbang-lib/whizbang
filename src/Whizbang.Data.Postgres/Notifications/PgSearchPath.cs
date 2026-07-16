using Npgsql;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Applies the resolved PostgreSQL search path to a connection string so the notification
/// layer's raw components (signal transport, durable-signal tail, schedule claimer, poll
/// sources, …) resolve unqualified Whizbang tables/functions in the SERVICE schema rather
/// than <c>public</c>. See
/// <see cref="Whizbang.Core.Notifications.NotificationConnectionStringResolver.Resolution.SearchPath"/>
/// for where the value comes from.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
/// <tests>tests/Whizbang.Core.Tests/Notifications/PgSearchPathTests.cs</tests>
public static class PgSearchPath {
  /// <summary>
  /// Returns <paramref name="connectionString"/> with <c>Search Path=<paramref name="searchPath"/></c>
  /// applied. No-ops when <paramref name="searchPath"/> is null/blank, or when the connection
  /// string already carries an explicit <c>Search Path</c> (operator config wins).
  /// </summary>
  public static string Apply(string connectionString, string? searchPath) {
    ArgumentNullException.ThrowIfNull(connectionString);
    if (string.IsNullOrWhiteSpace(searchPath)) {
      return connectionString;
    }
    var builder = new NpgsqlConnectionStringBuilder(connectionString);
    if (!string.IsNullOrWhiteSpace(builder.SearchPath)) {
      return connectionString;
    }
    builder.SearchPath = searchPath;
    return builder.ConnectionString;
  }

  /// <summary>
  /// Returns the resolution with <see cref="Apply"/> already run on its connection string —
  /// the one-call form every raw notification component uses right after
  /// <see cref="Whizbang.Core.Notifications.NotificationConnectionStringResolver.Resolve(Whizbang.Core.Notifications.WhizbangNotificationOptions, Microsoft.Extensions.Configuration.IConfiguration, Whizbang.Core.Notifications.INotificationConnectionStringFallback?)"/>.
  /// </summary>
  public static Whizbang.Core.Notifications.NotificationConnectionStringResolver.Resolution WithAppliedSearchPath(
      this Whizbang.Core.Notifications.NotificationConnectionStringResolver.Resolution resolution) {
    ArgumentNullException.ThrowIfNull(resolution);
    return resolution.ConnectionString is null
      ? resolution
      : resolution with { ConnectionString = Apply(resolution.ConnectionString, resolution.SearchPath) };
  }
}
