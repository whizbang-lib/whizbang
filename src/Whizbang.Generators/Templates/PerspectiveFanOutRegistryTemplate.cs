#region HEADER
// Template for PerspectiveFanOutDiscoveryGenerator. Embedded, not compiled: this region becomes the generated header,
// and __NAMESPACE__, __ASSEMBLY__ and FAN_OUT_ENTRIES are replaced at generation.
#endregion

#nullable enable

namespace __NAMESPACE__ {

/// <summary>
/// This assembly's perspective fan-outs (<see cref="global::Whizbang.Core.Perspectives.IPerspectiveFanOutFor{TModel, TEvent}"/>):
/// its contribution to the fan-out registry the host serves. Reflection-free; the invokers cast statically.
/// </summary>
public sealed class GeneratedPerspectiveFanOutRegistry : global::Whizbang.Core.Perspectives.IPerspectiveFanOutRegistry {
  /// <summary>The discovered fan-outs, one per (model, foreign event).</summary>
  internal static readonly global::System.Collections.Generic.IReadOnlyList<global::Whizbang.Core.Perspectives.PerspectiveFanOutEntry> All = new global::Whizbang.Core.Perspectives.PerspectiveFanOutEntry[] {
    #region FAN_OUT_ENTRIES
    // Replaced with one entry per discovered fan-out
    #endregion
  };

  /// <inheritdoc/>
  public global::System.Collections.Generic.IReadOnlyList<global::Whizbang.Core.Perspectives.PerspectiveFanOutEntry> Entries => All;
}

/// <summary>Registers this assembly's perspective fan-outs.</summary>
public static class PerspectiveFanOutRegistryExtensions {
  /// <summary>
  /// Adds this assembly's fan-outs to the fan-out registry the container serves. Called by AddWhizbang() through the
  /// module initializer below; call it directly only when configuring DI without AddWhizbang().
  /// </summary>
  public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddPerspectiveFanOutRegistry(
      this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services) {
    global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<GeneratedPerspectiveFanOutRegistry>(services);
    global::Whizbang.Core.Perspectives.PerspectiveFanOutRegistryServiceCollectionExtensions.AddPerspectiveFanOutRegistryContribution<GeneratedPerspectiveFanOutRegistry>(services);
    return services;
  }
}

/// <summary>Hands this assembly's fan-out registration to AddWhizbang(), named by the assembly.</summary>
internal static class PerspectiveFanOutRegistryModuleInitializer {
  [global::System.Runtime.CompilerServices.ModuleInitializer]
  internal static void Initialize() {
    global::Whizbang.Core.ServiceRegistrationCallbacks.RegisterPerspectiveFanOutRegistry(__ASSEMBLY__, static services =>
      PerspectiveFanOutRegistryExtensions.AddPerspectiveFanOutRegistry(services));
  }
}

}
