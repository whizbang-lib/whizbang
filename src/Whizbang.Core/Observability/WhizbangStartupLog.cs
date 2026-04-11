using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Diagnostics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Hosted service that logs Whizbang version and configuration on startup.
/// Logger category: Whizbang.Startup — configure independently in appsettings.
/// Runs before PerspectiveWorker (registered earlier in the pipeline).
/// </summary>
/// <docs>operations/observability/logging#startup</docs>
public sealed partial class WhizbangStartupLogger(
  ILoggerFactory loggerFactory) : IHostedService {

  private readonly ILogger _logger = loggerFactory.CreateLogger("Whizbang.Startup");

  /// <inheritdoc/>
  public Task StartAsync(CancellationToken cancellationToken) {
    LogWhizbangVersion(_logger, WhizbangVersionInfo.Version);
    return Task.CompletedTask;
  }

  /// <inheritdoc/>
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Information,
    Message = "Whizbang v{Version} initialized")]
  static partial void LogWhizbangVersion(ILogger logger, string version);
}
