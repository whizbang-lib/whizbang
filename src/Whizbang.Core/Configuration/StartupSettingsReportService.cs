// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    if (!options.Value.LogSettings) {
      LogReportOff(logger, LOG_SETTINGS_KEY);
      return Task.CompletedTask;
    }
    var report = StartupSettingsReport.Build(
      configuration as IConfigurationRoot,
      KnownConfigurationKeys.Library,
      KnownConfigurationKeys.Sensitive,
      derivedSources.SelectMany(static source => source.DerivedSettings));
    foreach (var section in report) {
      LogSection(logger, section.Name, section);
    }
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  [LoggerMessage(Level = LogLevel.Information, Message = "Settings {Section}: {Settings}")]
  static partial void LogSection(ILogger logger, string section, SettingsSection settings);

  [LoggerMessage(Level = LogLevel.Information, Message = "The startup settings report is off ({Key} = false)")]
  static partial void LogReportOff(ILogger logger, string key);
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
  internal static IServiceCollection AddWhizbangStartupSettingsReport(this IServiceCollection services) {
    services.AddOptions<WhizbangStartupOptions>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<WhizbangStartupOptions>, ConfigureWhizbangStartupOptions>(
      sp => new ConfigureWhizbangStartupOptions(sp.GetService<IConfiguration>())));
    services.TryAddSingleton(sp => new StartupSettingsReportService(
      sp.GetRequiredService<IOptions<WhizbangStartupOptions>>(),
      sp.GetService<IConfiguration>(),
      sp.GetServices<IDerivedSettingSource>(),
      sp.GetRequiredService<ILogger<StartupSettingsReportService>>()));
    return services.AddHostedServiceAtStage(StartupStage.Running, static sp => sp.GetRequiredService<StartupSettingsReportService>());
  }
}
