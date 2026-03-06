using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Configuration;

namespace Whizbang.Core;

/// <summary>
/// Static registry for service registration callbacks.
/// Consumer assemblies register their discovered services via module initializer.
/// This approach is AOT-compatible (no reflection required).
/// </summary>
/// <remarks>
/// <para>
/// The source generators (ServiceRegistrationGenerator, ReceptorDiscoveryGenerator) generate
/// module initializers that set these callbacks when the consumer assembly loads.
/// <see cref="ServiceCollectionExtensions.AddWhizbang"/> then invokes these callbacks
/// to register all discovered services automatically.
/// </para>
/// <para>
/// This pattern allows generated code in the consumer assembly to be invoked from
/// Whizbang.Core without reflection, while preserving user control via options.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Generated module initializer in consumer assembly:
/// [ModuleInitializer]
/// internal static void RegisterServiceCallbacks() {
///   ServiceRegistrationCallbacks.LensServices = (services, options) =&gt;
///     services.AddLensServices(o =&gt; o.IncludeSelfRegistration = options.IncludeSelfRegistration);
/// }
///
/// // User code - services are auto-registered:
/// services.AddWhizbang();  // Invokes LensServices callback automatically
/// </code>
/// </example>
public static class ServiceRegistrationCallbacks {
  private static readonly object _lock = new();

  /// <summary>
  /// Callback for registering discovered lens services (ILensQuery implementations).
  /// Set by source-generated module initializer in consumer assembly.
  /// </summary>
  public static Action<IServiceCollection, ServiceRegistrationOptions>? LensServices { get; set; }

  /// <summary>
  /// Callback for registering discovered perspective services (IPerspective implementations).
  /// Set by source-generated module initializer in consumer assembly.
  /// </summary>
  public static Action<IServiceCollection, ServiceRegistrationOptions>? PerspectiveServices { get; set; }

  /// <summary>
  /// Callback for registering the generated dispatcher and receptor infrastructure.
  /// Set by source-generated module initializer in consumer assembly.
  /// </summary>
  public static Action<IServiceCollection>? Dispatcher { get; set; }

  /// <summary>
  /// Invokes all registered service callbacks with the provided options.
  /// Called by <see cref="ServiceCollectionExtensions.AddWhizbang"/> to auto-register services.
  /// </summary>
  /// <param name="services">The service collection to register services in.</param>
  /// <param name="options">Options controlling registration behavior.</param>
  internal static void InvokeAll(IServiceCollection services, ServiceRegistrationOptions options) {
    lock (_lock) {
      LensServices?.Invoke(services, options);
      PerspectiveServices?.Invoke(services, options);
      Dispatcher?.Invoke(services);
    }
  }

  /// <summary>
  /// Resets all callbacks. Used for testing purposes only.
  /// </summary>
  internal static void Reset() {
    lock (_lock) {
      LensServices = null;
      PerspectiveServices = null;
      Dispatcher = null;
    }
  }
}
