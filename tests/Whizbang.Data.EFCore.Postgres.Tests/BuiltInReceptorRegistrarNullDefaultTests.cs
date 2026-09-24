using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The four registrars that add a framework built-in receptor at startup, driven the way a host
/// with no receptors of its own resolves them.
/// </summary>
/// <remarks>
/// <para>
/// Each registrar documents the same promise: it no-ops when there is no registry, so a schema-only
/// or diagnostic host still boots. Each checked for that by testing whether
/// <c>GetService&lt;IReceptorRegistry&gt;()</c> returned null — which stopped being possible once
/// every framework dependency gained a turnkey default. The service now always resolves, to
/// <see cref="NullReceptorRegistry"/>, whose <c>Register</c> throws by design; the guard became
/// unreachable and the promised no-op became a startup crash.
/// </para>
/// <para>
/// It only bites a host that declares no receptors, because any assembly that declares one gets a
/// generated registry that replaces the default — which is why it reached a deployed service
/// (JDX's system-management service, a pure API surface) rather than a test. The host died before
/// binding its port, so the platform saw only a refused connection on the startup probe.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/ScheduledStreamCloseReceptorRegistrar.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityManifestReceptors.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/RedeliveryRequestReceptorRegistrar.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/RebuildCommandReceptorRegistrar.cs</code-under-test>
[Category("Shard1")]
public class BuiltInReceptorRegistrarNullDefaultTests {

  /// <summary>
  /// A provider wired the way the framework's own defaults wire it: the registry resolves, and what
  /// it resolves to is the null default.
  /// </summary>
  private static ServiceProvider _hostWithoutReceptors() {
    var services = new ServiceCollection();
    services.AddSingleton<IReceptorRegistry>(NullReceptorRegistry.Instance);
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task ScheduledStreamClose_WhenTheRegistryIsTheNullDefault_StartsWithoutThrowingAsync() {
    await using var sp = _hostWithoutReceptors();
    var registrar = new ScheduledStreamCloseReceptorRegistrar(
      sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScheduledStreamCloseReceptor>.Instance);

    await registrar.StartAsync(CancellationToken.None);
  }

  [Test]
  public async Task IntegrityManifest_WhenTheRegistryIsTheNullDefault_StartsWithoutThrowingAsync() {
    await using var sp = _hostWithoutReceptors();
    var registrar = new IntegrityManifestReceptorRegistrar(
      sp, sp.GetRequiredService<IServiceScopeFactory>(),
      NullLogger<IntegrityManifestRequestReceptor>.Instance,
      NullLogger<IntegrityManifestReceptor>.Instance);

    await registrar.StartAsync(CancellationToken.None);
  }

  [Test]
  public async Task RedeliveryRequest_WhenTheRegistryIsTheNullDefault_StartsWithoutThrowingAsync() {
    await using var sp = _hostWithoutReceptors();
    var registrar = new RedeliveryRequestReceptorRegistrar(
      sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RedeliveryRequestReceptor>.Instance);

    await registrar.StartAsync(CancellationToken.None);
  }

  [Test]
  public async Task RebuildCommand_WhenTheRegistryIsTheNullDefault_StartsWithoutThrowingAsync() {
    await using var sp = _hostWithoutReceptors();
    var registrar = new RebuildCommandReceptorRegistrar(
      sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RebuildPerspectiveCommandReceptor>.Instance);

    await registrar.StartAsync(CancellationToken.None);
  }

  /// <summary>
  /// The refusal itself stays: registering into the null default is still a loud failure, because a
  /// receptor accepted by a registry nothing dispatches from would be dropped in silence. The
  /// registrars must recognize the default, not the registry must stop refusing.
  /// </summary>
  [Test]
  public async Task TheNullDefault_StillRefusesARuntimeRegistration() {
    await using var sp = _hostWithoutReceptors();
    // A real built-in receptor rather than a local fake: the receptor-discovery generator registers
    // every IReceptor<> it finds in an assembly, so declaring one here would change this very test
    // project from "declares no receptors" into one that does.
    var receptor = new ScheduledStreamCloseReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScheduledStreamCloseReceptor>.Instance);

    await Assert.That(() => NullReceptorRegistry.Instance.Register(receptor, LifecycleStage.LocalImmediateInline))
      .Throws<InvalidOperationException>()
      .Because("a receptor added to a registry nothing dispatches from must fail loudly, not silently");
  }
}
