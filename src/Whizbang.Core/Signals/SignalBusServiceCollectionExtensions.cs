// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Signals;

/// <summary>
/// Registration for the <see cref="ISignalBus"/>. Registers the transport-agnostic bus and,
/// by default, the in-memory transport. Data providers add their own transports (Postgres
/// NOTIFY push, polling pull) alongside the default.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
/// <docs>operations/startup/startup-status#what-runs-in-each-stage</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupStageWiringTests.cs:TheSignalBus_StartsAtElect_WithTheNotificationConnection_OnceAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupPreflightHostTests.cs:HeldInPreflight_TheSignalBusStartsNoTransport_AndStartsItAtElectAsync</tests>
public static class SignalBusServiceCollectionExtensions {
  /// <summary>
  /// Register the signal bus and the default in-memory transport. Idempotent. The bus is started at
  /// <see cref="StartupStage.Elect"/>, with the notification connection its transports listen on.
  /// </summary>
  public static IServiceCollection AddWhizbangSignalBus(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);

    services.TryAddWhizbangDefaults();
    services.TryAddSingleton<SignalBus>();
    services.TryAddSingletonOverNullDefault<ISignalBus>(static sp => sp.GetRequiredService<SignalBus>());
    // ISignalSink is the bus-side entry point that transports/tail-workers call to deliver
    // received signals. Registered as the same singleton so the durable-log tail (and any
    // other consumer that raises signals into the bus) resolves to the actual bus instance.
    services.TryAddSingleton<ISignalSink>(static sp => sp.GetRequiredService<SignalBus>());
    // Default transport: in-memory loopback. Data providers add their own (Postgres NOTIFY)
    // alongside this via TryAddEnumerable, so single-process hosts still work. Pull sources
    // register separately as ISignalSource (see IPollSignalSource<T>).
    services.TryAddEnumerable(ServiceDescriptor.Singleton<ISignalTransport, InMemorySignalTransport>());
    // The host — not any consumer — starts the bus. Without this hosted service the transports
    // never subscribe and every wire doorbell is silently dropped (issue #505).
    // Self-contained: this worker requires the instance identity, so the extension that
    // registers it must guarantee the identity exists rather than assume a fuller composition.
    services.AddWhizbangInstanceIdentity();
    services.AddOptions<SignalBusOptions>();
    services.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<SignalBusOptions>>(sp => {
      var configuration = sp.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
      return new Microsoft.Extensions.Options.ConfigureOptions<SignalBusOptions>(options => {
        if (configuration is not null) {
#pragma warning disable IL2026 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs)
          Microsoft.Extensions.Configuration.ConfigurationBinder.Bind(
            configuration.GetSection("Whizbang:SignalBus"), options);
#pragma warning restore IL2026
        }
      });
    });
    // #1636: started at Elect, with the notification connection its loopback probe comes back over. Started with the
    // host, the probe sent pg_notify in Preflight, for a route that cannot answer until that connection is up. With no
    // stage gate (a host without the worker pipeline) the wrapper starts it with the host, as before. The wrapper is
    // keyed on the service type, so repeated calls stay idempotent.
    services.TryAddSingleton<SignalBusHostedService>();
    services.AddHostedServiceAtStage(StartupStage.Elect, static sp => sp.GetRequiredService<SignalBusHostedService>());
    // Shared state behind the signal-bus health component: probe verdicts, last wire arrival,
    // doorbell-liveness accounting. Written by the probe loop / transports / claim loop.
    services.TryAddSingleton<SignalBusLivenessState>();
    return services;
  }
}
