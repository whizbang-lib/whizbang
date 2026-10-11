// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Startup;

/// <summary>
/// An elector that holds duties beyond a single call, and gives them up on a graceful stop.
/// </summary>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Startup/DutyShutdownReleaseServiceTests.cs</tests>
public interface IReleasesDutiesOnShutdown {
  /// <summary>Releases every duty this instance still holds.</summary>
  /// <param name="cancellationToken">The host's stop token.</param>
  Task ReleaseAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Releases held duties when the host stops, so a rolling deploy hands a role off at once
/// instead of waiting for its lease to lapse.
/// </summary>
/// <remarks>
/// A release that fails is logged and the rest still run. The database being unreachable at
/// shutdown leaves the lease to lapse, which is the crash path the design already bounds, so it
/// is not a reason to fail the stop. Cancellation of the stop itself propagates.
/// </remarks>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Startup/DutyShutdownReleaseServiceTests.cs</tests>
public sealed partial class DutyShutdownReleaseService : IHostedService {
  private readonly IReadOnlyList<IReleasesDutiesOnShutdown> _releasers;
  private readonly ILogger<DutyShutdownReleaseService> _logger;

  /// <summary>Creates the service over every registered releaser.</summary>
  /// <param name="releasers">The electors that hold duties.</param>
  /// <param name="logger">Logger.</param>
  public DutyShutdownReleaseService(
      IEnumerable<IReleasesDutiesOnShutdown> releasers,
      ILogger<DutyShutdownReleaseService> logger) {
    ArgumentNullException.ThrowIfNull(releasers);
    ArgumentNullException.ThrowIfNull(logger);
    _releasers = [.. releasers];
    _logger = logger;
  }

  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  /// <inheritdoc />
  public async Task StopAsync(CancellationToken cancellationToken) {
    foreach (var releaser in _releasers) {
      try {
        await releaser.ReleaseAllAsync(cancellationToken).ConfigureAwait(false);
      } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
        throw;
      } catch (Exception ex) {
        LogReleaseFailed(_logger, ex);
      }
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Releasing held duties on shutdown failed; the leases will lapse instead, and another instance takes over after the lapse bound")]
  static partial void LogReleaseFailed(ILogger logger, Exception ex);
}
