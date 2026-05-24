using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
public sealed class DbContextNotificationConnectionStringFallback : INotificationConnectionStringFallback {
  private readonly IServiceProvider _serviceProvider;
  private readonly Type _dbContextType;
  private string? _cached;
  private bool _resolved;
  private readonly object _gate = new();

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
        _cached = dbContext.Database.GetConnectionString();
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
}
