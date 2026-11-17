// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Every assembly's lifecycle receptors are found through the one <see cref="IReceptorRegistry"/> the container
/// serves (#2197).
/// </summary>
/// <remarks>
/// Each assembly that declares receptors generates a registry and registers it through
/// <see cref="ReceptorRegistryServiceCollectionExtensions.AddReceptorRegistryContribution{TRegistry}"/>. Registered as a
/// plain singleton, the last assembly's registry displaced the others, so no lifecycle receptor (<c>[FireAt]</c>)
/// declared in any other assembly fired at a worker stage. These pin the union, which contribution answers for a
/// receptor two assemblies both declare (the latest, #2201), where runtime registrations go (the latest contribution),
/// and that the null default and a host's own registry keep their meaning.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorRegistryServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/CompositeReceptorRegistry.cs</code-under-test>
public class ReceptorRegistryContributionTests {
  // Public: the receptor generator discovers every receptor class in this assembly and routes it.
  public sealed record OrderPlaced : IMessage;

  public sealed class OnOrderPlaced : IReceptor<OrderPlaced>, IReceptor<OrderPlaced, int> {
    ValueTask IReceptor<OrderPlaced>.HandleAsync(OrderPlaced message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask<int> IReceptor<OrderPlaced, int>.HandleAsync(OrderPlaced message, CancellationToken cancellationToken) => ValueTask.FromResult(0);
  }

  private static ReceptorInfo _receptor(string id) =>
    new(typeof(OrderPlaced), id, static (_, _, _, _, _) => ValueTask.FromResult<object?>(null));

  /// <summary>Stands in for one assembly's generated registry, recording the runtime calls it receives.</summary>
  private abstract class AssemblyRegistry(params string[] receptorIds) : IReceptorRegistry {
    public List<string> RuntimeCalls { get; } = [];
    public IReadOnlyList<ReceptorInfo> Answer { get; init; } = [.. receptorIds.Select(_receptor)];

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) =>
      stage == LifecycleStage.PostPerspectiveInline ? Answer : [];
    public IReadOnlyCollection<string> GetCompiledReceptorIds() => receptorIds;
    public IReadOnlyList<ReceptorInfo> GetRuntimeReceptorsForBaseTypesOf(Type messageType, LifecycleStage stage) {
      RuntimeCalls.Add("base-types");
      return [];
    }
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage =>
      RuntimeCalls.Add("register");
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage =>
      RuntimeCalls.Add("register-response");
    public bool HasRuntimeConsumerFor(string clrTypeName) {
      RuntimeCalls.Add("has-consumer");
      return true;
    }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage {
      RuntimeCalls.Add("unregister");
      return true;
    }
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage {
      RuntimeCalls.Add("unregister-response");
      return true;
    }
    public bool HasAnyRuntimeReceptors(string messageType) {
      RuntimeCalls.Add("has-any");
      return true;
    }
  }

  private sealed class OrdersRegistry() : AssemblyRegistry("Orders.OnOrderPlaced", "Shared.Audit");
  private sealed class BillingRegistry() : AssemblyRegistry("Billing.OnOrderPlaced", "Shared.Audit");
  private sealed class HostsOwnRegistry() : AssemblyRegistry("Host.OnOrderPlaced");

  /// <summary>Implements only what an implementation must, so the interface's defaults answer the rest.</summary>
  private sealed class MinimalRegistry : IReceptorRegistry {
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  private static void _add<TRegistry>(IServiceCollection services) where TRegistry : class, IReceptorRegistry {
    services.AddSingleton<TRegistry>();
    services.AddReceptorRegistryContribution<TRegistry>();
  }

  private static ServiceProvider _twoAssemblies(bool ordersFirst = true, IServiceCollection? services = null) {
    services ??= new ServiceCollection();
    if (ordersFirst) {
      _add<OrdersRegistry>(services);
      _add<BillingRegistry>(services);
    } else {
      _add<BillingRegistry>(services);
      _add<OrdersRegistry>(services);
    }
    return services.BuildServiceProvider();
  }

