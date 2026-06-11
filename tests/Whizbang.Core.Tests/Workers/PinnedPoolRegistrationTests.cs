#pragma warning disable CA1707

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// DI registration matrix for the pinned worker pool. Verifies that the
/// IPinnedConnectionPool resolved by the container matches what the options
/// say should happen — NoOp when the feature is off or unconfigured, real
/// PostgreSQL-backed pool when the Postgres helper is wired AND options say
/// to enable.
/// </summary>
/// <docs>fundamentals/workers/pinned-connection-pool</docs>
public class PinnedPoolRegistrationTests {

  [Test]
  public async Task Register_CoreOnly_ResolvesNoOpAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangPinnedWorkerPool(opts => {
      opts.Enabled = true;
      opts.ConnectionString = "Host=test;Database=test;Username=x;Password=x";
    });

    var sp = services.BuildServiceProvider();
    var pool = sp.GetRequiredService<IPinnedConnectionPool>();

    await Assert.That(pool).IsTypeOf<NoOpPinnedConnectionPool>()
      .Because("Without AddWhizbangPostgresPinnedPool, the Core extension defaults to NoOp — preserves AOT cleanliness and lets Postgres-bound assemblies opt-in explicitly.");
  }

  [Test]
  public async Task Register_PostgresButDisabled_ResolvesNoOpAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangPinnedWorkerPool(opts => {
      opts.Enabled = false;
      opts.ConnectionString = "Host=test;Database=test;Username=x;Password=x";
    });
    services.AddWhizbangPostgresPinnedPool();

    var sp = services.BuildServiceProvider();
    var pool = sp.GetRequiredService<IPinnedConnectionPool>();

    await Assert.That(pool).IsTypeOf<NoOpPinnedConnectionPool>()
      .Because("Enabled=false MUST result in a NoOp even when the Postgres extension is wired — operators rely on the flag as a runtime kill-switch without redeploying.");
  }

  [Test]
  public async Task Register_PostgresEnabledButNoConnString_ResolvesNoOpAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangPinnedWorkerPool(opts => {
      opts.Enabled = true;
      opts.ConnectionString = null;
    });
    services.AddWhizbangPostgresPinnedPool();

    var sp = services.BuildServiceProvider();
    var pool = sp.GetRequiredService<IPinnedConnectionPool>();

    await Assert.That(pool).IsTypeOf<NoOpPinnedConnectionPool>()
      .Because("Missing ConnectionString means the operator hasn't supplied the direct PG endpoint — silently fall back to NoOp rather than crash at construction.");
  }

  [Test]
  public async Task Register_PostgresEnabledWithConnString_ResolvesRealPoolAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangPinnedWorkerPool(opts => {
      opts.Enabled = true;
      opts.ConnectionString = "Host=test;Database=test;Username=x;Password=x";
      opts.Size = 2;
    });
    services.AddWhizbangPostgresPinnedPool();

    var sp = services.BuildServiceProvider();
    var pool = sp.GetRequiredService<IPinnedConnectionPool>();

    await Assert.That(pool).IsTypeOf<PinnedConnectionPool>()
      .Because("With Enabled=true AND a non-empty ConnectionString AND the Postgres helper called, the real pool MUST resolve — that's the slot-3 DISCARD-ALL improvement path.");
  }

  [Test]
  public async Task Register_PinnedWorkerRegistry_IsSingletonAcrossResolutionsAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangPinnedWorkerPool(opts => { });

    var sp = services.BuildServiceProvider();
    var a = sp.GetRequiredService<PinnedWorkerRegistry>();
    var b = sp.GetRequiredService<PinnedWorkerRegistry>();

    await Assert.That(a).IsSameReferenceAs(b)
      .Because("The registry holds the consumer opt-ins via AddPinnedWorker<T>(); registering twice would lose them silently.");
  }

  [Test]
  public async Task Register_CalledTwice_DoesNotDoubleRegisterRegistryAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangPinnedWorkerPool(opts => { });
    services.AddWhizbangPinnedWorkerPool(opts => { });

    var sp = services.BuildServiceProvider();
    var a = sp.GetRequiredService<PinnedWorkerRegistry>();
    var b = sp.GetRequiredService<PinnedWorkerRegistry>();

    await Assert.That(a).IsSameReferenceAs(b)
      .Because("TryAddSingleton on the second call MUST be idempotent — duplicate registration would split opt-in state into two registries.");
  }

  [Test]
  public async Task AddPinnedWorker_OptsInCustomWorkerAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangPinnedWorkerPool(opts => {
      opts.Enabled = true;
      opts.ConnectionString = "Host=test;Database=test;Username=x;Password=x";
    });
    services.AddPinnedWorker<_customWorker>();
    services.AddWhizbangPostgresPinnedPool();

    var sp = services.BuildServiceProvider();
    var registry = sp.GetRequiredService<PinnedWorkerRegistry>();
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WhizbangPinnedPoolOptions>>().Value;

    await Assert.That(registry.IsEligible(typeof(_customWorker), opts)).IsTrue()
      .Because("Consumer opt-in MUST surface as eligibility on the registry; otherwise the custom worker silently doesn't pin.");
  }

  private sealed class _customWorker : Microsoft.Extensions.Hosting.IHostedService {
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
  }
}
