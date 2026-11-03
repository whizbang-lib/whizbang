// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whizbang.Core.Versioning;

/// <summary>Registers this process's <see cref="IApplicationVersionProvider"/> (#2027).</summary>
/// <docs>operations/startup/startup-status#the-instance-row</docs>
/// <tests>tests/Whizbang.Core.Tests/Versioning/ApplicationVersionRegistrationTests.cs</tests>
public static class ApplicationVersionRegistration {
  /// <summary>
  /// Registers the application version resolved from configuration (<see cref="ApplicationVersion.VERSION_KEY"/>,
  /// <see cref="ApplicationVersion.DEPLOY_NUMBER_KEY"/>) and the entry assembly's informational version, unless a
  /// provider is already registered. Idempotent.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The same collection.</returns>
  public static IServiceCollection TryAddWhizbangApplicationVersion(IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);
    services.TryAddSingleton<IApplicationVersionProvider>(static sp => new ApplicationVersionProvider(
      ApplicationVersion.FromConfiguration(sp.GetService<IConfiguration>(), EntryInformationalVersion(Assembly.GetEntryAssembly()))));
    return services;
  }

  /// <summary>The informational version an entry assembly declares, or null.</summary>
  /// <param name="entry">The entry assembly, or null when the process has none (a native host, some test runners).</param>
  /// <returns>Its informational version, or null.</returns>
  internal static string? EntryInformationalVersion(Assembly? entry) =>
    entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
}
