// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>What one misattributed-origin correction cycle did.</summary>
/// <param name="Batches">The batches it applied.</param>
/// <param name="Corrected">Stored events it re-attributed to their owner.</param>
/// <param name="Status"><c>caught-up</c>, <c>budget</c> (the cycle's batch budget ran out first), or
/// <c>skipped (...)</c>.</param>
/// <docs>resilience/stream-integrity#an-origin-that-does-not-own-the-type</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MisattributedOriginCorrectionSqlTests.cs:Step_CorrectsInBoundedBatches_AndResumesFromItsCursorAsync</tests>
public readonly record struct MisattributedOriginCorrectionResult(int Batches, long Corrected, string Status);

/// <summary>
/// The maintenance step that re-attributes stored events recorded under an origin that does not own their type (#2116).
/// A redelivery bundle names the service that answered it as every child's origin, so a service that answered with events
/// it had only received made consumers store another service's events under its own name. The owner's lane then counts
/// short and the answerer's long, and repair can never heal the bucket: the owner's redelivery is skipped as a duplicate.
/// </summary>
/// <remarks>
/// <para>
/// The step walks the event store by commit sequence (<c>wh_integrity_origin_correction_window</c>, migration 346), a batch
/// of <see cref="StreamIntegrityOptions.ReattributionBatchSize"/> rows at a time and each batch its own transaction. For the
/// types a batch holds under an origin it asks this service's ownership map (<see cref="IntegrityDomainOwnership"/>, built
/// from the domains other services announce on their checkpoints) for each type's owner, through the same rule as the
/// receive path (<see cref="MisattributedOrigin"/>). The SQL (<c>wh_integrity_correct_origins</c>) gives every row
/// recorded under another origin its owner, with no origin sequence (the owner's next redelivery fills it), moves its
/// digest through the shared fold, refolds the wrong origin's sealed epochs and re-arms both lanes' ledger buckets. It
/// never deletes an event. Each correction is logged at Warning per type, wrong origin and owner, and counted on
/// <see cref="StreamIntegrityMetrics.MisattributedOriginsCorrected"/>.
/// </para>
/// <para>
/// It runs in the maintenance cycle, so it is idle-gated and runs only on the maintainer turn, at most
/// <see cref="StreamIntegrityOptions.MaxReattributionBatchesPerPass"/> batches per cycle. The cursor is durable and moves
/// by compare-and-swap; the window holds the cursor row for its batch's transaction, so a second instance waits instead
/// of repeating the batch, and the walk resumes across restarts. A changed
/// ownership map rewalks from the start, so history reaches an owner learned later. A store with no known owner costs
/// one read of the claims table per cycle. <see cref="StreamIntegrityOptions.ReattributionEnabled"/> set to
/// <see langword="false"/> turns it off together with the re-attribution of pre-origin history.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity#an-origin-that-does-not-own-the-type</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MisattributedOriginCorrectionSqlTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema prefix is formatted in, built by SqlText.Identifier; every value is a bound parameter.")]
public sealed partial class MisattributedOriginCorrectionStep : IMaintenanceStep {
  private const string CAUGHT_UP = "caught-up";
  private const string BUDGET = "budget";

  private readonly ILogger _logger;

  /// <summary>Creates the step.</summary>
  /// <param name="logger">Logs each correction at Warning.</param>
  public MisattributedOriginCorrectionStep(ILogger logger) {
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
  }

