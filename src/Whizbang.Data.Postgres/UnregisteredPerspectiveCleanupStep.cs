// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>What one unregistered-perspective cleanup cycle did (#2601).</summary>
/// <param name="Batches">The batches it ran.</param>
/// <param name="CursorsRemoved">Cursors it removed.</param>
/// <param name="RowsRemoved">Queued rows it removed with them.</param>
/// <param name="SnapshotsRemoved">Snapshots it removed with them.</param>
/// <param name="Perspectives">The perspectives it removed them for, each once, in ordinal order.</param>
/// <param name="Status"><c>ok</c>, <c>budget</c> (the cycle's batches were spent) or <c>skipped (...)</c>.</param>
/// <docs>fundamentals/perspectives/rewind#perspectives-no-longer-registered</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnregisteredPerspectiveCleanupStepTests.cs</tests>
public readonly record struct UnregisteredPerspectiveCleanupResult(
  int Batches, int CursorsRemoved, int RowsRemoved, int SnapshotsRemoved, IReadOnlyList<string> Perspectives, string Status);

/// <summary>The maintenance step that removes what is left of a perspective no service runs any more (#2601).</summary>
/// <docs>fundamentals/perspectives/rewind#perspectives-no-longer-registered</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnregisteredPerspectiveCleanupStepTests.cs</tests>
public sealed partial class UnregisteredPerspectiveCleanupStep : IMaintenanceStep {
  private readonly ILogger _logger;

  /// <summary>Creates the step.</summary>
  /// <param name="logger">Logs what each cycle removed.</param>
  public UnregisteredPerspectiveCleanupStep(ILogger<UnregisteredPerspectiveCleanupStep> logger) : this((ILogger)logger) { }

  /// <summary>Creates the step over any logger, for tests.</summary>
  internal UnregisteredPerspectiveCleanupStep(ILogger logger) {
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
  }

  /// <summary>Builds the step from a host's services.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  public static UnregisteredPerspectiveCleanupStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new((ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
  }

  /// <inheritdoc />
  public string Name => string.Empty;

  /// <inheritdoc />
  public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) => CleanAsync(services, cancellationToken);

  /// <summary>Runs one cycle and says what it did.</summary>
  /// <param name="services">The maintenance cycle's services.</param>
  /// <param name="cancellationToken">Cancellation; set at shutdown.</param>
  /// <returns>What the cycle did.</returns>
  public Task<UnregisteredPerspectiveCleanupResult> CleanAsync(IServiceProvider services, CancellationToken cancellationToken) =>
    throw new NotSupportedException($"not yet: {services} {cancellationToken.IsCancellationRequested} {_logger}");
}
