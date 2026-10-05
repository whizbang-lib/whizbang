// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Dispatch;

/// <summary>
/// A claim-key prefix whose claims the general expiry prune leaves alone, because the package that owns
/// the key convention prunes them on its own terms.
/// </summary>
/// <remarks>
/// The saga framework registers its four prefixes: it prunes its sweep, completion and continuation claims
/// after its own retention, and keeps its abandonment claims until an operator re-drives the saga. Any
/// number may be registered, and a prefix registered twice counts once.
/// </remarks>
/// <param name="KeyPrefix">The literal key prefix.</param>
/// <docs>fundamentals/dispatcher/publish-once#claim-expiry</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/ClaimedEmissionPruneStepTests.cs:Run_KeepsEveryRegisteredPrefix_OnceEachAsync</tests>
public sealed record RetainedClaimKeyPrefix(string KeyPrefix);

/// <summary>Registration for <see cref="RetainedClaimKeyPrefix"/>.</summary>
/// <docs>fundamentals/dispatcher/publish-once#claim-expiry</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/ClaimedEmissionPruneStepTests.cs:AddRetainedClaimKeyPrefix_RejectsABlankPrefixAsync</tests>
public static class RetainedClaimKeyPrefixServiceCollectionExtensions {
  /// <summary>
  /// Keeps the claims whose key starts with <paramref name="keyPrefix"/> out of the general expiry prune.
  /// Registering the same prefix again changes nothing.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="keyPrefix">The literal key prefix; must not be blank.</param>
  /// <returns>The same service collection.</returns>
  public static IServiceCollection AddRetainedClaimKeyPrefix(this IServiceCollection services, string keyPrefix) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
    var prefix = new RetainedClaimKeyPrefix(keyPrefix);
    if (!services.Any(d => d.ServiceType == typeof(RetainedClaimKeyPrefix) && Equals(d.ImplementationInstance, prefix))) {
      services.AddSingleton(prefix);
    }
    return services;
  }
}
