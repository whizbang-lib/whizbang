using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EFCore/Npgsql implementation of <see cref="IMessageTypeRegistryPopulator"/> that delegates to
/// the <c>reconcile_message_type_registry</c> PL/pgSQL function (migration 040) via the shared
/// <see cref="NpgsqlDataSource"/>.
/// </summary>
/// <remarks>
/// The turnkey <c>EnsureWhizbangDatabaseInitializedAsync</c> path calls the same SQL function
/// directly against each DbContext's connection. This DI-registered populator exists for
/// explicit invocation (admin commands, tooling) — it is not auto-run.
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
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT * FROM reconcile_message_type_registry(@p_entries::JSONB)";
    command.Parameters.Add(new NpgsqlParameter("p_entries", NpgsqlDbType.Jsonb) { Value = _serializeEntries(entries) });

    var inserted = 0;
    var updated = 0;
    var renamed = 0;
    var drifted = 0;
    await using (var reader = await command.ExecuteReaderAsync(cancellationToken)) {
      while (await reader.ReadAsync(cancellationToken)) {
        var action = reader.GetString(0);
        var currentClr = reader.GetString(2);
        var storedClr = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3);
        switch (action) {
          case "inserted": inserted++; break;
          case "updated": updated++; break;
          case "renamed":
            renamed++;
            _logger?.LogInformation(
              "Reconciled acknowledged rename: registry clr_type_name '{StoredClrTypeName}' updated to current '{CurrentClrTypeName}' (former name recorded in the pinned-type ledger).",
              storedClr, currentClr);
            break;
          case "drift_detected":
            drifted++;
            _logger?.LogWarning(
              "Pinned id drift: stored clr_type_name '{StoredClrTypeName}' differs from current code '{CurrentClrTypeName}' and is not a recorded former name. Record the rename in .whizbang/pinned-type-ledger.json to reconcile.",
              storedClr, currentClr);
            break;
        }
      }
    }

    var pinned = 0;
    var unpinned = 0;
    foreach (var e in entries) {
      if (e.PinnedId is null) {
        unpinned++;
      } else {
        pinned++;
      }
    }

    _logger?.LogInformation(
      "Message type registry populated: {Total} entries ({Pinned} pinned, {Unpinned} unpinned; {Inserted} inserted, {Updated} updated, {Renamed} renamed, {Drifted} drifted).",
      entries.Count, pinned, unpinned, inserted, updated, renamed, drifted);
  }

  private static string _serializeEntries(IReadOnlyList<MessageTypeCatalogEntry> entries) {
    var sb = new StringBuilder("[");
    for (var i = 0; i < entries.Count; i++) {
      var e = entries[i];
      if (i > 0) {
        sb.Append(',');
      }
      sb.Append("{\"ClrTypeName\":").Append(_jsonString(e.ClrTypeName))
        .Append(",\"PinnedId\":").Append(e.PinnedId is null ? "null" : _jsonString(e.PinnedId))
        .Append(",\"Kind\":").Append(_jsonString(e.Kind))
        .Append(",\"FormerNames\":").Append(_jsonArray(e.FormerNames))
        .Append('}');
    }
    sb.Append(']');
    return sb.ToString();
  }

  private static string _jsonArray(IReadOnlyList<string> values) {
    if (values is null || values.Count == 0) {
      return "[]";
    }
    var sb = new StringBuilder("[");
    for (var i = 0; i < values.Count; i++) {
      if (i > 0) {
        sb.Append(',');
      }
      sb.Append(_jsonString(values[i]));
    }
    sb.Append(']');
    return sb.ToString();
  }

  private static string _jsonString(string? value) {
    if (value is null) {
      return "null";
    }
    var sb = new StringBuilder("\"");
    foreach (var c in value) {
      switch (c) {
        case '"': sb.Append("\\\""); break;
        case '\\': sb.Append("\\\\"); break;
        case '\b': sb.Append("\\b"); break;
        case '\f': sb.Append("\\f"); break;
        case '\n': sb.Append("\\n"); break;
        case '\r': sb.Append("\\r"); break;
        case '\t': sb.Append("\\t"); break;
        default:
          if (c < 0x20) {
            sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "\\u{0:X4}", (int)c);
          } else {
            sb.Append(c);
          }
          break;
      }
    }
    sb.Append('"');
    return sb.ToString();
  }
}
