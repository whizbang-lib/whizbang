using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>
/// DI extension that swaps the default <see cref="NoOpPinnedConnectionPool"/>
/// for the PostgreSQL-backed <see cref="PinnedConnectionPool"/>. Should be
/// called AFTER <c>AddWhizbangPinnedWorkerPool</c> — typically right next
/// to the database-context registration so the operator wires the options
/// and the impl together.
/// </summary>
/// <docs>fundamentals/workers/pinned-connection-pool</docs>
public static class PostgresPinnedPoolServiceCollectionExtensions {

  /// <summary>
  /// Replaces the registered <see cref="IPinnedConnectionPool"/> with the
  /// PostgreSQL implementation when the resolved options have
  /// <see cref="WhizbangPinnedPoolOptions.Enabled"/> = <c>true</c> AND
  /// <see cref="WhizbangPinnedPoolOptions.ConnectionString"/> is non-empty.
  /// Otherwise leaves the no-op registration in place — making the
  /// service safe to call unconditionally at startup.
  /// </summary>
  /// <param name="services">DI container.</param>
  /// <returns><paramref name="services"/>, for chaining.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
  public static IServiceCollection AddWhizbangPostgresPinnedPool(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);

    // Remove the prior IPinnedConnectionPool registration (if any) so the factory below
    // wins. AddSingleton + TryAdd would NOT replace, hence Replace.
    services.Replace(ServiceDescriptor.Singleton<IPinnedConnectionPool>(sp => {
      var opts = sp.GetRequiredService<IOptions<WhizbangPinnedPoolOptions>>().Value;
      if (!opts.Enabled || string.IsNullOrWhiteSpace(opts.ConnectionString)) {
        return NoOpPinnedConnectionPool.Instance;
      }
      var registry = sp.GetRequiredService<PinnedWorkerRegistry>();
      var logger = sp.GetService<ILogger<PinnedConnectionPool>>();
      var metrics = sp.GetService<Whizbang.Core.Observability.PinnedPoolMetrics>();
      return new PinnedConnectionPool(opts, registry, logger, metrics);
    }));

    return services;
  }
}
