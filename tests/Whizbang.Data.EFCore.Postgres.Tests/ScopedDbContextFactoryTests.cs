using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The factory that hands every parallel lens resolver its own DbContext.
/// </summary>
/// <remarks>
/// <para>It exists because AddPooledDbContextFactory registers scoped option configurations
/// internally and blows up under scope validation. What replaced it has to provide the same
/// guarantee the pool did: a resolver running in parallel with another must not share a context
/// with it, because DbContext is not thread-safe and the failure is data-dependent — interleaved
/// change tracking, not an exception you can catch in a test.</para>
///
/// <para>The subtler half is scope lifetime. The scope is created inside the call and would be
/// eligible for disposal the moment the call returns; it is kept alive by a ConditionalWeakTable
/// keyed on the context. If that tracking were dropped, every scoped dependency the context was
/// built from would be disposed underneath it, and the context would fail on first real use
/// rather than at construction — far from the code that broke it.</para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/ScopedDbContextFactory.cs</code-under-test>
[Category("Core")]
[Category("Shard1")]
public class ScopedDbContextFactoryTests {

  private static ServiceProvider _provider() {
    var services = new ServiceCollection();
    services.AddScoped<_scopeProbe>();
    services.AddDbContext<_probeContext>(
      options => options.UseNpgsql("Host=127.0.0.1;Database=never_connected"),
      ServiceLifetime.Scoped);
    return services.BuildServiceProvider();
  }

  private static ScopedDbContextFactory<_probeContext> _factory(ServiceProvider provider) =>
    new(provider.GetRequiredService<IServiceScopeFactory>());

  [Test]
  public async Task EachCall_GetsItsOwnContextAndItsOwnScopeAsync() {
    // Two resolvers running in parallel must not end up on one context: DbContext is not
    // thread-safe, and sharing one corrupts change tracking rather than throwing.
    await using var provider = _provider();
    var factory = _factory(provider);

    var first = factory.CreateDbContext();
    var second = factory.CreateDbContext();

    await Assert.That(ReferenceEquals(first, second)).IsFalse()
      .Because("a shared context between parallel resolvers interleaves their change tracking, "
             + "and that failure shows up as wrong data rather than as an exception");
    await Assert.That(ReferenceEquals(first.Probe, second.Probe)).IsFalse()
      .Because("distinct contexts drawn from one scope would still share every scoped dependency "
             + "they were built from, which is the same collision one level down");
  }

  [Test]
  public async Task TheScopeOutlivesTheCallThatCreatedItAsync() {
    // The scope is created inside CreateDbContext and would be collectable the moment it returns.
    // If it were disposed there, the context would be holding scoped services that are already
    // gone and would fail on first real use — nowhere near the factory that handed it over.
    await using var provider = _provider();
    var factory = _factory(provider);

    var context = factory.CreateDbContext();

    await Assert.That(context.Probe.Disposed).IsFalse()
      .Because("the returned context is still expected to work, and it cannot if the scope its "
             + "dependencies came from was disposed as the factory returned");
  }

  [Test]
  public async Task WithoutAScopeFactory_ItRefusesAtConstructionAsync() {
    // Nothing this class does is possible without a scope factory, and every failure downstream
    // of accepting null would surface on a resolver thread instead of at wiring time.
    await Assert.That(() => new ScopedDbContextFactory<_probeContext>(null!))
      .Throws<ArgumentNullException>()
      .Because("a factory that cannot create scopes is a wiring mistake, and wiring mistakes "
             + "belong at startup rather than on the first parallel query");
  }

  /// <summary>A scoped dependency that reports whether its scope has been disposed.</summary>
  private sealed class _scopeProbe : IDisposable {
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
  }

  private sealed class _probeContext(DbContextOptions<_probeContext> options, _scopeProbe probe)
      : DbContext(options) {
    public _scopeProbe Probe { get; } = probe;
  }
}
