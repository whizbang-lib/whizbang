using System.Diagnostics.CodeAnalysis;
using Dapper;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Data;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Postgres implementation of <see cref="IMessageTypeRegistryPopulator"/> backed by Dapper.
/// </summary>
/// <remarks>
/// <para>
/// On every call, walks <see cref="IMessageTypeCatalog"/> and upserts rows into
/// <c>wh_message_type_registry</c>:
/// </para>
/// <list type="bullet">
///   <item>Pinned types are upserted by <c>pinned_id</c>. When a registry row's
///         <c>clr_type_name</c> differs from the current code, a warning is logged and the
///         row is <b>not</b> overwritten — only the rename tool (Phase 5) should reconcile drift.</item>
///   <item>Unpinned types are upserted by <c>clr_type_name</c>.</item>
/// </list>
/// <para>
/// Safe to call on every startup.
/// </para>
/// </remarks>
/// <docs>core-concepts/pinned-identity</docs>
[SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Populator runs once at startup; structured logging cost is negligible.")]
[SuppressMessage("Performance", "CA1873:Avoid potentially expensive argument evaluation when logging", Justification = "Populator runs once at startup; argument evaluation cost is negligible.")]
public sealed class DapperMessageTypeRegistryPopulator : IMessageTypeRegistryPopulator {
  private readonly IMessageTypeCatalog _catalog;
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly ILogger<DapperMessageTypeRegistryPopulator>? _logger;

  /// <summary>
  /// Initializes a new populator.
  /// </summary>
  public DapperMessageTypeRegistryPopulator(
      IMessageTypeCatalog catalog,
      IDbConnectionFactory connectionFactory,
      ILogger<DapperMessageTypeRegistryPopulator>? logger = null) {
    ArgumentNullException.ThrowIfNull(catalog);
    ArgumentNullException.ThrowIfNull(connectionFactory);
    _catalog = catalog;
    _connectionFactory = connectionFactory;
    _logger = logger;
  }

  /// <inheritdoc/>
  public async Task PopulateAsync(CancellationToken cancellationToken = default) {
    var entries = _catalog.GetAll();
    if (entries.Count == 0) {
      _logger?.LogInformation("Message type catalog is empty; skipping registry population");
      return;
    }

    using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);

    var inserted = 0;
    var updated = 0;
    var drifted = 0;
    var pinnedCount = 0;
    var unpinnedCount = 0;

    foreach (var entry in entries) {
      cancellationToken.ThrowIfCancellationRequested();

      if (entry.PinnedId is not null) {
        pinnedCount++;
        var existing = await connection.QueryFirstOrDefaultAsync<(Guid TypeId, string ClrTypeName)?>(
          new CommandDefinition(
            @"SELECT type_id AS TypeId, clr_type_name AS ClrTypeName
              FROM wh_message_type_registry
              WHERE pinned_id = @PinnedId::uuid",
            new { PinnedId = entry.PinnedId },
            cancellationToken: cancellationToken));

        if (existing is null) {
          var affected = await connection.ExecuteAsync(
            new CommandDefinition(
              @"INSERT INTO wh_message_type_registry (clr_type_name, pinned_id, kind, updated_at)
                VALUES (@ClrTypeName, @PinnedId::uuid, @Kind, NOW())
                ON CONFLICT (clr_type_name) DO UPDATE
                  SET pinned_id = EXCLUDED.pinned_id,
                      kind = EXCLUDED.kind,
                      updated_at = NOW()
                  WHERE wh_message_type_registry.pinned_id IS NULL",
              new { entry.ClrTypeName, entry.PinnedId, entry.Kind },
              cancellationToken: cancellationToken));
          if (affected == 1) {
            inserted++;
          }
        } else if (string.Equals(existing.Value.ClrTypeName, entry.ClrTypeName, StringComparison.Ordinal)) {
          await connection.ExecuteAsync(
            new CommandDefinition(
              @"UPDATE wh_message_type_registry
                SET kind = @Kind, updated_at = NOW()
                WHERE pinned_id = @PinnedId::uuid",
              new { entry.Kind, entry.PinnedId },
              cancellationToken: cancellationToken));
          updated++;
        } else {
          drifted++;
          _logger?.LogWarning(
            "Pinned id {PinnedId} is registered with clr_type_name '{StoredClrTypeName}' but current code has '{CurrentClrTypeName}'. Run the rename tool to reconcile.",
            entry.PinnedId,
            existing.Value.ClrTypeName,
            entry.ClrTypeName);
        }
      } else {
        unpinnedCount++;
        var affected = await connection.ExecuteAsync(
          new CommandDefinition(
            @"INSERT INTO wh_message_type_registry (clr_type_name, kind, updated_at)
              VALUES (@ClrTypeName, @Kind, NOW())
              ON CONFLICT (clr_type_name) DO UPDATE
                SET kind = EXCLUDED.kind, updated_at = NOW()",
            new { entry.ClrTypeName, entry.Kind },
            cancellationToken: cancellationToken));
        if (affected == 1) {
          inserted++;
        } else {
          updated++;
        }
      }
    }

    _logger?.LogInformation(
      "Message type registry populated: {Total} entries ({Pinned} pinned, {Unpinned} unpinned; {Inserted} inserted, {Updated} updated, {Drifted} drifted).",
      entries.Count, pinnedCount, unpinnedCount, inserted, updated, drifted);
  }
}
