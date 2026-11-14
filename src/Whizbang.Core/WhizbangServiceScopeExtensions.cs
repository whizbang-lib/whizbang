// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whizbang.Core;

/// <summary>An assembly a host anchors its service on (#2243): registered once per assembly, as an instance.</summary>
/// <param name="Assembly">The anchored assembly's name.</param>
/// <docs>extending/extensibility/replaceable-services#service-scope</docs>
public sealed record WhizbangServiceAnchor(string Assembly);

/// <summary>
/// Names the service a host serves, so it gets that service's generated registrations and not those of every service
/// loaded in the process (#2243).
/// </summary>
/// <remarks>
/// <c>WithEFCore&lt;TDbContext&gt;()</c> anchors on the DbContext's assembly, so a host that registers a DbContext
/// needs nothing more. The methods here are the escape hatch for a host with no DbContext in a process that holds
/// several services. Anchoring re-applies the scope to the registrations <c>AddWhizbang()</c> already made, so an
/// anchor may come before or after <c>AddWhizbang()</c>.
/// </remarks>
/// <docs>extending/extensibility/replaceable-services#service-scope</docs>
/// <tests>tests/Whizbang.Core.Tests/ServiceScope/ServiceScopedRegistrationTests.cs</tests>
public static class WhizbangServiceScopeExtensions {
  /// <summary>Anchors the host on the assembly that declares <typeparamref name="TMarker"/>.</summary>
  /// <typeparam name="TMarker">Any type in one of the service's assemblies.</typeparam>
  /// <param name="services">The host's service collection.</param>
  public static IServiceCollection AddWhizbangServiceAnchor<TMarker>(this IServiceCollection services) =>
    services.AddWhizbangServiceAnchor(TypeNameFormatter.AssemblySimpleName(typeof(TMarker).Assembly)!);

  /// <summary>Anchors the host on the assembly named <paramref name="assembly"/>.</summary>
  /// <param name="services">The host's service collection.</param>
  /// <param name="assembly">The anchored assembly's name.</param>
  public static IServiceCollection AddWhizbangServiceAnchor(this IServiceCollection services, string assembly) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentException.ThrowIfNullOrWhiteSpace(assembly);
    if (!AnchorsIn(services).Contains(assembly, StringComparer.Ordinal)) {
      services.AddSingleton(new WhizbangServiceAnchor(assembly));
      ServiceRegistrationCallbacks.Rescope(services);
    }
    return services;
  }

  /// <summary>Anchors the host on the assembly that declares <typeparamref name="TMarker"/>.</summary>
  /// <typeparam name="TMarker">Any type in one of the service's assemblies.</typeparam>
  /// <param name="builder">The Whizbang builder.</param>
  public static WhizbangBuilder ForServiceOf<TMarker>(this WhizbangBuilder builder) {
    ArgumentNullException.ThrowIfNull(builder);
    builder.Services.AddWhizbangServiceAnchor<TMarker>();
    return builder;
  }

  /// <summary>
  /// Registers the host's <see cref="ServiceAssemblyScope"/>, computed when first resolved from the anchors the
  /// container holds, and the startup report that names what it included and excluded.
  /// </summary>
  internal static IServiceCollection AddServiceAssemblyScope(this IServiceCollection services) {
    services.TryAddSingleton(sp => ServiceAssemblyScope.ComputeForProcess(
      sp.GetServices<WhizbangServiceAnchor>().Select(anchor => anchor.Assembly)));
    services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, ServiceAssemblyScopeStartupReporter>(
      sp => new ServiceAssemblyScopeStartupReporter(
        sp.GetRequiredService<ServiceAssemblyScope>(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ServiceAssemblyScopeStartupReporter>>())));
    return services;
  }

  /// <summary>The assemblies anchored in <paramref name="services"/>, in registration order.</summary>
  internal static IEnumerable<string> AnchorsIn(IServiceCollection services) =>
    services
      .Where(d => d.ServiceType == typeof(WhizbangServiceAnchor) && !d.IsKeyedService)
      .Select(d => d.ImplementationInstance)
      .OfType<WhizbangServiceAnchor>()
      .Select(anchor => anchor.Assembly);
}
