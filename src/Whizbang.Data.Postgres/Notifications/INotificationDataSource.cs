using Npgsql;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Opt-in DI marker for an <see cref="NpgsqlDataSource"/> the notification
/// workers should use when opening connections.
/// </summary>
/// <remarks>
/// <para>
/// Wrapping the data source behind a dedicated interface is deliberate:
/// auto-resolving a bare <see cref="NpgsqlDataSource"/> from DI also picks up
/// the EF Core-owned data source (registered by <c>AddDbContext.UseNpgsql</c>),
/// whose connection pool is sized for EF Core's working set. The notification
/// workers run independently (a LISTEN/NOTIFY connection that lives the
/// process lifetime, and a leader-election lock connection), and routing them
/// through EF Core's pool exhausts it under load.
/// </para>
/// <para>
/// Consumers configured via <c>UseNpgsql(NpgsqlDataSource)</c> — where there's
/// no string-recoverable credential — should register a SEPARATE data source
/// for notifications:
/// <code>
/// var notifyDataSource = new NpgsqlDataSourceBuilder(connStr).Build();
/// services.AddSingleton&lt;INotificationDataSource&gt;(new NotificationDataSource(notifyDataSource));
/// </code>
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public interface INotificationDataSource {
  /// <summary>The data source the notification workers will open connections on.</summary>
  NpgsqlDataSource DataSource { get; }
}

/// <summary>
/// Concrete <see cref="INotificationDataSource"/> wrapper. Use this when
/// registering a dedicated data source for notification workers.
/// </summary>
public sealed class NotificationDataSource : INotificationDataSource {
  /// <inheritdoc/>
  public NpgsqlDataSource DataSource { get; }

  /// <summary>Wraps an existing <see cref="NpgsqlDataSource"/>.</summary>
  public NotificationDataSource(NpgsqlDataSource dataSource) {
    ArgumentNullException.ThrowIfNull(dataSource);
    DataSource = dataSource;
  }
}
