// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// An event reaches every assembly's perspective invoker through the one <see cref="IPerspectiveInvoker"/> the
/// container serves (#2200).
/// </summary>
/// <remarks>
/// Each assembly that declares perspectives generates an invoker that runs only its own perspectives. Registered as a
/// plain scoped service, the last assembly's invoker displaced the others, so an event store invoking perspectives
/// inline ran only that assembly's.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Perspectives/CompositePerspectiveInvoker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/ContributionServiceCollectionExtensions.cs</code-under-test>
public class PerspectiveInvokerContributionTests {
  private sealed record OrderPlaced : IEvent;

  private abstract class AssemblyInvoker : IPerspectiveInvoker {
    public List<string> Calls { get; } = [];
    public void QueueEvent(IEvent eventData) => Calls.Add("queue");
    public Task InvokePerspectivesAsync(CancellationToken cancellationToken = default) {
      Calls.Add("invoke");
      return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() {
      Calls.Add("dispose");
      return ValueTask.CompletedTask;
    }
  }

  private sealed class OrdersInvoker : AssemblyInvoker;
  private sealed class BillingInvoker : AssemblyInvoker;
  private sealed class HostsOwnInvoker : AssemblyInvoker;

  private static ServiceProvider _twoAssemblies(IServiceCollection? services = null) {
    services ??= new ServiceCollection();
    services.AddScoped<OrdersInvoker>();
    services.AddPerspectiveInvokerContribution<OrdersInvoker>();
    services.AddScoped<BillingInvoker>();
    services.AddPerspectiveInvokerContribution<BillingInvoker>();
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task TwoAssemblies_AnEventReachesEveryAssemblysInvokerAsync() {
    await using var provider = _twoAssemblies();
    await using var scope = provider.CreateAsyncScope();
    var invoker = scope.ServiceProvider.GetRequiredService<IPerspectiveInvoker>();

    invoker.QueueEvent(new OrderPlaced());
    await invoker.InvokePerspectivesAsync();
    await invoker.DisposeAsync();

    string[] expected = ["queue", "invoke", "dispose"];
    await Assert.That(scope.ServiceProvider.GetRequiredService<OrdersInvoker>().Calls.Take(3).SequenceEqual(expected)).IsTrue()
      .Because("the first assembly's perspectives run however many assemblies registered after it");
    await Assert.That(scope.ServiceProvider.GetRequiredService<BillingInvoker>().Calls.Take(3).SequenceEqual(expected)).IsTrue();
  }

  [Test]
  public async Task EachScope_GetsItsOwnInvokersAsync() {
    await using var provider = _twoAssemblies();
    await using var first = provider.CreateAsyncScope();
    await using var second = provider.CreateAsyncScope();

    first.ServiceProvider.GetRequiredService<IPerspectiveInvoker>().QueueEvent(new OrderPlaced());

    await Assert.That(first.ServiceProvider.GetRequiredService<IPerspectiveInvoker>())
      .IsSameReferenceAs(first.ServiceProvider.GetRequiredService<IPerspectiveInvoker>());
    await Assert.That(second.ServiceProvider.GetRequiredService<OrdersInvoker>().Calls).IsEmpty()
      .Because("an event queued in one scope is that scope's unit of work only");
  }

  [Test]
  public async Task AHostsOwnInvoker_RegisteredFirst_IsTheOneServedAsync() {
    IServiceCollection services = new ServiceCollection();
    services.AddScoped<IPerspectiveInvoker, HostsOwnInvoker>();
    await using var provider = _twoAssemblies(services);
    await using var scope = provider.CreateAsyncScope();

    await Assert.That(scope.ServiceProvider.GetRequiredService<IPerspectiveInvoker>()).IsTypeOf<HostsOwnInvoker>();
  }

  [Test]
  public async Task Guards_ThrowAsync() {
    await Assert.That(() => PerspectiveInvokerServiceCollectionExtensions.AddPerspectiveInvokerContribution<OrdersInvoker>(null!))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new CompositePerspectiveInvoker(null!)).Throws<ArgumentNullException>();
  }
}
