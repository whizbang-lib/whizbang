using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Configuration;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Observability;

/// <summary>
/// Hosted service that prints the Whizbang banner and logs the version on startup.
/// Banner can be suppressed via <c>Whizbang:ShowBanner = false</c> in appsettings
/// or <see cref="WhizbangCoreOptions.ShowBanner"/> in code.
/// The version log line always fires regardless of banner setting.
/// Logger category: Whizbang.Startup
/// </summary>
/// <docs>operations/observability/logging#startup</docs>
public sealed partial class WhizbangStartupLogger(
  ILoggerFactory loggerFactory,
  IServiceInstanceProvider instanceProvider,
  WhizbangCoreOptions coreOptions,
  IConfiguration? configuration = null) : IHostedService {

  private readonly ILogger _logger = loggerFactory.CreateLogger("Whizbang.Startup");

  /// <inheritdoc/>
  public Task StartAsync(CancellationToken cancellationToken) {
    var serviceName = instanceProvider.ServiceName;
    var whizbangVersion = WhizbangVersionInfo.Version;

    // Config file overrides code option (Whizbang:ShowBanner in appsettings.json)
    var showBanner = coreOptions.ShowBanner;
    var configValue = configuration?["Whizbang:ShowBanner"];
    if (configValue is not null && bool.TryParse(configValue, out var parsed)) {
      showBanner = parsed;
    }

    // Print ASCII art banner (suppressed by ShowBanner = false)
    WhizbangBanner.PrintHeader(serviceName, whizbangVersion: whizbangVersion, enabled: showBanner);

    // Always log version via ILogger (respects log level config)
    LogWhizbangVersion(_logger, whizbangVersion, serviceName);

    return Task.CompletedTask;
  }

  /// <inheritdoc/>
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Information,
    Message = "Whizbang v{Version} initialized ({ServiceName})")]
  static partial void LogWhizbangVersion(ILogger logger, string version, string serviceName);
}
