// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Whizbang.Core;

/// <summary>
/// What startup does when two different assemblies register the same item
/// (<c>Whizbang:Startup:RegistrationCollisions</c>).
/// </summary>
/// <docs>extending/extensibility/replaceable-services#registration-collisions</docs>
/// <tests>tests/Whizbang.Core.Tests/RegistrationCollisionStartupValidatorTests.cs</tests>
public enum RegistrationCollisionMode {
  /// <summary>One Warning per collision, naming the item and both assemblies; the latest registration is used. The default.</summary>
  Warn = 0,

  /// <summary>The same warnings, then startup fails with an error naming every collision.</summary>
  Fail = 1,
}

/// <summary>Startup settings, bound from <c>Whizbang:Startup</c>.</summary>
/// <docs>extending/extensibility/replaceable-services#registration-collisions</docs>
/// <tests>tests/Whizbang.Core.Tests/RegistrationCollisionStartupValidatorTests.cs</tests>
public sealed class WhizbangStartupOptions {
  /// <summary>
  /// What to do when two different assemblies register the same item (<c>Whizbang:Startup:RegistrationCollisions</c>).
  /// Default <see cref="RegistrationCollisionMode.Warn"/>.
  /// </summary>
  public RegistrationCollisionMode RegistrationCollisions { get; set; } = RegistrationCollisionMode.Warn;
}

/// <summary>
/// Binds <see cref="WhizbangStartupOptions"/> from <c>Whizbang:Startup</c>. The section is named literally at the
/// call so the configuration binder source generator compiles the bind to typed assignments. A host without
/// configuration keeps the code values.
/// </summary>
/// <docs>extending/extensibility/replaceable-services#registration-collisions</docs>
/// <tests>tests/Whizbang.Core.Tests/RegistrationCollisionStartupValidatorTests.cs</tests>
internal sealed class ConfigureWhizbangStartupOptions(IConfiguration? configuration) : IConfigureOptions<WhizbangStartupOptions> {
  public void Configure(WhizbangStartupOptions options) {
    if (configuration is null) {
      return;
    }
#pragma warning disable IL2026, IL3050 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
    configuration.GetSection("Whizbang:Startup").Bind(options);
#pragma warning restore IL2026, IL3050
  }
}

/// <summary>
/// Reports, once when the host starts, every item two different assemblies registered.
/// </summary>
/// <remarks>
/// A hosted service rather than a startup-pipeline step, as the unknown-configuration-key check is:
/// <see cref="RegistrationCollisionMode.Fail"/> must be able to stop the host, and a throw from <c>StartAsync</c>
/// does. Every per-assembly registration adds it, so a host gets the check from the registrations themselves.
/// </remarks>
/// <param name="sources">Every per-assembly registration's collision source.</param>
/// <param name="options">The bound <c>Whizbang:Startup</c> settings.</param>
/// <param name="logger">Where the warnings go.</param>
/// <docs>extending/extensibility/replaceable-services#registration-collisions</docs>
/// <tests>tests/Whizbang.Core.Tests/RegistrationCollisionStartupValidatorTests.cs</tests>
internal sealed partial class RegistrationCollisionStartupValidator(
    IEnumerable<IRegistrationCollisionSource> sources,
    IOptions<WhizbangStartupOptions> options,
    ILogger<RegistrationCollisionStartupValidator> logger) : IHostedService {
  /// <summary>The key that selects the <see cref="RegistrationCollisionMode"/>.</summary>
  internal const string MODE_KEY = "Whizbang:Startup:RegistrationCollisions";

  /// <inheritdoc />
  /// <exception cref="InvalidOperationException">The mode is Fail and an item is registered by two assemblies.</exception>
  public Task StartAsync(CancellationToken cancellationToken) {
    var collisions = sources.SelectMany(source => source.GetCollisions()).ToList();
    foreach (var collision in collisions) {
      LogCollision(logger, collision.Registration, collision.Item, collision.HiddenAssembly, collision.WinningAssembly);
    }
    if (options.Value.RegistrationCollisions == RegistrationCollisionMode.Fail && collisions.Count > 0) {
      throw new InvalidOperationException(
        "Two assemblies register the same item: " + string.Join("; ", collisions)
        + $". Remove one of each pair, or set {MODE_KEY} to Warn to start with the latest registration of each.");
    }
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "{Registration} '{Item}' is registered by both {HiddenAssembly} and {WinningAssembly}. {WinningAssembly} was registered latest and is used; {HiddenAssembly}'s registration is hidden")]
  private static partial void LogCollision(ILogger logger, string registration, string item, string hiddenAssembly, string winningAssembly);
}

/// <summary>
/// Adds a per-assembly registration's collision source, and with it the startup check that reports collisions.
/// </summary>
/// <docs>extending/extensibility/replaceable-services#registration-collisions</docs>
/// <tests>tests/Whizbang.Core.Tests/RegistrationCollisionStartupValidatorTests.cs</tests>
public static class RegistrationCollisionServiceCollectionExtensions {
  /// <summary>
  /// Adds <paramref name="source"/> to the sources the startup check reads, and the check itself once.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="source">The registration's collision source.</param>
  /// <returns>The service collection.</returns>
  /// <tests>tests/Whizbang.Core.Tests/RegistrationCollisionStartupValidatorTests.cs:ACollision_UnderWarn_IsLoggedNamingBothAssemblies_AndStartsAsync</tests>
  public static IServiceCollection AddRegistrationCollisionSource(this IServiceCollection services, IRegistrationCollisionSource source) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(source);
    services.AddSingleton(source);
    return services.AddRegistrationCollisionCheck();
  }

  /// <summary>Adds the startup check and its settings once; every call after the first changes nothing.</summary>
  internal static IServiceCollection AddRegistrationCollisionCheck(this IServiceCollection services) {
    services.AddOptions<WhizbangStartupOptions>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<WhizbangStartupOptions>, ConfigureWhizbangStartupOptions>(
      sp => new ConfigureWhizbangStartupOptions(sp.GetService<IConfiguration>())));
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RegistrationCollisionStartupValidator>(
      sp => new RegistrationCollisionStartupValidator(
        sp.GetServices<IRegistrationCollisionSource>(),
        sp.GetRequiredService<IOptions<WhizbangStartupOptions>>(),
        sp.GetRequiredService<ILogger<RegistrationCollisionStartupValidator>>())));
    return services;
  }
}
