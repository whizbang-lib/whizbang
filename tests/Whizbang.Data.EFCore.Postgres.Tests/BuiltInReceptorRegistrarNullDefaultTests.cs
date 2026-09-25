using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    // Without this every reflected registrar's ILogger<T> resolves to null, the sweep below skips
    // it, and the sweep passes having exercised nothing. That is how the first version of this test
    // went green against the very defect it was written for.
    services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
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

  [Test]
  public async Task IntegrityCheckpoint_WhenTheRegistryIsTheNullDefault_StartsWithoutThrowingAsync() {
    await using var sp = _hostWithoutReceptors();
    var registrar = new IntegrityCheckpointReceptorRegistrar(
      sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityCheckpointReceptor>.Instance);

    await registrar.StartAsync(CancellationToken.None);
  }

  [Test]
  public async Task ScheduledIntegritySweep_WhenTheRegistryIsTheNullDefault_StartsWithoutThrowingAsync() {
    await using var sp = _hostWithoutReceptors();
    var registrar = new ScheduledIntegritySweepReceptorRegistrar(
      sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScheduledIntegritySweepReceptor>.Instance);

    await registrar.StartAsync(CancellationToken.None);
  }

  /// <summary>
  /// Every registrar of this shape, found rather than listed, so adding a seventh cannot reopen the
  /// hole that adding a fifth and sixth already did.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The first fix for this defect enumerated the registrars by hand and got four of six. The two it
  /// missed were not exotic: they sit in the same folder, take the same constructor, and fail the
  /// same way. A test naming each one by hand would have shipped with the same two gaps, because it
  /// would have been written from the same list.
  /// </para>
  /// <para>
  /// So the population is derived from the assembly: a hosted service whose first constructor
  /// parameter is the provider itself is the registrar shape, and every one of them has to survive a
  /// host that declares nothing of its own. A new registrar is covered the moment it is written.
  /// </para>
  /// </remarks>
  [Test]
  public async Task EveryStartupRegistrar_SurvivesAHostThatDeclaresNoReceptorsAsync() {
    await using var sp = _hostWithoutReceptors();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var registrars = typeof(IntegrityCheckpointReceptorRegistrar).Assembly
      .GetTypes()
      .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IHostedService).IsAssignableFrom(t))
      .Select(t => (Type: t, Ctor: t.GetConstructors().FirstOrDefault()))
      // The registrar shape, stated precisely: it needs the provider, a scope factory and loggers,
      // and nothing else. A hosted service that wants a database or a clock is doing other work and
      // is not what this sweep is about -- excluding it by SHAPE rather than by skipping it at run
      // time is what keeps the sweep from quietly exercising nothing.
      .Where(x => x.Ctor is not null
               && x.Ctor.GetParameters() is { Length: > 0 } ps
               && ps[0].ParameterType == typeof(IServiceProvider)
               && ps.All(pi => pi.ParameterType == typeof(IServiceProvider)
                            || pi.ParameterType == typeof(IServiceScopeFactory)
                            || (pi.ParameterType.IsGenericType
                                && pi.ParameterType.GetGenericTypeDefinition() == typeof(ILogger<>))))
      .ToList();

    await Assert.That(registrars.Count).IsGreaterThanOrEqualTo(6)
      .Because("six registrars of this shape exist today; finding fewer means the query stopped "
             + "matching and the sweep is vacuous rather than passing");

    var failures = new List<string>();
    var started = 0;
    foreach (var (type, ctor) in registrars) {
      object instance;
      try {
        // The provider supplies itself, the scope factory and any ILogger<T>; anything else is not
        // this shape and is skipped rather than guessed at.
        var args = ctor!.GetParameters().Select(pi => _resolve(pi.ParameterType, sp, scopeFactory)).ToArray();
        instance = ctor.Invoke(args);
      } catch (Exception ex) {
        failures.Add($"{type.Name} could not be constructed: {ex.GetBaseException().Message}");
        continue;
      }

      try {
        await ((IHostedService)instance).StartAsync(CancellationToken.None);
        started++;
      } catch (InvalidOperationException ex) {
        failures.Add($"{type.Name} threw on start: {ex.Message}");
      }
    }

    await Assert.That(failures).IsEmpty()
      .Because("a host that declares no receptors of its own must still boot; each of these refused "
             + "to, which is a crash before the port is bound rather than a degraded feature");
    await Assert.That(started).IsEqualTo(registrars.Count)
      .Because("every registrar found has to be started; one merely skipped is an untested one");
  }

  /// <summary>One constructor argument: the provider itself, its scope factory, or a resolved service.</summary>
  /// <param name="parameterType">The parameter being supplied.</param>
  /// <param name="services">The provider standing in for a host with no receptors.</param>
  /// <param name="scopeFactory">That provider's scope factory.</param>
  /// <returns>The argument, or <see langword="null"/> when this is not the registrar shape.</returns>
  private static object? _resolve(
      Type parameterType, IServiceProvider services, IServiceScopeFactory scopeFactory) {
    if (parameterType == typeof(IServiceProvider)) {
      return services;
    }

    if (parameterType == typeof(IServiceScopeFactory)) {
      return scopeFactory;
    }

    return services.GetService(parameterType);
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
