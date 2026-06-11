using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Whizbang.Core.Workers;

/// <summary>
/// DI extensions for configuring the Whizbang pinned-connection worker pool.
/// </summary>
/// <docs>fundamentals/workers/pinned-connection-pool</docs>
public static class PinnedPoolServiceCollectionExtensions {

  /// <summary>
  /// Registers the pinned worker connection pool and applies the supplied
  /// configuration. Workers in the tier-1+2 set (see
  /// <see cref="WhizbangPinnedPoolOptions"/> remarks) automatically borrow
  /// from the pool when <see cref="WhizbangPinnedPoolOptions.Enabled"/> is
  /// <c>true</c> AND <see cref="WhizbangPinnedPoolOptions.ConnectionString"/>
  /// is non-empty.
  /// </summary>
  /// <param name="services">DI container.</param>
  /// <param name="configure">Configuration callback that populates
  ///   <see cref="WhizbangPinnedPoolOptions"/> — typically wraps
  ///   <c>configuration.GetSection("Whizbang:Workers:PinnedPool").Bind(opts)</c>
  ///   plus any host-specific overrides.</param>
  /// <returns><paramref name="services"/>, for chaining.</returns>
  /// <remarks>
  /// AOT-clean — does not call <c>BindConfiguration</c> internally so the
  /// extension can live in <c>Whizbang.Core</c> (zero-reflection rule).
  /// Consumer code does the configuration binding, matching the established
  /// pattern in <c>AddWhizbangAzureBlobOffload</c> and friends.
  /// </remarks>
  /// <example>
  /// <code>
  /// services.AddWhizbangPinnedWorkerPool(opts => {
  ///   builder.Configuration.GetSection("Whizbang:Workers:PinnedPool").Bind(opts);
  ///   // or directly:
  ///   opts.ConnectionString = builder.Configuration.GetConnectionString("WorkerDirect");
  ///   opts.Enabled = true;
  ///   opts.Size = 2;
  /// });
  /// </code>
  /// </example>
  public static IServiceCollection AddWhizbangPinnedWorkerPool(
      this IServiceCollection services,
      Action<WhizbangPinnedPoolOptions> configure) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configure);

    services.AddOptions<WhizbangPinnedPoolOptions>()
      .Configure(configure)
      // Catches the #1 misconfiguration (Size left at 0 while Enabled=true) at
      // startup instead of surfacing as silent DISCARD-ALL overhead the
      // operator thought they had eliminated.
      .Validate(
        static opts =>
          !opts.Enabled
          || string.IsNullOrWhiteSpace(opts.ConnectionString)
          || opts.Size > 0,
        "WhizbangPinnedPoolOptions.Size must be > 0 when Enabled is true with a ConnectionString set.");

    services.TryAddSingleton<PinnedWorkerRegistry>();
    services.TryAddSingleton<Whizbang.Core.Observability.PinnedPoolMetrics>();

    // Default registration: NoOp. Whizbang.Data.Postgres replaces this with the real
    // PinnedConnectionPool when the consumer also calls AddWhizbangPostgresPinnedPool.
    // Workers default to NoOp regardless when no IPinnedConnectionPool is registered.
    services.TryAddSingleton<IPinnedConnectionPool>(_ => NoOpPinnedConnectionPool.Instance);

    return services;
  }

  /// <summary>
  /// Opts a non-Whizbang-shipped worker type into the pinned pool.
  /// Whizbang-built-in workers (tier 1+2) are already registered automatically
  /// based on <see cref="WhizbangPinnedPoolOptions.IncludeFlushWorkers"/>; this
  /// extension exists for consumer code (e.g. JDX-side custom workers) that
  /// wants to participate in the same pinned-pool plumbing.
  /// </summary>
  /// <typeparam name="TWorker">The worker class to opt in.</typeparam>
  /// <param name="services">DI container.</param>
  /// <returns><paramref name="services"/>, for chaining.</returns>
  /// <remarks>
  /// The worker must be registered separately as an <see cref="IHostedService"/>;
  /// this call only adds it to the pinned-pool eligibility set. Calls are
  /// additive — the set is preserved across multiple invocations.
  /// </remarks>
  /// <example>
  /// <code>
  /// services.AddHostedService&lt;MyBulkImportPollingWorker&gt;();
  /// services.AddPinnedWorker&lt;MyBulkImportPollingWorker&gt;();
  /// </code>
  /// </example>
  public static IServiceCollection AddPinnedWorker<TWorker>(this IServiceCollection services)
      where TWorker : class, IHostedService {
    ArgumentNullException.ThrowIfNull(services);

    services.TryAddSingleton<PinnedWorkerRegistry>();
    // Collected by PinnedWorkerRegistry's ctor via DI enumeration.
    services.AddSingleton(new PinnedWorkerOptIn(typeof(TWorker)));

    return services;
  }
}