  private static string[] _ids(IReadOnlyList<ReceptorInfo> receptors) => [.. receptors.Select(r => r.ReceptorId)];

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task TwoAssemblies_EachAssemblysLifecycleReceptorFires_WhicheverRegisteredLastAsync(bool ordersFirst) {
    await using var provider = _twoAssemblies(ordersFirst);

    var ids = _ids(provider.GetRequiredService<IReceptorRegistry>()
      .GetReceptorsFor(typeof(OrderPlaced), LifecycleStage.PostPerspectiveInline));

    await Assert.That(ids).Contains("Orders.OnOrderPlaced")
      .Because("a lifecycle receptor fires whichever assembly registered last");
    await Assert.That(ids).Contains("Billing.OnOrderPlaced");
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task AReceptorTwoAssembliesDeclare_AnswersOnce_FromTheLatestAsync(bool ordersFirst) {
    await using var provider = _twoAssemblies(ordersFirst);
    var latest = ordersFirst ? "Billing.OnOrderPlaced" : "Orders.OnOrderPlaced";

    var ids = _ids(provider.GetRequiredService<IReceptorRegistry>()
      .GetReceptorsFor(typeof(OrderPlaced), LifecycleStage.PostPerspectiveInline));

    await Assert.That(ids.Count(id => id == "Shared.Audit")).IsEqualTo(1)
      .Because("a receptor two assemblies declare under one name runs once, not once per assembly");
    await Assert.That(ids[0]).IsEqualTo(latest)
      .Because("the latest contribution answers first, so its declaration of the shared receptor is the one used");
  }

  [Test]
  public async Task OneRegistryAnsweringTwiceWithOneId_KeepsBothAsync() {
    var twice = new HostsOwnRegistry { Answer = [_receptor("runtime_X"), _receptor("runtime_X")] };
    var registry = new CompositeReceptorRegistry([new OrdersRegistry(), twice]);

    var ids = _ids(registry.GetReceptorsFor(typeof(OrderPlaced), LifecycleStage.PostPerspectiveInline));

    await Assert.That(ids.Count(id => id == "runtime_X")).IsEqualTo(2)
      .Because("only another assembly's receptor of the same id is left out; one registry's own answer stands");
  }

  [Test]
  public async Task OneContribution_IsAskedDirectlyAsync() {
    var only = new OrdersRegistry();
    var registry = new CompositeReceptorRegistry([only]);

    await Assert.That(registry.GetReceptorsFor(typeof(OrderPlaced), LifecycleStage.PostPerspectiveInline))
      .IsSameReferenceAs(only.Answer);
  }

  [Test]
  public async Task RuntimeRegistrations_GoToTheLatestContribution_AndAreAskedThereAsync() {
    var earlier = new OrdersRegistry();
    var latest = new BillingRegistry();
    var registry = new CompositeReceptorRegistry([latest, earlier]);
    var receptor = new OnOrderPlaced();

    registry.Register<OrderPlaced>(receptor, LifecycleStage.PostPerspectiveInline);
    registry.Register<OrderPlaced, int>(receptor, LifecycleStage.PostPerspectiveInline);
    var unregistered = registry.Unregister<OrderPlaced>(receptor, LifecycleStage.PostPerspectiveInline);
    var unregisteredResponse = registry.Unregister<OrderPlaced, int>(receptor, LifecycleStage.PostPerspectiveInline);
    var hasConsumer = registry.HasRuntimeConsumerFor("OrderPlaced");
    var hasAny = registry.HasAnyRuntimeReceptors("OrderPlaced");
    _ = registry.GetRuntimeReceptorsForBaseTypesOf(typeof(OrderPlaced), LifecycleStage.PostPerspectiveInline);

    await Assert.That(latest.RuntimeCalls.SequenceEqual(
      ["register", "register-response", "unregister", "unregister-response", "has-consumer", "has-any", "base-types"])).IsTrue()
      .Because($"one registry holds every runtime receptor: the latest contribution; it saw: {string.Join(", ", latest.RuntimeCalls)}");
    await Assert.That(earlier.RuntimeCalls).IsEmpty();
    await Assert.That(unregistered && unregisteredResponse && hasConsumer && hasAny).IsTrue()
      .Because("the latest contribution's answers are the composite's");
  }

  [Test]
  public async Task CompiledReceptorIds_AreEveryContributionsOnceEachAsync() {
    var registry = new CompositeReceptorRegistry([new OrdersRegistry(), new BillingRegistry()]);

    await Assert.That(registry.GetCompiledReceptorIds().Order(StringComparer.Ordinal).SequenceEqual(
      ["Billing.OnOrderPlaced", "Orders.OnOrderPlaced", "Shared.Audit"])).IsTrue();
  }

  [Test]
  public async Task TheReceptorRegistry_ReportsReceptorsTwoAssembliesShareAsync() {
    await using var provider = _twoAssemblies();

    var sources = provider.GetServices<IRegistrationCollisionSource>().ToArray();

    await Assert.That(sources.Length).IsEqualTo(1)
      .Because("contributing a receptor registry reports the receptors two assemblies both declare at startup");
    await Assert.That(sources[0].GetCollisions()).IsEmpty()
      .Because("both stand-in registries are declared in this one test assembly, which cannot hide its own receptor");
  }

  [Test]
  public async Task TheContributions_ReplaceTheNullDefault_WhicheverComesFirstAsync() {
    IServiceCollection before = new ServiceCollection();
    before.TryAddWhizbangDefaults();
    await using var defaultsFirst = _twoAssemblies(services: before);
    var after = new ServiceCollection();
    _add<OrdersRegistry>(after);
    after.TryAddWhizbangDefaults();
    await using var defaultsLast = after.BuildServiceProvider();

    await Assert.That(defaultsFirst.GetRequiredService<IReceptorRegistry>()).IsTypeOf<CompositeReceptorRegistry>();
    await Assert.That(defaultsLast.GetRequiredService<IReceptorRegistry>()).IsTypeOf<CompositeReceptorRegistry>();
  }

  [Test]
  public async Task AHostsOwnRegistry_RegisteredFirst_IsTheOneServedAsync() {
    IServiceCollection services = new ServiceCollection();
    services.AddSingleton<IReceptorRegistry>(new HostsOwnRegistry());
    await using var provider = _twoAssemblies(services: services);

    await Assert.That(provider.GetRequiredService<IReceptorRegistry>()).IsTypeOf<HostsOwnRegistry>();
  }

  [Test]
  public async Task TheGeneratedRegistry_ListsTheReceptorsCompiledIntoItAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Tests.Generated.GeneratedReceptorRegistry>();
    services.AddReceptorRegistryContribution<Whizbang.Core.Tests.Generated.GeneratedReceptorRegistry>();
    await using var provider = services.BuildServiceProvider();

    var ids = provider.GetRequiredService<IReceptorRegistry>().GetCompiledReceptorIds();

    await Assert.That(ids.Any(id => id.EndsWith(".GateVoidCommandReceptor", StringComparison.Ordinal))).IsTrue()
      .Because("the generated registry names every receptor compiled into it, so a receptor two assemblies both "
             + "declare can be reported at startup");
    await Assert.That(ids.Count).IsEqualTo(ids.Distinct(StringComparer.Ordinal).Count())
      .Because("a receptor handling several messages or stages is one receptor");
  }

  [Test]
  public async Task AnImplementationWithoutCompiledReceptors_ListsNoneAsync() {
    IReceptorRegistry registry = new MinimalRegistry();

    await Assert.That(registry.GetCompiledReceptorIds()).IsEmpty();
  }

  [Test]
  public async Task Guards_ThrowAsync() {
    await Assert.That(() => ReceptorRegistryServiceCollectionExtensions.AddReceptorRegistryContribution<OrdersRegistry>(null!))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new CompositeReceptorRegistry(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new CompositeReceptorRegistry([])).Throws<ArgumentException>();
  }

  /// <summary>
  /// When every contribution's implementation was removed, the host still builds (#2243): the receptor registry is the
  /// null registry, as for a host that declares no receptors, never a composite that throws for having none.
  /// </summary>
  [Test]
  public async Task EveryContributionRemoved_ServesTheNullRegistry_InsteadOfThrowingAsync() {
    var services = new ServiceCollection();
    _add<OrdersRegistry>(services);
    services.Remove(services.Single(d => d.ServiceType == typeof(OrdersRegistry)));
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IReceptorRegistry>()).IsSameReferenceAs(NullReceptorRegistry.Instance);
  }
}
