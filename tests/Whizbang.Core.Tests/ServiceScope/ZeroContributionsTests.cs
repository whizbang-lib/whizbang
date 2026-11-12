// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.ServiceScope;

/// <summary>
/// Zero contributions is a valid state for every service the contribution mechanism serves (#2243): when no
/// contribution resolves (each one's implementation was removed, or the host's scope leaves none), the host still
/// builds and the service answers as empty.
/// </summary>
/// <remarks>
/// Each test registers one assembly's contribution and then removes its implementation, which leaves the contribution
/// entry behind with nothing to resolve: the composite is built over no contributions at all.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/ContributionServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorRegistryServiceCollectionExtensions.cs</code-under-test>
public class ZeroContributionsTests {
  private sealed record SomethingHappened : IEvent;

  private sealed class OnlyRegistry : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => throw new NotSupportedException();
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => throw new NotSupportedException();
    public IReadOnlyList<Type> GetEventTypes() => throw new NotSupportedException();
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors => throw new NotSupportedException();
  }

  private sealed class OnlyPinnedIds : IPinnedIdRegistry {
    public string? GetPinnedId(Type type) => throw new NotSupportedException();
    public IReadOnlyList<(Type Type, string PinnedId)> GetAll() => throw new NotSupportedException();
  }

  private sealed class OnlyInvoker : IPerspectiveInvoker {
    public void QueueEvent(IEvent eventData) => throw new NotSupportedException();
    public Task InvokePerspectivesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private static ServiceCollection _withRemoved<TImplementation>(Action<IServiceCollection> register, Action<IServiceCollection> contribute)
      where TImplementation : class {
    var services = new ServiceCollection();
    register(services);
    contribute(services);
    services.Remove(services.Single(d => d.ServiceType == typeof(TImplementation)));
    return services;
  }

  [Test]
  public async Task PerspectiveRunnersAndEventTypes_WithNoContribution_AreEmptyAsync() {
    var services = _withRemoved<OnlyRegistry>(s => s.AddSingleton<OnlyRegistry>(), s => s.AddPerspectiveRunnerRegistryContribution<OnlyRegistry>());
    await using var provider = services.BuildServiceProvider();

    var runners = provider.GetRequiredService<IPerspectiveRunnerRegistry>();

    await Assert.That(runners.GetRunner("Anything", provider)).IsNull();
    await Assert.That(runners.GetRegisteredPerspectives()).IsEmpty();
    await Assert.That(runners.LifecycleStagesWithReceptors).IsEmpty();
    await Assert.That(provider.GetRequiredService<IEventTypeProvider>().GetEventTypes()).IsEmpty();
  }

  [Test]
  public async Task PinnedIdentities_WithNoContribution_AreEmptyAsync() {
    var services = _withRemoved<OnlyPinnedIds>(s => s.AddSingleton<OnlyPinnedIds>(), s => s.AddPinnedIdRegistryContribution<OnlyPinnedIds>());
    await using var provider = services.BuildServiceProvider();

    var pinned = provider.GetRequiredService<IPinnedIdRegistry>();

    await Assert.That(pinned.GetAll()).IsEmpty();
    await Assert.That(pinned.GetPinnedId(typeof(SomethingHappened))).IsNull();
  }

  [Test]
  public async Task ThePerspectiveInvoker_WithNoContribution_QueuesAndInvokesNothingAsync() {
    var services = _withRemoved<OnlyInvoker>(s => s.AddScoped<OnlyInvoker>(), s => s.AddPerspectiveInvokerContribution<OnlyInvoker>());
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var invoker = scope.ServiceProvider.GetRequiredService<IPerspectiveInvoker>();
    invoker.QueueEvent(new SomethingHappened());

    await Assert.That(() => invoker.InvokePerspectivesAsync()).ThrowsNothing();
  }

  [Test]
  public async Task TheCollisionReport_WithNoContribution_FindsNothingAsync() {
    var services = _withRemoved<OnlyRegistry>(s => s.AddSingleton<OnlyRegistry>(), s => s.AddPerspectiveRunnerRegistryContribution<OnlyRegistry>());
    await using var provider = services.BuildServiceProvider();

    var collisions = provider.GetServices<IRegistrationCollisionSource>().SelectMany(source => source.GetCollisions());

    await Assert.That(collisions).IsEmpty();
  }
}
