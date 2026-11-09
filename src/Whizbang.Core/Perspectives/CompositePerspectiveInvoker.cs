// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The perspective invoker over every contributed <see cref="IPerspectiveInvoker"/>: the invoker a host whose
/// perspectives are declared in more than one assembly serves (#2200).
/// </summary>
/// <remarks>
/// <para>
/// Each assembly's generated invoker runs only the perspectives that assembly declares, so every event is queued with
/// every contribution, and invoking or disposing the composite invokes or disposes each, in the order given (the
/// registration hands them over latest registered first). A perspective two assemblies both declare is run by each,
/// and is reported at startup by the perspective runner registry, which names the same collision
/// (<see cref="RegistrationCollisions"/>).
/// </para>
/// <para>
/// Scoped, as the generated invoker is: each scope (a request, a message batch) gets its own contributions and its
/// own composite over them.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveInvokerContributionTests.cs</tests>
public sealed class CompositePerspectiveInvoker : IPerspectiveInvoker {
  private readonly IPerspectiveInvoker[] _invokers;

  /// <summary>Combines the given invokers, in the order given.</summary>
  /// <param name="invokers">The contributions, latest registered first.</param>
  public CompositePerspectiveInvoker(IEnumerable<IPerspectiveInvoker> invokers) {
    ArgumentNullException.ThrowIfNull(invokers);
    _invokers = [.. invokers];
  }

  /// <inheritdoc />
  public void QueueEvent(IEvent eventData) {
    foreach (var invoker in _invokers) {
      invoker.QueueEvent(eventData);
    }
  }

  /// <inheritdoc />
  public async Task InvokePerspectivesAsync(CancellationToken cancellationToken = default) {
    foreach (var invoker in _invokers) {
      await invoker.InvokePerspectivesAsync(cancellationToken).ConfigureAwait(false);
    }
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    foreach (var invoker in _invokers) {
      await invoker.DisposeAsync().ConfigureAwait(false);
    }
  }
}

/// <summary>
/// Registers one assembly's <see cref="IPerspectiveInvoker"/> as a contribution to the invoker the container serves.
/// </summary>
/// <remarks>
/// Every assembly that declares perspectives generates an invoker of its own, and its generated registration calls
/// <see cref="AddPerspectiveInvokerContribution{TInvoker}"/>. Registered as a plain scoped service, the last
/// assembly's invoker displaced the others, and an event store that invokes perspectives inline (the Dapper event
/// store) ran only that assembly's perspectives (#2200). The served invoker is a <see cref="CompositePerspectiveInvoker"/>
/// over every contribution in the scope, latest registered first. An invoker the host registers itself before the
/// contributions is left alone.
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveInvokerContributionTests.cs</tests>
public static class PerspectiveInvokerServiceCollectionExtensions {
  /// <summary>The service key each assembly's invoker is registered under as a contribution.</summary>
  public const string CONTRIBUTION_KEY = "whizbang:perspective-invoker-contribution";

  /// <summary>Adds <typeparamref name="TInvoker"/>'s perspectives to the scoped <see cref="IPerspectiveInvoker"/> the container serves.</summary>
  /// <typeparam name="TInvoker">The contributing invoker, itself registered in the container as a scoped service.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveInvokerContributionTests.cs:TwoAssemblies_AnEventReachesEveryAssemblysInvokerAsync</tests>
  public static IServiceCollection AddPerspectiveInvokerContribution<TInvoker>(this IServiceCollection services)
      where TInvoker : class, IPerspectiveInvoker {
    ArgumentNullException.ThrowIfNull(services);
    return services.AddScopedContribution<IPerspectiveInvoker, TInvoker>(
      CONTRIBUTION_KEY, static invokers => new CompositePerspectiveInvoker(invokers));
  }
}
