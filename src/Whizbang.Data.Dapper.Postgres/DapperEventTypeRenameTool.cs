using System.Diagnostics.CodeAnalysis;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core;
using Whizbang.Core.Data;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Postgres implementation of <see cref="IEventTypeRenameTool"/> backed by Dapper.
/// </summary>
/// <remarks>
/// <para>
/// Detects drift by comparing <c>wh_message_type_registry</c> rows (keyed on <c>pinned_id</c>)
/// against the compile-time <see cref="IMessageTypeCatalog"/>. Applies pending renames across
/// the six data tables that store CLR type names, plus the registry row itself, all in one
/// transaction.
/// </para>
/// <para>
/// Not wired into startup — consumers call <see cref="ExecuteAsync"/> explicitly from a
/// migration script or admin command.
/// </para>
/// </remarks>
/// <docs>core-concepts/pinned-identity</docs>
[SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Rename tool runs once per namespace change; structured logging cost is negligible.")]
[SuppressMessage("Performance", "CA1873:Avoid potentially expensive argument evaluation when logging", Justification = "Rename tool runs infrequently; argument evaluation cost is negligible.")]
public sealed class DapperEventTypeRenameTool : IEventTypeRenameTool {
  private readonly IMessageTypeCatalog _catalog;
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly ILogger<DapperEventTypeRenameTool>? _logger;
  private readonly List<PendingRename> _manualRenames = [];

  /// <summary>
  /// Initializes a new rename tool.
  /// </summary>
  public DapperEventTypeRenameTool(
      IMessageTypeCatalog catalog,
      IDbConnectionFactory connectionFactory,
      ILogger<DapperEventTypeRenameTool>? logger = null) {
    ArgumentNullException.ThrowIfNull(catalog);
    ArgumentNullException.ThrowIfNull(connectionFactory);
    _catalog = catalog;
    _connectionFactory = connectionFactory;
    _logger = logger;
  }

  /// <inheritdoc/>
  public async Task<IReadOnlyList<PendingRename>> DetectRenamesAsync(CancellationToken cancellationToken = default) {
    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);

    var catalogByPinnedId = _catalog.GetAll()
      .Where(e => e.PinnedId is not null)
      .ToDictionary(e => e.PinnedId!, e => e.ClrTypeName, StringComparer.Ordinal);

    if (catalogByPinnedId.Count == 0) {
      return [];
    }

    var registryRows = await connection.QueryAsync<RegistryRow>(
      new CommandDefinition(
        @"SELECT pinned_id::text AS PinnedId, clr_type_name AS ClrTypeName
          FROM wh_message_type_registry
          WHERE pinned_id IS NOT NULL",
        cancellationToken: cancellationToken));

    var pending = new List<PendingRename>();
    foreach (var row in registryRows) {
      if (row.PinnedId is null) {
        continue;
      }
      if (!catalogByPinnedId.TryGetValue(row.PinnedId, out var currentClrTypeName)) {
        continue;
      }
      if (!string.Equals(row.ClrTypeName, currentClrTypeName, StringComparison.Ordinal)) {
        pending.Add(new PendingRename(row.PinnedId, row.ClrTypeName, currentClrTypeName));
      }
    }

    return pending;
  }

  /// <inheritdoc/>
  [System.Obsolete("Superseded by the pinned-type ledger + ledger-aware registry reconcile. See IEventTypeRenameTool.")]
  public void Rename(string oldTypeName, string newTypeName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(oldTypeName);
    ArgumentException.ThrowIfNullOrWhiteSpace(newTypeName);
    _manualRenames.Add(new PendingRename(PinnedId: null, oldTypeName, newTypeName));
  }

  /// <inheritdoc/>
  [System.Obsolete("Superseded by the pinned-type ledger + ledger-aware registry reconcile. See IEventTypeRenameTool.")]
  public async Task ExecuteAsync(CancellationToken cancellationToken = default) {
    var detected = await DetectRenamesAsync(cancellationToken);
    var pending = detected.Concat(_manualRenames)
      // Dedup on OldClrTypeName: detected drift takes priority (has pinned_id).
      .GroupBy(r => r.OldClrTypeName, StringComparer.Ordinal)
      .Select(g => g.First())
      .ToList();

    if (pending.Count == 0) {
      _logger?.LogInformation("No pending renames — registry and data tables are in sync.");
      return;
    }

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
    var npgsqlConnection = (NpgsqlConnection)connection;
    await using var transaction = await npgsqlConnection.BeginTransactionAsync(cancellationToken);

    var totalRowsAffected = 0;
    foreach (var rename in pending) {
      cancellationToken.ThrowIfCancellationRequested();

      var eventTypeAffected = await connection.ExecuteAsync(
        new CommandDefinition(
          "UPDATE wh_event_store SET event_type = @New WHERE event_type = @Old",
          new { New = rename.NewClrTypeName, Old = rename.OldClrTypeName },
          transaction: transaction,
          cancellationToken: cancellationToken));

      var aggregateTypeAffected = await connection.ExecuteAsync(
        new CommandDefinition(
          "UPDATE wh_event_store SET aggregate_type = @New WHERE aggregate_type = @Old",
          new { New = rename.NewClrTypeName, Old = rename.OldClrTypeName },
          transaction: transaction,
          cancellationToken: cancellationToken));

      var inboxAffected = await connection.ExecuteAsync(
        new CommandDefinition(
          "UPDATE wh_inbox SET message_type = @New WHERE message_type = @Old",
          new { New = rename.NewClrTypeName, Old = rename.OldClrTypeName },
          transaction: transaction,
          cancellationToken: cancellationToken));

      var outboxMessageAffected = await connection.ExecuteAsync(
        new CommandDefinition(
          "UPDATE wh_outbox SET message_type = @New WHERE message_type = @Old",
          new { New = rename.NewClrTypeName, Old = rename.OldClrTypeName },
          transaction: transaction,
          cancellationToken: cancellationToken));

      var outboxEnvelopeAffected = await connection.ExecuteAsync(
        new CommandDefinition(
          "UPDATE wh_outbox SET envelope_type = @New WHERE envelope_type = @Old",
          new { New = rename.NewClrTypeName, Old = rename.OldClrTypeName },
          transaction: transaction,
          cancellationToken: cancellationToken));

      var associationsAffected = await connection.ExecuteAsync(
        new CommandDefinition(
          "UPDATE wh_message_associations SET message_type = @New WHERE message_type = @Old",
          new { New = rename.NewClrTypeName, Old = rename.OldClrTypeName },
          transaction: transaction,
          cancellationToken: cancellationToken));

      var perspectiveRegistryAffected = await connection.ExecuteAsync(
        new CommandDefinition(
          "UPDATE wh_perspective_registry SET clr_type_name = @New WHERE clr_type_name = @Old",
          new { New = rename.NewClrTypeName, Old = rename.OldClrTypeName },
          transaction: transaction,
          cancellationToken: cancellationToken));

      var typeRegistryAffected = await connection.ExecuteAsync(
        new CommandDefinition(
          @"UPDATE wh_message_type_registry
            SET clr_type_name = @New, updated_at = NOW()
            WHERE clr_type_name = @Old",
          new { New = rename.NewClrTypeName, Old = rename.OldClrTypeName },
          transaction: transaction,
          cancellationToken: cancellationToken));

      var renameTotal = eventTypeAffected + aggregateTypeAffected + inboxAffected
        + outboxMessageAffected + outboxEnvelopeAffected + associationsAffected
        + perspectiveRegistryAffected + typeRegistryAffected;

      totalRowsAffected += renameTotal;

      _logger?.LogInformation(
        "Renamed '{Old}' → '{New}' ({EventStore}+{Aggregate}+{Inbox}+{OutboxMsg}+{OutboxEnv}+{Assoc}+{PerspectiveRegistry}+{Registry} rows).",
        rename.OldClrTypeName, rename.NewClrTypeName,
        eventTypeAffected, aggregateTypeAffected, inboxAffected,
        outboxMessageAffected, outboxEnvelopeAffected, associationsAffected,
        perspectiveRegistryAffected, typeRegistryAffected);
    }

    await transaction.CommitAsync(cancellationToken);
    _manualRenames.Clear();

    _logger?.LogInformation(
      "Applied {Count} rename(s) affecting {RowsAffected} row(s) across 6 data tables + registry.",
      pending.Count, totalRowsAffected);
  }

  private sealed record RegistryRow(string? PinnedId, string ClrTypeName);
}
