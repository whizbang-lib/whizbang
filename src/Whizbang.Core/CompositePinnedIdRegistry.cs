// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core;

/// <summary>The pinned-identity registry over every contributed <see cref="IPinnedIdRegistry"/>.</summary>
/// <docs>fundamentals/identity/pinned-identity</docs>
public sealed class CompositePinnedIdRegistry : IPinnedIdRegistry {
  private readonly IPinnedIdRegistry _last;

  /// <summary>Combines the given registries.</summary>
  /// <param name="registries">The contributions.</param>
  public CompositePinnedIdRegistry(IEnumerable<IPinnedIdRegistry> registries) {
    ArgumentNullException.ThrowIfNull(registries);
    _last = registries.Last();
  }

  /// <inheritdoc />
  public string? GetPinnedId(Type type) => _last.GetPinnedId(type);

  /// <inheritdoc />
  public IReadOnlyList<(Type Type, string PinnedId)> GetAll() => _last.GetAll();
}

/// <summary>Registers one assembly's <see cref="IPinnedIdRegistry"/> as a contribution.</summary>
/// <docs>fundamentals/identity/pinned-identity</docs>
public static class PinnedIdRegistryServiceCollectionExtensions {
  /// <summary>Registers <typeparamref name="TRegistry"/>'s pinned types.</summary>
  /// <typeparam name="TRegistry">The contributing registry.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection.</returns>
  public static IServiceCollection AddPinnedIdRegistryContribution<TRegistry>(this IServiceCollection services)
      where TRegistry : class, IPinnedIdRegistry {
    services.AddSingleton<IPinnedIdRegistry>(sp => sp.GetRequiredService<TRegistry>());
    return services;
  }
}
