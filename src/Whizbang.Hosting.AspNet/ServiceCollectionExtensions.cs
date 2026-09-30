using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Health;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Extension methods for registering Whizbang ASP.NET Core hosting services.
/// </summary>
/// <docs>data/work-coordinator-strategies</docs>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/ServiceCollectionExtensionsTests.cs</tests>
public static class ServiceCollectionExtensions {
  /// <summary>Registered once by <c>AddWhizbangHealthChecks</c> so a second call can see the first.</summary>
  private sealed class ManagedHealthChecksMarker {
    public static ManagedHealthChecksMarker Instance { get; } = new();
  }

  /// <summary>
  /// Registers Whizbang's turnkey ASP.NET hosting: the flush, correlation and security-headers startup
  /// filters, <b>and</b> the schema-availability gate + managed-resource health checks — all injected
  /// automatically, no <c>app.Use…</c> calls required. Out of the box a host doing a non-blocking
  /// startup migration serves reads (writes get 503) and reports <b>ready</b> (migrating is healthy by
  /// default), so it is not rolled back; everything is configurable/overridable via options.
  /// </summary>
  /// <remarks>
  /// Idempotent — safe to call multiple times. The availability gate is a pass-through once the schema
  /// is ready, and is skipped entirely when no <see cref="Core.Workers.ISchemaReadyGate"/> is registered.
  /// </remarks>
  /// <tests>tests/Whizbang.Hosting.AspNet.Tests/ServiceCollectionExtensionsTests.cs:AddWhizbangAspNet_RegistersStartupFilterAsync</tests>
  /// <tests>tests/Whizbang.Hosting.AspNet.Tests/ServiceCollectionExtensionsTests.cs:AddWhizbangAspNet_CalledMultipleTimes_RegistersOnceAsync</tests>
  public static IServiceCollection AddWhizbangAspNet(this IServiceCollection services) {
    // The hosting options bind from configuration, so a deployment can change them without a code
    // change. Each binding is written out against its concrete type rather than through a generic
    // helper: the binder source generator only intercepts ConfigurationBinder.Bind when it can see
    // the type at the call site, and a type parameter sends it to the reflection path, which is an
    // AOT break (IL3050). Registered before the filters below, which resolve through IOptions<T>.
    services.AddOptions<WhizbangAvailabilityOptions>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<WhizbangAvailabilityOptions>>(sp => {
      var configuration = sp.GetService<IConfiguration>();
      return new ConfigureOptions<WhizbangAvailabilityOptions>(options => {
        if (configuration is not null) {
#pragma warning disable IL2026 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
          ConfigurationBinder.Bind(configuration.GetSection("Whizbang:AspNet:Availability"), options);
#pragma warning restore IL2026
        }
      });
    }));
    services.AddOptions<WhizbangCorrelationOptions>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<WhizbangCorrelationOptions>>(sp => {
      var configuration = sp.GetService<IConfiguration>();
      return new ConfigureOptions<WhizbangCorrelationOptions>(options => {
        if (configuration is not null) {
#pragma warning disable IL2026 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
          ConfigurationBinder.Bind(configuration.GetSection("Whizbang:AspNet:Correlation"), options);
#pragma warning restore IL2026
        }
      });
    }));
    services.AddOptions<WhizbangSecurityHeadersOptions>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<WhizbangSecurityHeadersOptions>>(sp => {
      var configuration = sp.GetService<IConfiguration>();
      return new ConfigureOptions<WhizbangSecurityHeadersOptions>(options => {
        if (configuration is not null) {
#pragma warning disable IL2026 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
          ConfigurationBinder.Bind(configuration.GetSection("Whizbang:AspNet:SecurityHeaders"), options);
#pragma warning restore IL2026
        }
      });
    }));

    services.TryAddEnumerable(
      ServiceDescriptor.Singleton<IStartupFilter, WhizbangFlushStartupFilter>());
    // Turnkey: capture an inbound correlation id (default X-Correlation-ID) at the start of the pipeline so
    // the request's first dispatch adopts it. Configure headers via WhizbangCorrelationOptions.
    services.TryAddEnumerable(
      ServiceDescriptor.Singleton<IStartupFilter, WhizbangCorrelationStartupFilter>());
    // Turnkey: hardened HTTP response headers (HSTS on TLS/forwarded-TLS, nosniff, X-Frame-Options,
    // CSP frame-ancestors, Referrer-Policy, Permissions-Policy) on every response, applied idempotently
    // (an edge-set header wins). Method filtering is OFF by default — opt in per service via
    // WhizbangSecurityHeadersOptions.AllowedMethods; opt out entirely via Enabled = false. Configure with
    // services.Configure<WhizbangSecurityHeadersOptions>(...).
    services.TryAddEnumerable(
      ServiceDescriptor.Singleton<IStartupFilter, WhizbangSecurityHeadersStartupFilter>());

    // Turnkey: auto-inject the schema-availability gate (serve reads / 503 writes during a startup
    // migration, pass-through once ready). Configure the mode or disable via WhizbangAvailabilityOptions,
    // or from Whizbang:AspNet:Availability.
    services.TryAddEnumerable(
      ServiceDescriptor.Singleton<IStartupFilter, WhizbangAvailabilityStartupFilter>());
    // Surfaces that must not share a failure domain with what they report on (the startup status
    // endpoint above all) register their own gate exemption here at mapping time.
    services.TryAddSingleton<WhizbangAvailabilityExemptions>();

    // Turnkey: managed-resource health — liveness ("live") + readiness ("ready") over every registered
    // IWhizbangHealthSource, so a migrating host reports ready-by-default instead of failing readiness.
    // AddWhizbangManagedHealth is idempotent (TryAdd); the check registration is guarded by a marker so
    // repeated AddWhizbangAspNet calls don't add duplicate-named checks.
    services.AddWhizbangManagedHealth();
    if (!services.Any(static d => d.ServiceType == typeof(ManagedHealthChecksMarker))) {
      services.AddSingleton(ManagedHealthChecksMarker.Instance);
      services.AddHealthChecks().AddWhizbangManagedHealthChecks();
    }
    return services;
  }
}
