// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whizbang.Core.Observability;

/// <summary>
/// Opts a host in to a trace store. <c>AddWhizbang</c> registers none, so the dispatcher does no
/// trace work until a host asks for it (#1601).
/// </summary>
/// <docs>fundamentals/persistence/observability#trace-store-registration</docs>
/// <tests>tests/Whizbang.Observability.Tests/DependencyInjection/TraceStoreRegistrationTests.cs</tests>
public static class TraceStoreRegistration {
  /// <summary>
  /// Registers the bounded <see cref="InMemoryTraceStore"/> as the host's only <see cref="ITraceStore"/>,
  /// keeping the most recent <see cref="InMemoryTraceStore.DEFAULT_CAPACITY"/> envelopes.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/DependencyInjection/TraceStoreRegistrationTests.cs:AddWhizbangInMemoryTraceStore_RegistersOneSingletonWithTheDefaultCapacityAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/DependencyInjection/TraceStoreRegistrationTests.cs:AddWhizbangInMemoryTraceStore_NullServices_ThrowsAsync</tests>
  public static IServiceCollection AddWhizbangInMemoryTraceStore(this IServiceCollection services) =>
    services.AddWhizbangInMemoryTraceStore(InMemoryTraceStore.DEFAULT_CAPACITY);

  /// <summary>
  /// Registers the bounded <see cref="InMemoryTraceStore"/> as the host's only <see cref="ITraceStore"/>,
  /// keeping the most recent <paramref name="capacity"/> envelopes. A store registered earlier is replaced.
  /// </summary>
  /// <param name="services">The host's services.</param>
  /// <param name="capacity">The most envelopes held; must be positive.</param>
  /// <exception cref="ArgumentOutOfRangeException">The capacity is zero or negative.</exception>
  /// <tests>tests/Whizbang.Observability.Tests/DependencyInjection/TraceStoreRegistrationTests.cs:AddWhizbangInMemoryTraceStore_WithCapacity_UsesItAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/DependencyInjection/TraceStoreRegistrationTests.cs:AddWhizbangInMemoryTraceStore_CalledAfterAnotherStore_LeavesExactlyOneAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/DependencyInjection/TraceStoreRegistrationTests.cs:AddWhizbangInMemoryTraceStore_CapacityNotPositive_ThrowsAtRegistrationAsync</tests>
  public static IServiceCollection AddWhizbangInMemoryTraceStore(this IServiceCollection services, int capacity) {
    ArgumentNullException.ThrowIfNull(services);
    // Rejected here so a bad capacity fails at registration, not at the first dispatch
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

    services.RemoveAll<ITraceStore>();
    services.AddSingleton<ITraceStore>(_ => new InMemoryTraceStore(capacity));
    return services;
  }
}
