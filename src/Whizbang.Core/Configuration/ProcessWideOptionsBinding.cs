// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Health;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Resilience;
using Whizbang.Core.RunControl;
using Whizbang.Core.Security;
using Whizbang.Core.Startup;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Configuration;

/// <summary>
/// Binds the process-wide options classes from configuration (#1014), so an operator can change them
/// at deploy time without a code change.
/// </summary>
/// <remarks>
/// <para>Every binding names its concrete type at the call site. The configuration binder source
/// generator intercepts <see cref="ConfigurationBinder.Bind(IConfiguration, object?)"/> only when it
/// can see the type there; a generic helper taking <c>TOptions</c> falls back to the reflection
/// overload, which is an AOT break (IL3050). The repetition is deliberate.</para>
/// <para>Precedence is the one the rest of the framework uses. A key that is present overrides the
/// value code set; a key that is absent leaves the code value (or the class default) alone. For the
/// <c>IOptions&lt;T&gt;</c> classes the binding runs where the worker pipeline registers it, so a
/// <c>services.Configure&lt;T&gt;</c> call made after <c>AddWhizbang()</c> still has the last word.</para>
/// </remarks>
/// <docs>operations/configuration/configuration-reference</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/ProcessWideOptionsBindingTests.cs</tests>
internal static class ProcessWideOptionsBinding {

  /// <summary>An empty configuration, bound when the host registered no <see cref="IConfiguration"/>.</summary>
  private static readonly IConfiguration _empty = new ConfigurationBuilder().Build();

  /// <summary>
  /// The named section of the host's configuration, or an empty one when the host registered none,
  /// so binding degrades to the code defaults instead of failing to resolve.
  /// </summary>
  internal static IConfiguration Section(IServiceProvider services, string key)
    => services.GetService<IConfiguration>()?.GetSection(key) ?? _empty;

  /// <summary>
  /// Registers the <c>IOptions&lt;T&gt;</c> bindings for the process-wide classes the worker pipeline
  /// consumes. A repeat call binds the same keys again, which is harmless: none of these classes
  /// carries a collection the binder would append to.
  /// </summary>
  internal static void AddProcessWideOptionsBinding(IServiceCollection services) {
    services.AddOptions<SystemEventOptions>();
    services.AddSingleton<IConfigureOptions<SystemEventOptions>>(sp =>
      new ConfigureOptions<SystemEventOptions>(options => BindSystemEvents(sp, options)));

    services.AddOptions<RedeliveryPumpOptions>();
    services.AddSingleton<IConfigureOptions<RedeliveryPumpOptions>>(sp =>
      new ConfigureOptions<RedeliveryPumpOptions>(options => {
#pragma warning disable IL2026 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
        Section(sp, "Whizbang:Redelivery").Bind(options);
#pragma warning restore IL2026
      }));
    // The re-delivery receptor resolves the plain instance; hand it the bound one.
    services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<RedeliveryPumpOptions>>().Value);

    services.AddOptions<ThrottleRetryOptions>();
    services.AddSingleton<IConfigureOptions<ThrottleRetryOptions>>(sp =>
      new ConfigureOptions<ThrottleRetryOptions>(options => {
#pragma warning disable IL2026 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
        Section(sp, "Whizbang:ThrottleRetry").Bind(options);
#pragma warning restore IL2026
      }));

    services.AddOptions<StreamRateLimiterOptions>();
    services.AddSingleton<IConfigureOptions<StreamRateLimiterOptions>>(sp =>
      new ConfigureOptions<StreamRateLimiterOptions>(options => {
#pragma warning disable IL2026 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
        Section(sp, "Whizbang:StreamRateLimiter").Bind(options);
#pragma warning restore IL2026
      }));
    // The limiter is keyed by stream, so one per process is the meaningful shape; an application
    // that injects it gets the bound settings. One constructed by hand uses what it is given.
    services.TryAddSingleton(sp => new StreamRateLimiter(
      sp.GetRequiredService<IOptions<StreamRateLimiterOptions>>().Value,
      sp.GetRequiredService<ILogger<StreamRateLimiter>>()));

