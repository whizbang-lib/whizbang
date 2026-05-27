using Microsoft.Extensions.Configuration;

namespace Whizbang.Core.Notifications;

/// <summary>
/// Resolves the connection string the LISTEN/NOTIFY worker should use, given
/// <see cref="WhizbangNotificationOptions"/> and <see cref="IConfiguration"/>.
/// Pure function — no side effects, no DI access. Tested directly.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public static class NotificationConnectionStringResolver {
  /// <summary>
  /// Outcome of a resolution attempt.
  /// </summary>
  /// <param name="ConnectionString">The connection string to use, or <c>null</c> if none could be resolved.</param>
  /// <param name="Source">Where the resolved string came from — for diagnostics + logging.</param>
  public sealed record Resolution(string? ConnectionString, ResolutionSource Source);

  /// <summary>Provenance of a resolved connection string.</summary>
  public enum ResolutionSource {
    /// <summary>Could not resolve any connection string.</summary>
    None,
    /// <summary>Used <see cref="WhizbangNotificationOptions.DirectConnectionString"/> directly.</summary>
    ExplicitOption,
    /// <summary>Used <c>ConnectionStrings:{Key}-direct</c> from <see cref="IConfiguration"/>.</summary>
    DirectKey,
    /// <summary>Used <c>ConnectionStrings:{Key}</c> (pooled) — fallback when no <c>-direct</c> variant exists.</summary>
    PooledKeyFallback,
    /// <summary>
    /// Used <see cref="INotificationConnectionStringFallback.GetConnectionString"/> — typically
    /// the EF Core DbContext's connection string surfaced by <c>.WithDriver.Postgres&lt;TDbContext&gt;()</c>.
    /// </summary>
    DbContextFallback,
  }

  /// <summary>
  /// Resolves the connection string per the documented precedence:
  /// <list type="number">
  ///   <item><description><see cref="WhizbangNotificationOptions.DirectConnectionString"/> if non-empty</description></item>
  ///   <item><description><c>ConnectionStrings:{ConnectionStringKey}-direct</c> from <paramref name="config"/></description></item>
  ///   <item><description><c>ConnectionStrings:{ConnectionStringKey}</c> (pooled) from <paramref name="config"/></description></item>
  ///   <item><description>Otherwise <c>null</c></description></item>
  /// </list>
  /// </summary>
  public static Resolution Resolve(WhizbangNotificationOptions options, IConfiguration config)
    => Resolve(options, config, fallback: null);

  /// <summary>
  /// Same precedence as <see cref="Resolve(WhizbangNotificationOptions, IConfiguration)"/>, but
  /// after all explicit-config sources have come up empty, consults
  /// <paramref name="fallback"/> (if provided) for a last-resort connection string. The fallback
  /// is never consulted when an explicit value resolves — explicit notification config always wins.
  /// </summary>
  public static Resolution Resolve(
      WhizbangNotificationOptions options,
      IConfiguration config,
      INotificationConnectionStringFallback? fallback) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(config);

    if (!string.IsNullOrWhiteSpace(options.DirectConnectionString)) {
      return new Resolution(options.DirectConnectionString, ResolutionSource.ExplicitOption);
    }

    if (!string.IsNullOrWhiteSpace(options.ConnectionStringKey)) {
      var direct = config.GetConnectionString($"{options.ConnectionStringKey}-direct");
      if (!string.IsNullOrWhiteSpace(direct)) {
        return new Resolution(direct, ResolutionSource.DirectKey);
      }

      var pooled = config.GetConnectionString(options.ConnectionStringKey);
      if (!string.IsNullOrWhiteSpace(pooled)) {
        return new Resolution(pooled, ResolutionSource.PooledKeyFallback);
      }
    }

    var fallbackValue = fallback?.GetConnectionString();
    if (!string.IsNullOrWhiteSpace(fallbackValue)) {
      return new Resolution(fallbackValue, ResolutionSource.DbContextFallback);
    }

    return new Resolution(null, ResolutionSource.None);
  }
}
