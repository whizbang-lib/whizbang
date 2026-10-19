// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Capabilities;

/// <summary>
/// Registration of capability detection. A provider package calls it after registering its
/// <see cref="ICapabilityProvider"/>, so a host gets the probe, the verdict and the gauge with no wiring.
/// </summary>
/// <docs>operations/infrastructure/capabilities#adding-a-capability-provider</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityServiceCollectionExtensionsTests.cs</tests>
public static class CapabilityServiceCollectionExtensions {
  /// <summary>
  /// Registers, once per container, the <see cref="ICapabilityRegistry"/> over every registered
  /// <see cref="ICapabilityProvider"/> (with its gauge), the <c>Capabilities</c> startup step and the
  /// <c>Whizbang:Capabilities</c> binding. Safe to call more than once.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The same collection.</returns>
  public static IServiceCollection AddWhizbangCapabilities(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);

    services.AddOptions<CapabilityOptions>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<CapabilityOptions>, ConfigureCapabilityOptions>(
      sp => new ConfigureCapabilityOptions(sp.GetService<IConfiguration>())));

    services.AddMetrics();
    services.TryAddSingleton<WhizbangMetrics>();
    services.TryAddSingleton<ICapabilityRegistry>(sp => new CapabilityRegistry(
      sp.GetServices<ICapabilityProvider>(),
      sp.GetRequiredService<ILogger<CapabilityRegistry>>(),
      sp.GetRequiredService<WhizbangMetrics>()));
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupStep, CapabilitiesStartupStep>(sp =>
      new CapabilitiesStartupStep(
        sp.GetRequiredService<ICapabilityRegistry>(),
        sp.GetRequiredService<IOptions<CapabilityOptions>>(),
        sp.GetRequiredService<ILogger<CapabilitiesStartupStep>>())));
    return services;
  }
}

/// <summary>
/// Binds <see cref="CapabilityOptions"/> from <c>Whizbang:Capabilities</c>. The section is named
/// literally at the call so the configuration binder source generator compiles the bind to typed
/// assignments: no reflection reaches the AOT path. A host without configuration keeps the code values.
/// </summary>
/// <docs>operations/infrastructure/capabilities#configuration</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityServiceCollectionExtensionsTests.cs</tests>
internal sealed class ConfigureCapabilityOptions(IConfiguration? configuration) : IConfigureOptions<CapabilityOptions> {
  public void Configure(CapabilityOptions options) {
    if (configuration is null) {
      return;
    }
#pragma warning disable IL2026, IL3050 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
    configuration.GetSection("Whizbang:Capabilities").Bind(options);
#pragma warning restore IL2026, IL3050
  }
}
