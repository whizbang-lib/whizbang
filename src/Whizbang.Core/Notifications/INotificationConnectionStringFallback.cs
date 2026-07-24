namespace Whizbang.Core.Notifications;

/// <summary>
/// Optional last-resort source for the LISTEN/NOTIFY + commit-order-stamper connection
/// string. Consulted by <see cref="NotificationConnectionStringResolver"/> only after
/// <see cref="WhizbangNotificationOptions.DirectConnectionString"/> and
/// <see cref="WhizbangNotificationOptions.ConnectionStringKey"/>-based lookups have
/// returned nothing.
/// <para>
/// The intended implementation pulls the connection string off the service's already-
/// registered EF Core <c>DbContext</c>, so consumers of <c>.WithDriver.Postgres&lt;TDbContext&gt;()</c>
/// don't need to duplicate connection-string configuration under <c>Whizbang:Database</c>
/// just to enable notifications.
/// </para>
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public interface INotificationConnectionStringFallback {
  /// <summary>
  /// Returns a connection string to use when explicit notification configuration is absent.
  /// Implementations should return <c>null</c> or empty when no fallback is available
  /// (the resolver treats both the same).
  /// </summary>
  string? GetConnectionString();

  /// <summary>
  /// Returns the PostgreSQL search path (the service's Whizbang schema) the notification
  /// layer's raw connections should use, or <c>null</c> when unknown. The raw components
  /// (signal transport, durable-signal tail, schedule claimer, poll sources, …) issue
  /// unqualified SQL against tables/functions that live in the SERVICE schema — without a
  /// search path those queries resolve against <c>public</c> and fail on multi-schema
  /// deployments. Consulted for EVERY resolution source (a config-supplied connection
  /// string still needs the schema); <see cref="WhizbangNotificationOptions.SearchPath"/>
  /// overrides it when set.
  /// </summary>
  string? GetSearchPath() => null;
}
