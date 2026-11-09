// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Registers one assembly's <see cref="IEventTypeProvider"/> as a contribution to the provider the container serves.
/// </summary>
/// <docs>fundamentals/events/events</docs>
public static class EventTypeProviderServiceCollectionExtensions {
  /// <summary>Registers <typeparamref name="TProvider"/>'s event types.</summary>
  /// <typeparam name="TProvider">The contributing provider, itself registered in the container.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection.</returns>
  public static IServiceCollection AddEventTypeProviderContribution<TProvider>(this IServiceCollection services)
      where TProvider : class, IEventTypeProvider {
    services.AddSingleton<IEventTypeProvider>(sp => sp.GetRequiredService<TProvider>());
    return services;
  }
}
