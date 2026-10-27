// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Capabilities;

namespace Whizbang.Core.Workers;

/// <summary>Stub.</summary>
public sealed class OnlineTableRewriteStep(
    IEnumerable<ITableRewriter> rewriters,
    IOptions<MaintenanceWorkerOptions> options,
    ICapabilityRegistry? capabilities,
    ILogger<OnlineTableRewriteStep> logger) : IMaintenanceStep {
  /// <inheritdoc />
  public string Name => "";

  /// <inheritdoc />
  public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    _ = (rewriters, options, capabilities, logger);
    return Task.CompletedTask;
  }
}
