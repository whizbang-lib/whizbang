using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Logs the loaded Whizbang assembly version at host startup so operators can
/// verify which build is actually running in a pod — vs which version the source
/// tree references — without needing to exec into the container.
/// </summary>
/// <remarks>
/// Reports <see cref="AssemblyInformationalVersionAttribute"/> (NuGet-style
/// <c>0.488.1-alpha.4+sha</c>) when present, falling back to
/// <see cref="AssemblyFileVersionAttribute"/> then plain
/// <see cref="Assembly.GetName"/>.Version. The log fires once at
/// <see cref="IHostedService.StartAsync"/> and the service then sits idle —
/// no background work, no per-message overhead. Slot-3 debug pattern:
/// `kubectl logs deploy/x | grep "Whizbang"` to confirm deployed version.
/// </remarks>
/// <docs>operations/diagnostics</docs>
public sealed partial class WhizbangVersionLogger(ILogger<WhizbangVersionLogger> logger) : IHostedService {
  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken) {
    var assembly = typeof(WhizbangVersionLogger).Assembly;
    var name = assembly.GetName().Name ?? "Whizbang.Core";
    var version =
      assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
      ?? assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
      ?? assembly.GetName().Version?.ToString()
      ?? "unknown";
    LogStartup(logger, name, version);
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Information,
    Message = "Whizbang loaded: {AssemblyName} version {AssemblyVersion}")]
  private static partial void LogStartup(ILogger logger, string assemblyName, string assemblyVersion);
}
