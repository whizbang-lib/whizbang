// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Perspectives;

/// <summary>The perspective invoker over every contributed <see cref="IPerspectiveInvoker"/>.</summary>
/// <docs>fundamentals/perspectives/perspectives</docs>
public sealed class CompositePerspectiveInvoker : IPerspectiveInvoker {
  private readonly IPerspectiveInvoker _last;

  /// <summary>Combines the given invokers.</summary>
  /// <param name="invokers">The contributions.</param>
  public CompositePerspectiveInvoker(IEnumerable<IPerspectiveInvoker> invokers) {
    ArgumentNullException.ThrowIfNull(invokers);
    _last = invokers.Last();
  }

  /// <inheritdoc />
  public void QueueEvent(IEvent eventData) => _last.QueueEvent(eventData);

  /// <inheritdoc />
  public Task InvokePerspectivesAsync(CancellationToken cancellationToken = default) => _last.InvokePerspectivesAsync(cancellationToken);

  /// <inheritdoc />
  public ValueTask DisposeAsync() => _last.DisposeAsync();
}

/// <summary>Registers one assembly's <see cref="IPerspectiveInvoker"/> as a contribution.</summary>
/// <docs>fundamentals/perspectives/perspectives</docs>
public static class PerspectiveInvokerServiceCollectionExtensions {
  /// <summary>Registers <typeparamref name="TInvoker"/>.</summary>
  /// <typeparam name="TInvoker">The contributing invoker.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection.</returns>
  public static IServiceCollection AddPerspectiveInvokerContribution<TInvoker>(this IServiceCollection services)
      where TInvoker : class, IPerspectiveInvoker {
    services.AddScoped<IPerspectiveInvoker>(sp => sp.GetRequiredService<TInvoker>());
    return services;
  }
}