    services.AddOptions<DebuggerAwareClockOptions>();
    services.AddSingleton<IConfigureOptions<DebuggerAwareClockOptions>>(sp =>
      new ConfigureOptions<DebuggerAwareClockOptions>(options => {
#pragma warning disable IL2026 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
        Section(sp, "Whizbang:DebuggerAwareClock").Bind(options);
#pragma warning restore IL2026
      }));

    services.AddOptions<StandbyWatcherOptions>();
    services.AddSingleton<IConfigureOptions<StandbyWatcherOptions>>(sp =>
      new ConfigureOptions<StandbyWatcherOptions>(options => {
#pragma warning disable IL2026 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
        Section(sp, "Whizbang:StandbyWatcher").Bind(options);
#pragma warning restore IL2026
      }));
    // The watcher and the handshake take the plain instance. TryAdd keeps a host's own registration.
    services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<StandbyWatcherOptions>>().Value);

    services.AddOptions<PerspectiveSnapshotOptions>();
    services.AddSingleton<IConfigureOptions<PerspectiveSnapshotOptions>>(sp =>
      new ConfigureOptions<PerspectiveSnapshotOptions>(options => {
#pragma warning disable IL2026 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
        Section(sp, "Whizbang:Perspectives:Snapshots").Bind(options);
#pragma warning restore IL2026
      }));

    services.AddOptions<PerspectiveRewindOptions>();
    services.AddSingleton<IConfigureOptions<PerspectiveRewindOptions>>(sp =>
      new ConfigureOptions<PerspectiveRewindOptions>(options => {
#pragma warning disable IL2026 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
        Section(sp, "Whizbang:Perspectives:Rewind").Bind(options);
#pragma warning restore IL2026
      }));

    services.AddOptions<PerspectiveStreamLockOptions>();
    services.AddSingleton<IConfigureOptions<PerspectiveStreamLockOptions>>(sp =>
      new ConfigureOptions<PerspectiveStreamLockOptions>(options => {
#pragma warning disable IL2026 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
        Section(sp, "Whizbang:Perspectives:StreamLock").Bind(options);
#pragma warning restore IL2026
      }));

    services.AddOptions<PerspectiveStreamAffinityOptions>();
    services.AddSingleton<IConfigureOptions<PerspectiveStreamAffinityOptions>>(sp =>
      new ConfigureOptions<PerspectiveStreamAffinityOptions>(options => {
#pragma warning disable IL2026 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
        Section(sp, "Whizbang:Workers:PerspectiveAffinity").Bind(options);
#pragma warning restore IL2026
      }));

    // The IOptions<WhizbangCoreOptions> view (read by the EF Core lens queries and work coordinator)
    // is a separate object from the singleton AddWhizbang registers; both read the same section.
    services.AddOptions<WhizbangCoreOptions>();
    services.AddSingleton<IConfigureOptions<WhizbangCoreOptions>>(sp =>
      new ConfigureOptions<WhizbangCoreOptions>(options => BindCore(sp, options)));
  }

  /// <summary>Applies <c>Whizbang:SystemEvents</c> to <paramref name="options"/>.</summary>
  internal static void BindSystemEvents(IServiceProvider services, SystemEventOptions options) {
#pragma warning disable IL2026 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
    Section(services, "Whizbang:SystemEvents").Bind(options);
#pragma warning restore IL2026
  }

  /// <summary>Applies <c>Whizbang:MessageSecurity</c> to <paramref name="options"/>.</summary>
  internal static MessageSecurityOptions BindMessageSecurity(IServiceProvider services, MessageSecurityOptions options) {
#pragma warning disable IL2026 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
    Section(services, "Whizbang:MessageSecurity").Bind(options);
#pragma warning restore IL2026
    return options;
  }

  /// <summary>Applies <c>Whizbang:Lifecycle</c> to <paramref name="options"/>.</summary>
  internal static WhizbangLifecycleOptions BindLifecycle(IServiceProvider services, WhizbangLifecycleOptions options) {
#pragma warning disable IL2026 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
    Section(services, "Whizbang:Lifecycle").Bind(options);
#pragma warning restore IL2026
    return options;
  }

  /// <summary>
  /// Applies <c>Whizbang:Health</c> to <paramref name="options"/>. A policy is named rather than
  /// bound (<see cref="HealthPolicy"/> is a mapping, not a settings bag): <c>Lenient</c> or
  /// <c>Strict</c>, for the default and per component.
  /// </summary>
  internal static WhizbangHealthOptions BindHealth(IServiceProvider services, WhizbangHealthOptions options) {
    var section = new HealthConfigurationSection();
#pragma warning disable IL2026 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
    Section(services, "Whizbang:Health").Bind(section);
#pragma warning restore IL2026
    if (section.Default is { } defaultPolicy) {
      options.Default = _healthPolicy(defaultPolicy, "Whizbang:Health:Default");
    }
    if (section.SourceTimeout is { } timeout) {
      options.SourceTimeout = timeout;
    }
    foreach (var (component, policy) in section.Components) {
      options.Components[component] = _healthPolicy(policy, $"Whizbang:Health:Components:{component}");
    }
    return options;
  }

  private static HealthPolicy _healthPolicy(string name, string key) {
    if (string.Equals(name, "Lenient", StringComparison.OrdinalIgnoreCase)) {
      return HealthPolicy.Lenient;
    }
    if (string.Equals(name, "Strict", StringComparison.OrdinalIgnoreCase)) {
      return HealthPolicy.Strict;
    }
    throw new InvalidOperationException(
      $"{key} is '{name}', which is not a health policy. Use Lenient or Strict.");
  }

  /// <summary>
  /// Applies <c>Whizbang:Core</c> and <c>Whizbang:ShowBanner</c> to <paramref name="options"/>. Only
  /// the keys read at run time bind; the ones read while services are being registered
  /// (<see cref="WhizbangCoreOptions.AutoRegisterAspNetHosting"/>,
  /// <see cref="WhizbangCoreOptions.ValidateRegistrations"/>, <see cref="WhizbangCoreOptions.Services"/>)
  /// are decided before configuration can be read, so binding them would publish a key that does nothing.
  /// </summary>
  internal static WhizbangCoreOptions BindCore(IServiceProvider services, WhizbangCoreOptions options) {
    var section = new CoreConfigurationSection();
#pragma warning disable IL2026 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
    Section(services, "Whizbang:Core").Bind(section);
#pragma warning restore IL2026
    section.ApplyTo(options);

    // One banner key, the one the startup log has always honored. Whizbang:Core:ShowBanner is not
    // a second spelling of it.
    var banner = services.GetService<IConfiguration>()?["Whizbang:ShowBanner"];
    if (bool.TryParse(banner, out var showBanner)) {
      options.ShowBanner = showBanner;
    }
    return options;
  }
}

