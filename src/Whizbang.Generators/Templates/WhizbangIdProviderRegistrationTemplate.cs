using System;
using Microsoft.Extensions.DependencyInjection;

namespace __NAMESPACE__.Generated;

#region HEADER
// This region gets replaced with generated header + timestamp
#endregion

/// <summary>
/// Auto-registration for all WhizbangId providers in this assembly.
/// This class is generated once per assembly containing WhizbangId types.
/// </summary>
public static class WhizbangIdProviderRegistration {
  /// <summary>
  /// Module initializer that registers provider factories with the global registry.
  /// Runs automatically when the assembly is loaded - no explicit call needed.
  /// </summary>
  /// <remarks>
  /// This method:
  /// <list type="bullet">
  /// <item>Registers factory functions for creating typed providers</item>
  /// <item>Registers DI callback for AddWhizbangIdProviders() integration</item>
  /// </list>
  /// </remarks>
  [System.Runtime.CompilerServices.ModuleInitializer]
  public static void Initialize() {
    #region FACTORY_REGISTRATIONS
    // This region gets replaced with RegisterFactory calls for each WhizbangId type
    #endregion

    // Register DI callback
    global::Whizbang.Core.WhizbangIdProviderRegistry.RegisterDICallback(RegisterAll);
  }

  /// <summary>
  /// Registers all WhizbangId providers from this assembly with the DI container.
  /// Called by AddWhizbangIdProviders() extension method.
  /// </summary>
  /// <param name="services">The service collection to register providers with</param>
  /// <param name="baseProvider">The base provider to use for all typed providers</param>
  public static void RegisterAll(
      global::Microsoft.Extensions.DependencyInjection.IServiceCollection services,
      global::Whizbang.Core.IWhizbangIdProvider baseProvider) {

    #region DI_REGISTRATIONS
    // This region gets replaced with AddSingleton calls for each WhizbangId type
    #endregion
  }
}
