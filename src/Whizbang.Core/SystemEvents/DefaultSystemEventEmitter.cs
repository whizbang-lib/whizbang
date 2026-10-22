// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// The <see cref="ISystemEventEmitter"/> a service gets when it registers none of its own: the real
/// <see cref="SystemEventEmitter"/> wherever an event store is registered, so the
/// <see cref="SystemEventOptions"/> defaults decide what is stored, and the
/// <see cref="NullSystemEventEmitter"/> where none is.
/// </summary>
/// <remarks>
/// <para>
/// Registered with TryAdd by <c>AddWhizbang</c> and by <c>AddSystemEvents</c>, so whichever runs first
/// registers it, the other adds nothing, and a host's own registration made before either still wins.
/// A host that registers its emitter after them wins too, because the last registration is the one
/// resolved.
/// </para>
/// <para>
/// A singleton. Whether a store exists is decided when the emitter is first resolved, after the driver
/// has registered its store whatever the order of the calls, and each record is appended through a store
/// resolved in a scope of its own (<see cref="ScopePerAppendSystemEventSink"/>), so a scoped store is
/// never captured and a singleton store works the same way.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/system-events#emitter</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/SystemEventEmitterRegistrationTests.cs</tests>
internal static class DefaultSystemEventEmitter {
  /// <summary>Registers the default emitter unless an emitter is already registered.</summary>
  /// <param name="services">The service collection.</param>
  internal static void TryAdd(IServiceCollection services) =>
    services.TryAddSingleton<ISystemEventEmitter>(Create);

  /// <summary>
  /// Builds the default emitter: the real one when the container reports an event store, the null one when
  /// it reports none or cannot say.
  /// </summary>
  /// <param name="serviceProvider">The root service provider.</param>
  /// <returns>The emitter to register.</returns>
  /// <tests>tests/Whizbang.Core.Tests/SystemEvents/SystemEventEmitterRegistrationTests.cs:Create_WhenTheProviderCannotSayWhetherAStoreIsRegistered_ReturnsTheNullEmitterAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/SystemEvents/SystemEventEmitterRegistrationTests.cs:AddWhizbang_WithNoEventStore_KeepsTheNullEmitterAsync</tests>
  internal static ISystemEventEmitter Create(IServiceProvider serviceProvider) {
    if (serviceProvider.GetService<IServiceProviderIsService>()?.IsService(typeof(IEventStore)) != true) {
      return new NullSystemEventEmitter();
    }

    return SystemEventEmitter.ForSink(
      serviceProvider.GetRequiredService<IOptions<SystemEventOptions>>(),
      new ScopePerAppendSystemEventSink(serviceProvider.GetRequiredService<IServiceScopeFactory>()),
      serviceProvider.GetRequiredService<IServiceInstanceProvider>(),
      serviceProvider.GetRequiredService<ILogger<SystemEventEmitter>>(),
      serviceProvider.GetService<IAuditDecisionHook>());
  }
}
