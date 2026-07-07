using System.Diagnostics.CodeAnalysis;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Data;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Dapper implementation of <see cref="IMessageTypeRegistryPopulator"/> that delegates to the
/// <c>reconcile_message_type_registry</c> PL/pgSQL function (migration 040).
/// </summary>
/// <remarks>
/// Provided for explicit invocation (admin commands, migrations). Turnkey startup paths run
/// the same SQL function per-DbContext inside <c>EnsureWhizbangDatabaseInitializedAsync</c>.
/// </remarks>
/// <docs>core-concepts/pinned-identity</docs>
[SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Populator runs once at startup; structured logging cost is negligible.")]
[SuppressMessage("Performance", "CA1873:Avoid potentially expensive argument evaluation when logging", Justification = "Populator runs once at startup; argument evaluation cost is negligible.")]
public sealed class DapperMessageTypeRegistryPopulator : IMessageTypeRegistryPopulator {
  private readonly IMessageTypeCatalog _catalog;
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly ILogger<DapperMessageTypeRegistryPopulator>? _logger;

  /// <summary>Initializes a new populator.</summary>
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

    var rows = await connection.QueryAsync<ReconcileRow>(
      new CommandDefinition(
        "SELECT o_action AS Action, o_pinned_id AS PinnedId, o_clr_type_name AS ClrTypeName, o_stored_clr_type_name AS StoredClrTypeName FROM reconcile_message_type_registry(@Entries::jsonb)",
        new { Entries = _serializeEntries(entries) },
        cancellationToken: cancellationToken));

    var inserted = 0;
    var updated = 0;
    var renamed = 0;
    var drifted = 0;
    foreach (var row in rows) {
      switch (row.Action) {
        case "inserted": inserted++; break;
        case "updated": updated++; break;
        case "renamed":
          renamed++;
          _logger?.LogInformation(
            "Reconciled acknowledged rename: registry clr_type_name '{StoredClrTypeName}' updated to current '{CurrentClrTypeName}' (former name recorded in the pinned-type ledger).",
            row.StoredClrTypeName, row.ClrTypeName);
          break;
        case "drift_detected":
          drifted++;
          _logger?.LogWarning(
            "Pinned id drift: stored clr_type_name '{StoredClrTypeName}' differs from current code '{CurrentClrTypeName}' and is not a recorded former name. Record the rename in .whizbang/pinned-type-ledger.json to reconcile.",
            row.StoredClrTypeName, row.ClrTypeName);
          break;
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

  private sealed record ReconcileRow(string Action, Guid? PinnedId, string ClrTypeName, string? StoredClrTypeName);

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
