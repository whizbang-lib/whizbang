// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The maintenance step that keeps the held-without-sequence change log bounded (#2351): it folds every origin's oldest
/// pending changes into the per-type totals and deletes them.
/// </summary>
/// <remarks>
/// <para>
/// The row triggers on the event store write one change per held event that joins or leaves the set, and an integrity
/// answer folds only its own origin's changes. An origin no consumer asks about would grow the log for good, and every
/// answer for an origin that is asked about would start with its backlog. This step folds the log whoever asked.
/// </para>
/// <para>
/// Bounded per run: at most <see cref="MAX_BATCHES_PER_RUN"/> folds of <see cref="BATCH_SIZE"/> changes, stopping at the
/// first fold that finds less than a full batch. Every instance may run it: a fold skips the changes another holds
/// rather than waiting on them, so concurrent runs share the work instead of repeating it.
/// </para>
/// </remarks>
/// <param name="dbContextType">The DbContext whose schema holds the log.</param>
/// <param name="logger">Optional logger.</param>
/// <docs>resilience/stream-integrity#the-change-log-stays-bounded</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnsequencedDigestCompactionStepTests.cs</tests>
public sealed class UnsequencedDigestCompactionStep(
    Type dbContextType,
    ILogger<UnsequencedDigestCompactionStep>? logger = null) : IMaintenanceStep {

  /// <summary>How many changes one fold takes at most.</summary>
  public const int BATCH_SIZE = UnsequencedDigestCompaction.BATCH_SIZE;

  /// <summary>How many folds one run issues at most.</summary>
  public const int MAX_BATCHES_PER_RUN = UnsequencedDigestCompaction.MAX_BATCHES_PER_RUN;

  private readonly Type _dbContextType = dbContextType ?? throw new ArgumentNullException(nameof(dbContextType));
  private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

  /// <inheritdoc />
  public string Name => UnsequencedDigestCompaction.STEP_NAME;

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var context = (DbContext)services.GetRequiredService(_dbContextType);
    var schema = context.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema();

    await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    try {
      await UnsequencedDigestCompaction.RunAsync(
        (NpgsqlConnection)context.Database.GetDbConnection(), schema, BATCH_SIZE, MAX_BATCHES_PER_RUN, _logger,
        cancellationToken).ConfigureAwait(false);
    } finally {
      await context.Database.CloseConnectionAsync().ConfigureAwait(false);
    }
  }
}