/// <summary>
/// Marker carried in DI to opt a custom worker type into the pinned pool.
/// Collected by <see cref="PinnedWorkerRegistry"/> at construction.
/// </summary>
public sealed class PinnedWorkerOptIn {
  /// <summary>The worker type being opted in.</summary>
  public Type WorkerType { get; }

  /// <summary>Constructs the marker for the supplied worker type.</summary>
  public PinnedWorkerOptIn(Type workerType) {
    ArgumentNullException.ThrowIfNull(workerType);
    WorkerType = workerType;
  }
}

/// <summary>
/// Tracks which worker types are eligible to borrow from the pinned
/// connection pool. Combines Whizbang's tier-based default set with any
/// consumer opt-ins added via <see cref="PinnedPoolServiceCollectionExtensions.AddPinnedWorker{TWorker}"/>.
/// </summary>
/// <remarks>
/// Singleton-scoped; the registry is read by each worker's connection-borrow
/// helper to decide whether to use the pinned pool vs. the standard pgbouncer
/// route. Eligibility is resolved once at startup (when DI builds), not per
/// borrow, so runtime cost is zero after warmup.
/// </remarks>
public sealed class PinnedWorkerRegistry {
  // Tier 1 — always pinned when the feature is enabled. Hot polling loops
  // with measurable DB cost dominated by short-txn DISCARD ALL overhead.
  private static readonly HashSet<string> _tier1Workers = new(StringComparer.Ordinal) {
    "ClaimWorker",
    "OutboxPublishWorker",
    "HeartbeatWorker",
    "LeaseRenewalWorker",
  };

  // Tier 2 — flush-channel workers that ship small frequent transactions.
  // Opted in by IncludeFlushWorkers (default true).
  private static readonly HashSet<string> _tier2FlushWorkers = new(StringComparer.Ordinal) {
    "InboxHandlerWorker",
    "OutboxCompletionFlushWorker",
    "PerspectiveCompletionFlushWorker",
    "FailureFlushWorker",
  };

  private readonly HashSet<Type> _optIns = [];

  /// <summary>Default constructor — no consumer opt-ins (used by tests and zero-config setups).</summary>
  public PinnedWorkerRegistry() { }

  /// <summary>DI constructor — consumes any <see cref="PinnedWorkerOptIn"/> markers registered via <see cref="PinnedPoolServiceCollectionExtensions.AddPinnedWorker{TWorker}"/>.</summary>
  public PinnedWorkerRegistry(IEnumerable<PinnedWorkerOptIn> optIns) {
    ArgumentNullException.ThrowIfNull(optIns);
    foreach (var optIn in optIns) {
      _optIns.Add(optIn.WorkerType);
    }
  }

  /// <summary>
  /// Adds a consumer worker type to the eligibility set. Called via the
  /// <see cref="PinnedPoolServiceCollectionExtensions.AddPinnedWorker{TWorker}"/> extension.
  /// </summary>
  public void AddOptIn(Type workerType) {
    ArgumentNullException.ThrowIfNull(workerType);
    _optIns.Add(workerType);
  }

  /// <summary>
  /// Resolves whether the given worker type is eligible for the pinned pool,
  /// given the supplied options. Combines tier-1 default + tier-2 (if
  /// included) + opt-ins, minus explicit excludes.
  /// </summary>
  public bool IsEligible(Type workerType, WhizbangPinnedPoolOptions options) {
    ArgumentNullException.ThrowIfNull(workerType);
    ArgumentNullException.ThrowIfNull(options);

    var simpleName = workerType.Name;
    if (options.ExcludeWorkers.Contains(simpleName, StringComparer.Ordinal)) {
      return false;
    }

    if (_tier1Workers.Contains(simpleName)) {
      return true;
    }

    if (options.IncludeFlushWorkers && _tier2FlushWorkers.Contains(simpleName)) {
      return true;
    }

    return _optIns.Contains(workerType);
  }
}
