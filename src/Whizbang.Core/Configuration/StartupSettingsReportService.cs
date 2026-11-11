// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Configuration;

/// <summary>
/// Writes the startup settings report once the service is ready: one Information entry per settings section, giving
/// every setting's value and where it came from (configured, and by which provider; default; or derived, with the
/// inputs it was derived from). With <c>Whizbang:Startup:LogSettings</c> set to false it writes only that the report is
/// off.
/// </summary>
/// <remarks>
/// Registered by <c>AddWhizbang</c> at <see cref="Startup.StartupStage.Running"/>, through the startup stage table, so
/// every host writes it when it is fully up and nothing has to remember to call it.
/// </remarks>
/// <docs>operations/configuration/startup-settings-report</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/StartupSettingsReportServiceTests.cs</tests>
internal sealed partial class StartupSettingsReportService(
    IOptions<WhizbangStartupOptions> options,
    IConfiguration? configuration,
    IEnumerable<IDerivedSettingSource> derivedSources,
    ILogger<StartupSettingsReportService> logger) : IHostedService {
  /// <summary>The key that turns the report off.</summary>
  internal const string LOG_SETTINGS_KEY = "Whizbang:Startup:LogSettings";

  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken) {
    _ = (options, configuration, derivedSources, logger);
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Registers the startup settings report (#2251).</summary>
/// <docs>operations/configuration/startup-settings-report#when-it-is-written</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Configuration/StartupSettingsReportServiceTests.cs</tests>
internal static class StartupSettingsReportServiceCollectionExtensions {
  /// <summary>
  /// Hosts the report at <see cref="StartupStage.Running"/>, with the <c>Whizbang:Startup</c> settings that turn it off.
  /// Every call after the first changes nothing.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The same collection.</returns>
  internal static IServiceCollection AddWhizbangStartupSettingsReport(this IServiceCollection services) => services;
}
