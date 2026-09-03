using Npgsql;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Last-resort source of an <b>authenticated</b> <see cref="NpgsqlDataSource"/> for the notification
/// workers (LISTEN/NOTIFY, commit-order stamping, duty election) when neither explicit notification
/// configuration nor a credential-bearing <c>ConnectionStrings</c> entry exists.
/// </summary>
/// <remarks>
/// <para>
/// Under <c>UseNpgsql(NpgsqlDataSource)</c> Npgsql redacts the password from every
/// <c>ConnectionString</c> surface — the connection's, the data source's, EF Core's resolved string —
/// so the string-based <see cref="Whizbang.Core.Notifications.INotificationConnectionStringFallback"/>
/// yields a string that cannot authenticate ("No password has been provided but the backend requires
/// one"). The data source itself still holds the credentials. The storage driver implements this
/// interface over the consumer's DbContext so auto-discovery can borrow that data source instead.
/// </para>
/// <para>
/// A borrowed data source is never disposed by the notification stack, and it is used as-is: the
/// dedicated pool sizing, application-name stamping and keepalive tuning that apply to a data source
/// the notification stack builds itself do not apply here. Configure
/// <c>Whizbang:Database:ConnectionStringKey</c> (or call <c>AddWhizbangNotificationDataSource</c>)
/// to get a dedicated pool.
/// </para>
/// </remarks>
/// <docs>data/drivers#bring-your-own-dbcontext</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/NotificationDataSourceAutoDiscoveryTests.cs</tests>
public interface INotificationDataSourceFallback {
  /// <summary>
  /// The credential-bearing data source the application already authenticates with, or <c>null</c>
  /// when the application has none to lend (a string-configured DbContext, a non-Npgsql provider).
  /// </summary>
  NpgsqlDataSource? GetDataSource();
}
