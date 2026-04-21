using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EFCore/Npgsql implementation of <see cref="IMessageTypeRegistryPopulator"/> that reconciles
/// <c>wh_message_type_registry</c> against the compile-time <see cref="IMessageTypeCatalog"/>
/// using the Whizbang-wide <see cref="NpgsqlDataSource"/>.
/// </summary>
/// <remarks>
/// Mirrors the semantics of the Dapper populator but uses raw Npgsql to avoid pulling a Dapper
/// dependency into the EFCore package. Pinned types upsert by pinned_id and log a warning on
/// drift; unpinned types upsert by clr_type_name.
/// </remarks>
/// <docs>core-concepts/pinned-identity</docs>
[SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Populator runs once at startup; structured logging cost is negligible.")]
[SuppressMessage("Performance", "CA1873:Avoid potentially expensive argument evaluation when logging", Justification = "Populator runs once at startup; argument evaluation cost is negligible.")]
public sealed class EFCoreMessageTypeRegistryPopulator : IMessageTypeRegistryPopulator {
  private readonly IMessageTypeCatalog _catalog;
  private readonly NpgsqlDataSource _dataSource;
  private readonly ILogger<EFCoreMessageTypeRegistryPopulator>? _logger;

  /// <summary>Initializes a new populator.</summary>
  public EFCoreMessageTypeRegistryPopulator(
      IMessageTypeCatalog catalog,
      NpgsqlDataSource dataSource,
      ILogger<EFCoreMessageTypeRegistryPopulator>? logger = null) {
    ArgumentNullException.ThrowIfNull(catalog);
    ArgumentNullException.ThrowIfNull(dataSource);
    _catalog = catalog;
    _dataSource = dataSource;
    _logger = logger;
  }

  /// <inheritdoc/>
  public async Task PopulateAsync(CancellationToken cancellationToken = default) {
    var entries = _catalog.GetAll();
    if (entries.Count == 0) {
      _logger?.LogInformation("Message type catalog is empty; skipping registry population");
      return;
    }

    await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

    // Self-bootstrap: ensure the registry table exists on this connection before upserting.
    // PostgresSchemaBuilder + migration 039 both create this table, but consumer apps with
    // multiple DbContexts may run the populator against an NpgsqlDataSource whose database
    // wasn't initialized via the turnkey EFCore init. CREATE TABLE IF NOT EXISTS is idempotent
    // and keeps the populator working in that shape without special-casing it.
    await _ensureTableAsync(connection, cancellationToken);

    var inserted = 0;
    var updated = 0;
    var drifted = 0;
    var pinnedCount = 0;
    var unpinnedCount = 0;

    foreach (var entry in entries) {
      cancellationToken.ThrowIfCancellationRequested();

      if (entry.PinnedId is not null) {
        pinnedCount++;
        var (existingClrTypeName, exists) = await _lookupByPinnedIdAsync(connection, entry.PinnedId, cancellationToken);

        if (!exists) {
          var affected = await _upsertPinnedNewAsync(connection, entry, cancellationToken);
          if (affected == 1) { inserted++; } else { updated++; }
        } else if (string.Equals(existingClrTypeName, entry.ClrTypeName, StringComparison.Ordinal)) {
          await _touchPinnedAsync(connection, entry, cancellationToken);
          updated++;
        } else {
          drifted++;
          _logger?.LogWarning(
            "Pinned id {PinnedId} is registered with clr_type_name '{StoredClrTypeName}' but current code has '{CurrentClrTypeName}'. Run the rename tool to reconcile.",
            entry.PinnedId, existingClrTypeName, entry.ClrTypeName);
        }
      } else {
        unpinnedCount++;
        var affected = await _upsertUnpinnedAsync(connection, entry, cancellationToken);
        if (affected == 1) { inserted++; } else { updated++; }
      }
    }

    _logger?.LogInformation(
      "Message type registry populated: {Total} entries ({Pinned} pinned, {Unpinned} unpinned; {Inserted} inserted, {Updated} updated, {Drifted} drifted).",
      entries.Count, pinnedCount, unpinnedCount, inserted, updated, drifted);
  }

  private static async Task _ensureTableAsync(NpgsqlConnection connection, CancellationToken ct) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      CREATE TABLE IF NOT EXISTS wh_message_type_registry (
          type_id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
          clr_type_name VARCHAR(500) NOT NULL,
          pinned_id UUID NULL,
          kind VARCHAR(50) NOT NULL,
          updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
          CONSTRAINT uq_message_type_registry_clr_type_name UNIQUE (clr_type_name)
      );
      CREATE UNIQUE INDEX IF NOT EXISTS ix_message_type_registry_pinned_id
          ON wh_message_type_registry (pinned_id)
          WHERE pinned_id IS NOT NULL;";
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private static async Task<(string? ClrTypeName, bool Exists)> _lookupByPinnedIdAsync(
      NpgsqlConnection connection, string pinnedId, CancellationToken ct) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT clr_type_name FROM wh_message_type_registry WHERE pinned_id = @pinnedId::uuid";
    cmd.Parameters.Add(new NpgsqlParameter("pinnedId", NpgsqlDbType.Text) { Value = pinnedId });

    await using var reader = await cmd.ExecuteReaderAsync(ct);
    if (await reader.ReadAsync(ct)) {
      return (reader.GetString(0), true);
    }
    return (null, false);
  }

  private static async Task<int> _upsertPinnedNewAsync(
      NpgsqlConnection connection, MessageTypeCatalogEntry entry, CancellationToken ct) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_message_type_registry (clr_type_name, pinned_id, kind, updated_at)
      VALUES (@clr, @pid::uuid, @kind, NOW())
      ON CONFLICT (clr_type_name) DO UPDATE
        SET pinned_id = EXCLUDED.pinned_id,
            kind = EXCLUDED.kind,
            updated_at = NOW()
        WHERE wh_message_type_registry.pinned_id IS NULL";
    cmd.Parameters.Add(new NpgsqlParameter("clr", NpgsqlDbType.Text) { Value = entry.ClrTypeName });
    cmd.Parameters.Add(new NpgsqlParameter("pid", NpgsqlDbType.Text) { Value = entry.PinnedId! });
    cmd.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = entry.Kind });
    return await cmd.ExecuteNonQueryAsync(ct);
  }

  private static async Task _touchPinnedAsync(
      NpgsqlConnection connection, MessageTypeCatalogEntry entry, CancellationToken ct) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      UPDATE wh_message_type_registry
      SET kind = @kind, updated_at = NOW()
      WHERE pinned_id = @pid::uuid";
    cmd.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = entry.Kind });
    cmd.Parameters.Add(new NpgsqlParameter("pid", NpgsqlDbType.Text) { Value = entry.PinnedId! });
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private static async Task<int> _upsertUnpinnedAsync(
      NpgsqlConnection connection, MessageTypeCatalogEntry entry, CancellationToken ct) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_message_type_registry (clr_type_name, kind, updated_at)
      VALUES (@clr, @kind, NOW())
      ON CONFLICT (clr_type_name) DO UPDATE
        SET kind = EXCLUDED.kind, updated_at = NOW()";
    cmd.Parameters.Add(new NpgsqlParameter("clr", NpgsqlDbType.Text) { Value = entry.ClrTypeName });
    cmd.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = entry.Kind });
    return await cmd.ExecuteNonQueryAsync(ct);
  }
}
