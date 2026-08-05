using System;
using Microsoft.Extensions.Configuration;
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
/// <tests>tests/Whizbang.Core.Tests/Workers/PinnedPoolRegistrationTests.cs:Register_PostgresEnabledWithConnString_ResolvesRealPoolAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/PinnedPoolRegistrationTests.cs:Register_PostgresButDisabled_ResolvesNoOpAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/PinnedPoolRegistrationTests.cs:Register_ConnectionStringName_TakesPrecedenceOverInlineAsync</tests>
public static class PostgresPinnedPoolServiceCollectionExtensions {

  /// <summary>
  /// Replaces the registered <see cref="IPinnedConnectionPool"/> with the
  /// PostgreSQL implementation when the resolved options have
  /// <see cref="WhizbangPinnedPoolOptions.Enabled"/> = <c>true</c> AND a
  /// connection string is resolvable via either
  /// <see cref="WhizbangPinnedPoolOptions.ConnectionStringName"/> (preferred —
  /// looks up <c>ConnectionStrings:{Name}</c>) or
  /// <see cref="WhizbangPinnedPoolOptions.ConnectionString"/> (inline fallback).
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
      if (!opts.Enabled) {
        return NoOpPinnedConnectionPool.Instance;
      }

      var connectionString = _resolveConnectionString(sp, opts);
      if (string.IsNullOrWhiteSpace(connectionString)) {
        return NoOpPinnedConnectionPool.Instance;
      }

      var registry = sp.GetRequiredService<PinnedWorkerRegistry>();
      var logger = sp.GetService<ILogger<PinnedConnectionPool>>();
      var metrics = sp.GetService<Whizbang.Core.Observability.PinnedPoolMetrics>();
      // Wrap so the pool sees the resolved string regardless of source.
      var effectiveOpts = new WhizbangPinnedPoolOptions {
        ConnectionStringName = opts.ConnectionStringName,
        ConnectionString = connectionString,
        Enabled = opts.Enabled,
        Size = opts.Size,
        IncludeFlushWorkers = opts.IncludeFlushWorkers,
        ExcludeWorkers = opts.ExcludeWorkers,
        ConnectionLifetimeSeconds = opts.ConnectionLifetimeSeconds,
        BorrowTimeoutMilliseconds = opts.BorrowTimeoutMilliseconds,
      };
      return new PinnedConnectionPool(effectiveOpts, registry, logger, metrics);
    }));

    return services;
  }

  // ConnectionStringName wins over inline ConnectionString — keeps secret-bearing
  // strings in standard ConnectionStrings:* config (key-vault, env-var, log-redaction
  // conventions all apply uniformly). Falls back to inline only when no name is set.
  private static string? _resolveConnectionString(IServiceProvider sp, WhizbangPinnedPoolOptions opts) {
    if (!string.IsNullOrWhiteSpace(opts.ConnectionStringName)) {
      var configuration = sp.GetService<IConfiguration>();
      var resolved = configuration?.GetConnectionString(opts.ConnectionStringName);
      if (!string.IsNullOrWhiteSpace(resolved)) {
        return resolved;
      }
    }
    return opts.ConnectionString;
  }
}
