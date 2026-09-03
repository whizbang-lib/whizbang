using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Whizbang.Core.Notifications;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// <see cref="INotificationConnectionStringFallback"/> that lazily resolves the connection
/// string from a registered EF Core <c>DbContext</c>. Lets services wired via
/// <c>.WithDriver.Postgres&lt;TDbContext&gt;()</c> use the same connection string for
/// LISTEN/NOTIFY + commit-order stamping that EF Core already uses for storage —
/// without forcing operators to duplicate it under <c>Whizbang:Database</c>.
/// </summary>
/// <remarks>
/// Cached after the first successful lookup. The DbContext is resolved in a transient
/// scope solely to extract <c>Database.GetConnectionString()</c>; the context is disposed
/// immediately. Returns <c>null</c> if the registered DbContext doesn't surface a
/// connection string (e.g., it was configured with an open <c>DbConnection</c>).
/// </remarks>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public sealed class DbContextNotificationConnectionStringFallback
  : INotificationConnectionStringFallback, Whizbang.Data.Postgres.Notifications.INotificationDataSourceFallback {
  private readonly IServiceProvider _serviceProvider;
  private readonly Type _dbContextType;
  private string? _cached;
  private bool _resolved;
  private string? _cachedSearchPath;
  private bool _searchPathResolved;
  private NpgsqlDataSource? _cachedDataSource;
  private bool _dataSourceResolved;
  private readonly Lock _gate = new();

  /// <summary>
  /// Creates a fallback that resolves the connection string from the registered DbContext of
  /// <paramref name="dbContextType"/>. The type must be registered as a scoped service in
  /// <paramref name="serviceProvider"/>.
  /// </summary>
  public DbContextNotificationConnectionStringFallback(IServiceProvider serviceProvider, Type dbContextType) {
    ArgumentNullException.ThrowIfNull(serviceProvider);
    ArgumentNullException.ThrowIfNull(dbContextType);
    if (!typeof(DbContext).IsAssignableFrom(dbContextType)) {
      throw new ArgumentException(
        $"Type '{dbContextType.FullName}' is not a {nameof(DbContext)} subtype.",
        nameof(dbContextType));
    }
    _serviceProvider = serviceProvider;
    _dbContextType = dbContextType;
  }

  /// <inheritdoc />
  public string? GetConnectionString() {
    if (_resolved) {
      return _cached;
    }
    lock (_gate) {
      if (_resolved) {
        return _cached;
      }
      using var scope = _serviceProvider.CreateScope();
      var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(_dbContextType);
      try {
        _cached = _resolveCredentialBearingConnectionString(dbContext);
      } catch (InvalidOperationException) {
        // Non-relational provider (e.g., InMemory) — GetConnectionString throws. Treat as
        // "no fallback available" so the listener/stamper falls back to disabled rather than
        // crashing the host.
        _cached = null;
      }
      _resolved = true;
      return _cached;
    }
  }

  /// <inheritdoc />
  /// <remarks>
  /// Surfaces the EF Core model's default schema so the notification layer's raw connections
  /// resolve unqualified Whizbang tables/functions (<c>wh_signals</c>,
  /// <c>wh_claim_due_schedules</c>, …) in the SERVICE schema instead of <c>public</c>.
  /// Returns <c>null</c> for single-schema (public) deployments — no search path needed.
  /// </remarks>
  public string? GetSearchPath() {
    if (_searchPathResolved) {
      return _cachedSearchPath;
    }
    lock (_gate) {
      if (_searchPathResolved) {
        return _cachedSearchPath;
      }
      using var scope = _serviceProvider.CreateScope();
      var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(_dbContextType);
      try {
        _cachedSearchPath = dbContext.Model.GetDefaultSchema();
      } catch (InvalidOperationException) {
        // Non-relational provider — treat as "no schema known".
        _cachedSearchPath = null;
      }
      _searchPathResolved = true;
      return _cachedSearchPath;
    }
  }

  /// <inheritdoc />
  /// <remarks>
  /// Surfaces the <see cref="NpgsqlDataSource"/> a <c>UseNpgsql(NpgsqlDataSource)</c> context was
  /// configured with — the one object in the host that still holds the credentials Npgsql redacts
  /// from every connection-string surface. Read from the Npgsql options extension, the same way the
  /// generated schema initializer already does for <c>VACUUM</c>. Returns <c>null</c> for
  /// string-configured and non-Npgsql contexts, where the string path above already carries
  /// credentials. Cached after the first lookup, like the string.
  /// </remarks>
  /// <docs>data/drivers#bring-your-own-dbcontext</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DbContextNotificationConnectionStringFallbackTests.cs:GetDataSource_UseNpgsqlDataSource_ReturnsTheDbContextsDataSourceAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DutyElectionByoDataSourceE2ETests.cs</tests>
  public NpgsqlDataSource? GetDataSource() {
    // Resolved once per startup, so a single check under the lock is enough: no fast path, no
    // unreachable double-check.
    lock (_gate) {
      if (!_dataSourceResolved) {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(_dbContextType);
#pragma warning disable EF1001 // Internal EF Core API usage — the data source behind UseNpgsql(NpgsqlDataSource) is only exposed on the provider's options extension; the generated schema initializer reads it the same way
        _cachedDataSource = dbContext.GetService<IDbContextOptions>()
          .Extensions
          .OfType<Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal.NpgsqlOptionsExtension>()
          .FirstOrDefault()?.DataSource as NpgsqlDataSource;
#pragma warning restore EF1001
        _dataSourceResolved = true;
      }
      return _cachedDataSource;
    }
  }

  /// <summary>
  /// Tries to surface the original credential-bearing connection string from the
  /// DbContext, regardless of which <c>UseNpgsql(...)</c> overload configured it.
  /// </summary>
  /// <remarks>
  /// <para>Layered resolution — first hit wins:</para>
  /// <list type="number">
  /// <item>
  ///   <description>
  ///   <see cref="RelationalOptionsExtension.ConnectionString"/> — populated by
  ///   <c>UseNpgsql(string)</c>. The string is the original options-supplied
  ///   value and never gets stripped, so this path is the simplest and most
  ///   common.
  ///   </description>
  /// </item>
  /// <item>
  ///   <description>
  ///   The Npgsql data source via
  ///   <c>NpgsqlOptionsExtension.DataSource</c> — populated by
  ///   <c>UseNpgsql(NpgsqlDataSource)</c>. The data source returns a fresh
  ///   <see cref="NpgsqlConnection"/> whose <see cref="NpgsqlConnection.ConnectionString"/>
  ///   carries the original credentials (no Open has happened on that
  ///   connection instance yet, so Npgsql hasn't stripped them).
  ///   Also probed via <see cref="IServiceProvider"/> in case the consumer
  ///   registered the data source as a singleton.
  ///   </description>
  /// </item>
  /// <item>
  ///   <description>
  ///   <see cref="RelationalOptionsExtension.Connection"/> as
  ///   <see cref="NpgsqlConnection"/> — populated by
  ///   <c>UseNpgsql(NpgsqlConnection)</c>. Best-effort: if the connection has
  ///   not yet been opened, its <c>ConnectionString</c> still carries credentials;
  ///   once opened, Npgsql strips them and we can't recover. Consumers using
  ///   this overload in production are encouraged to set
  ///   <c>WhizbangNotificationOptions.ConnectionStringKey</c> for a config-only
  ///   bypass.
  ///   </description>
  /// </item>
  /// <item>
  ///   <description>
  ///   <see cref="RelationalDatabaseFacadeExtensions.GetConnectionString"/> —
  ///   last resort. After <c>OpenAsync</c> the live string is stripped of
  ///   credentials, but pre-Open it still has them. Kept as a backstop for
  ///   non-Npgsql relational providers a downstream consumer may have configured.
  ///   </description>
  /// </item>
  /// </list>
  /// </remarks>
  private static string? _resolveCredentialBearingConnectionString(DbContext dbContext) {
    var ext = dbContext.GetService<IDbContextOptions>()
      .Extensions.OfType<RelationalOptionsExtension>().FirstOrDefault();

    // Layer 1 — UseNpgsql(string).
    if (!string.IsNullOrEmpty(ext?.ConnectionString)) {
      return ext.ConnectionString;
    }

    // Layer 2 — UseNpgsql(NpgsqlConnection). Pre-Open, the connection's
    // ConnectionString still carries credentials (Npgsql only strips them
    // once OpenAsync completes). Recovers the password for that overload as
    // long as the fallback runs before EF Core has used the connection.
    if (ext?.Connection is NpgsqlConnection extConn &&
        !string.IsNullOrEmpty(extConn.ConnectionString)) {
      return extConn.ConnectionString;
    }

    // Layer 3 — last resort.
    //
    // NOTE: There is no string-based recovery path for
    // `UseNpgsql(NpgsqlDataSource)`. Both NpgsqlConnection.ConnectionString and
    // NpgsqlDataSource.ConnectionString strip credentials — the data source
    // retains them only internally for SCRAM auth. Consumers configured with
    // the DataSource overload must either:
    //   (a) register the NpgsqlDataSource as a DI singleton — both
    //       PgCommitOrderStamperWorker and PgSharedNotifyConnection accept an
    //       optional NpgsqlDataSource and will call OpenConnectionAsync on it
    //       directly, bypassing any string-based resolution; or
    //   (b) set WhizbangNotificationOptions.ConnectionStringKey to short-circuit
    //       this fallback at the resolver layer.
    return dbContext.Database.GetConnectionString();
  }
}
