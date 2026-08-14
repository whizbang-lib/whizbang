using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Configuration;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Applies <see cref="PerspectiveRowRetentionOptions"/> to <see cref="PerspectiveTtlRegistry"/>
/// at host startup — the operator rung of the row-retention override ladder. Runs before the
/// worker pipeline processes anything (hosted services start in registration order and this is
/// registered alongside the workers); inert on defaults (enabled, no overrides).
/// </summary>
/// <docs>fundamentals/perspectives/row-retention</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowRetentionConfiguratorTests.cs</tests>
public sealed partial class PerspectiveRowRetentionConfigurator(
    IOptions<PerspectiveRowRetentionOptions> options,
    ILogger<PerspectiveRowRetentionConfigurator> logger) : IHostedService {
  private readonly PerspectiveRowRetentionOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<PerspectiveRowRetentionConfigurator> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

  /// <inheritdoc/>
  public Task StartAsync(CancellationToken cancellationToken) {
    PerspectiveTtlRegistry.ApplyRuntimeConfiguration(_options.Enabled, _options.Overrides);
    if (!_options.Enabled) {
      LogRetentionDisabled(_logger);
    } else if (_options.Overrides.Count > 0) {
      LogOverridesApplied(_logger, _options.Overrides.Count);
    }
    return Task.CompletedTask;
  }

  /// <inheritdoc/>
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Perspective row retention is DISABLED by configuration — rows will not be stamped, hidden, or probed for resurrection")]
  private static partial void LogRetentionDisabled(ILogger logger);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information,
    Message = "Perspective row retention: {OverrideCount} runtime TTL override(s) applied")]
  private static partial void LogOverridesApplied(ILogger logger, int overrideCount);
}
