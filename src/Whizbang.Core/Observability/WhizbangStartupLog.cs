using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Configuration;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Observability;

/// <summary>
/// Hosted service that prints the Whizbang banner and logs the version on startup.
/// Banner can be suppressed via <see cref="WhizbangCoreOptions.ShowBanner"/> in code or
/// <c>Whizbang:ShowBanner = false</c> in configuration, which binds over it (#1014).
/// The version log line always fires regardless of banner setting.
/// Logger category: Whizbang.Startup
/// </summary>
/// <docs>operations/observability/logging#startup</docs>
public sealed partial class WhizbangStartupLogger(
  ILoggerFactory loggerFactory,
  IServiceInstanceProvider instanceProvider,
  WhizbangCoreOptions coreOptions) : IHostedService {

  private readonly ILogger _logger = loggerFactory.CreateLogger("Whizbang.Startup");

  /// <inheritdoc/>
  public Task StartAsync(CancellationToken cancellationToken) {
    var serviceName = instanceProvider.ServiceName;
    const string whizbangVersion = WhizbangVersionInfo.Version;

    // One banner switch: Whizbang:ShowBanner is applied to WhizbangCoreOptions.ShowBanner when the
    // options resolve, so configuration still overrides code without a second read here.
    var showBanner = coreOptions.ShowBanner;

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