/// <summary>
/// The run-time keys of <c>Whizbang:Core</c>. Each is nullable so an absent key leaves the value code
/// set alone, rather than resetting it to a default.
/// </summary>
internal sealed class CoreConfigurationSection {
  public TimeSpan? ShutdownDeregistrationTimeout { get; set; }
  public bool? EnableTagProcessing { get; set; }
  public TagProcessingMode? TagProcessingMode { get; set; }
  public Lenses.QueryScope? DefaultQueryScope { get; set; }
  public EmptyStreamIdPolicy? EmptyStreamIdPolicy { get; set; }
  public long? MaxMessagePayloadBytes { get; set; }
  public double? MessagePayloadWarningRatio { get; set; }

  public void ApplyTo(WhizbangCoreOptions options) {
    options.ShutdownDeregistrationTimeout = ShutdownDeregistrationTimeout ?? options.ShutdownDeregistrationTimeout;
    options.EnableTagProcessing = EnableTagProcessing ?? options.EnableTagProcessing;
    options.TagProcessingMode = TagProcessingMode ?? options.TagProcessingMode;
    options.DefaultQueryScope = DefaultQueryScope ?? options.DefaultQueryScope;
    options.EmptyStreamIdPolicy = EmptyStreamIdPolicy ?? options.EmptyStreamIdPolicy;
    options.MaxMessagePayloadBytes = MaxMessagePayloadBytes ?? options.MaxMessagePayloadBytes;
    options.MessagePayloadWarningRatio = MessagePayloadWarningRatio ?? options.MessagePayloadWarningRatio;
  }
}

/// <summary>The keys of <c>Whizbang:Health</c>, with policies by name.</summary>
internal sealed class HealthConfigurationSection {
  public string? Default { get; set; }
  public TimeSpan? SourceTimeout { get; set; }
  public Dictionary<string, string> Components { get; } = new(StringComparer.Ordinal);
}
