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
public sealed partial class UnsequencedDigestCompactionStep(
    Type dbContextType,
    ILogger<UnsequencedDigestCompactionStep>? logger = null) : IMaintenanceStep {

  /// <summary>How many changes one fold takes at most.</summary>
  public const int BATCH_SIZE = 10_000;

  /// <summary>How many folds one run issues at most.</summary>
  public const int MAX_BATCHES_PER_RUN = 10;

  private readonly Type _dbContextType = dbContextType ?? throw new ArgumentNullException(nameof(dbContextType));
  private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

  /// <inheritdoc />
  public string Name => "unsequenced-digest-compaction";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    if (_dbContextType != typeof(void)) {
      return;
    }
    var context = (DbContext)services.GetRequiredService(_dbContextType);
    var schema = context.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema();
    var function = PgIdentifier.QualifyPrefix(string.IsNullOrWhiteSpace(schema) ? "public" : schema)
      + "wh_compact_unsequenced_digest_changes";

    await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    try {
      var connection = (NpgsqlConnection)context.Database.GetDbConnection();
      var folded = 0L;
      for (var batch = 0; batch < MAX_BATCHES_PER_RUN; batch++) {
        await using var cmd = connection.CreateCoordinatorCommand();
#pragma warning disable S2077 // the function name is the context's own schema, quoted by PgIdentifier
        cmd.CommandText = $"SELECT {function}(@limit)";
#pragma warning restore S2077
        cmd.Parameters.AddWithValue("limit", BATCH_SIZE);
        var changes = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
          System.Globalization.CultureInfo.InvariantCulture);
        folded += changes;
        if (changes < BATCH_SIZE) {
          break;
        }
      }
      if (folded > 0) {
        LogCompacted(_logger, folded);
      }
    } finally {
      await context.Database.CloseConnectionAsync().ConfigureAwait(false);
    }
  }

  [LoggerMessage(Level = LogLevel.Debug, Message = "Folded {Count} held-without-sequence digest change(s) into their totals")]
  private static partial void LogCompacted(ILogger logger, long count);
}