  /// <summary>Builds the step from a host's services, logging to the host's logger when it has one.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MisattributedOriginCorrectionSqlTests.cs:FromServices_AndItsArguments_AreCheckedAsync</tests>
  public static MisattributedOriginCorrectionStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new MisattributedOriginCorrectionStep(
      services.GetService<ILogger<MisattributedOriginCorrectionStep>>() ?? (ILogger)NullLogger.Instance);
  }

  /// <inheritdoc />
  public string Name => "misattributed-origin-correction";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
    await CorrectAsync(services, cancellationToken).ConfigureAwait(false);

  /// <summary>Runs one cycle and says what it did.</summary>
  /// <param name="services">The maintenance cycle's services; the database is reached through
  /// <see cref="PostgresArchiveConnection"/>, bounds and the switch come from <see cref="StreamIntegrityOptions"/>, and this
  /// service's own owned domains from <see cref="RoutingOptions"/>.</param>
  /// <param name="cancellationToken">Cancellation; set at shutdown.</param>
  /// <returns>What the cycle did.</returns>
  public async Task<MisattributedOriginCorrectionResult> CorrectAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var options = services.GetService<IOptions<StreamIntegrityOptions>>()?.Value ?? new StreamIntegrityOptions();
    if (!options.ReattributionEnabled) {
      const string OFF = "skipped (Whizbang:StreamIntegrity:ReattributionEnabled=false)";
      LogSkipped(_logger, OFF);
      return new MisattributedOriginCorrectionResult(0, 0, OFF);
    }
    var database = services.GetRequiredService<PostgresArchiveConnection>();
    var prefix = database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".";
    var ownership = new IntegrityDomainOwnership(await _claimsAsync(database, prefix, cancellationToken).ConfigureAwait(false));
    if (ownership.Owners.Count == 0) {
      const string NO_OWNERS = "skipped (no service has announced an owned domain)";
      LogSkipped(_logger, NO_OWNERS);
      return new MisattributedOriginCorrectionResult(0, 0, NO_OWNERS);
    }
    var ownedDomains = services.GetService<IOptions<RoutingOptions>>()?.Value.OwnedDomains ?? new HashSet<string>();
    var walk = new Walk(prefix, ownership.Fingerprint([.. ownedDomains]), Math.Max(1, options.ReattributionBatchSize), ownership, ownedDomains,
      services.GetService<StreamIntegrityMetrics>());
    var batches = 0;
    long corrected = 0;
    while (batches < options.MaxReattributionBatchesPerPass) {
      var (status, count) = await _batchAsync(database, walk, cancellationToken).ConfigureAwait(false);
      if (status != BUDGET) {
        return new MisattributedOriginCorrectionResult(batches, corrected, status);
      }
      batches++;
      corrected += count;
    }
    return new MisattributedOriginCorrectionResult(batches, corrected, BUDGET);
  }

  private sealed record Walk(
    string Prefix, string Fingerprint, int BatchSize, IntegrityDomainOwnership Ownership, IReadOnlyCollection<string> OwnedDomains,
    StreamIntegrityMetrics? Metrics);

  private static async Task<List<IntegrityDomainClaim>> _claimsAsync(
      PostgresArchiveConnection database, string prefix, CancellationToken cancellationToken) {
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"SELECT domain, owner_service_id, owner_service_name FROM {prefix}wh_integrity_domain_claims", connection);
    var claims = new List<IntegrityDomainClaim>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      claims.Add(new IntegrityDomainClaim { Domain = reader.GetString(0), OwnerServiceId = reader.GetGuid(1), OwnerServiceName = reader.GetString(2) });
    }
    return claims;
  }

  // One batch, one transaction: the window, each of its types' owners from the map, the correction. Returns BUDGET when it
  // applied a batch and the walk may continue.
  private async Task<(string Status, long Corrected)> _batchAsync(
      PostgresArchiveConnection database, Walk walk, CancellationToken cancellationToken) {
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    long after, through;
    string[] types;
    await using (var window = new NpgsqlCommand(
      $"SELECT after_commit_sequence, through_commit_sequence, event_types FROM {walk.Prefix}wh_integrity_origin_correction_window($1, $2)",
      connection, transaction)) {
      window.Parameters.AddWithValue(walk.Fingerprint);
      window.Parameters.AddWithValue(walk.BatchSize);
      await using var reader = await window.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
      (after, through, types) = (reader.GetInt64(0), reader.GetInt64(1), reader.GetFieldValue<string[]>(2));
    }
    if (through <= after) {
      await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
      return (CAUGHT_UP, 0);
    }
    var targets = types
      .Select(t => (Type: t, Owner: MisattributedOrigin.TrueOwner(t, Guid.Empty, walk.Ownership, walk.OwnedDomains)))
      .Where(t => t.Owner is not null)
      .ToList();
    await using var correct = new NpgsqlCommand(
      $"SELECT advanced, event_type, from_origin, to_owner, corrected_count FROM {walk.Prefix}wh_integrity_correct_origins($1, $2, $3, $4)",
      connection, transaction);
    correct.Parameters.AddWithValue(after);
    correct.Parameters.AddWithValue(through);
    correct.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = targets.Select(t => t.Type).ToArray() });
    correct.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = targets.Select(t => t.Owner!.Value).ToArray() });
    long corrected = 0;
    await using (var reader = await correct.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)) {
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        // The window holds the cursor for this transaction, so the correction always advances it (column 0).
        if (reader.IsDBNull(1)) {
          continue;
        }
        var count = reader.GetInt32(4);
        corrected += count;
        walk.Metrics?.MisattributedOriginsCorrected.Add(count);
        LogCorrected(_logger, count, reader.GetString(1), reader.GetGuid(2), reader.GetGuid(3), after, through);
      }
    }
    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    return (BUDGET, corrected);
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "A generated structured log message: each value is its own searchable property (count, type, wrong origin, owner, batch range).")]
  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Re-attributed {Count} stored event(s) of type {EventType} from origin {FromOrigin}, which does not own the type, to " +
              "its owner {Owner} (commit sequences {After} to {Through}); their origin sequence is cleared until the owner's next " +
              "redelivery fills it (#2116)")]
  static partial void LogCorrected(ILogger logger, int count, string eventType, Guid fromOrigin, Guid owner, long after, long through);

  [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Misattributed-origin correction {Status}")]
  static partial void LogSkipped(ILogger logger, string status);
}
