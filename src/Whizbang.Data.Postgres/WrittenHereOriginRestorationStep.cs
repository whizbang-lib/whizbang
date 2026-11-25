// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>What one cycle of the written-here origin restoration did.</summary>
/// <param name="Batches">The batches it applied.</param>
/// <param name="RestoredReactions">Events returned to the local lane that were stored with no origin position.</param>
/// <param name="RestoredReturns">Events returned to the local lane that were stored with an origin position.</param>
/// <param name="Ambiguous">Rows left because the evidence did not prove them written here.</param>
/// <param name="Status"><c>caught-up</c>, <c>budget</c>, or <c>skipped (...)</c>.</param>
public readonly record struct WrittenHereOriginRestorationResult(
  int Batches, long RestoredReactions, long RestoredReturns, long Ambiguous, string Status);

/// <summary>The maintenance step that returns events written here, stored under another origin, to the local lane (#2573).</summary>
public sealed partial class WrittenHereOriginRestorationStep : IMaintenanceStep {
  private readonly ILogger _logger;

  /// <summary>Creates the step.</summary>
  /// <param name="logger">Logs each restoration.</param>
  public WrittenHereOriginRestorationStep(ILogger logger) {
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
  }

  /// <summary>Builds the step from a host's services.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  public static WrittenHereOriginRestorationStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new WrittenHereOriginRestorationStep(
      services.GetService<ILogger<WrittenHereOriginRestorationStep>>() ?? (ILogger)NullLogger.Instance);
  }

  /// <inheritdoc />
  public string Name => "written-here-origin-restoration";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
    await RestoreAsync(services, cancellationToken).ConfigureAwait(false);

  /// <summary>Runs one cycle and says what it did.</summary>
  /// <param name="services">The maintenance cycle's services.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>What the cycle did.</returns>
  public Task<WrittenHereOriginRestorationResult> RestoreAsync(IServiceProvider services, CancellationToken cancellationToken) {
    _ = (_logger, services, cancellationToken);
    return Task.FromResult(new WrittenHereOriginRestorationResult(0, 0, 0, 0, "not implemented"));
  }
}
