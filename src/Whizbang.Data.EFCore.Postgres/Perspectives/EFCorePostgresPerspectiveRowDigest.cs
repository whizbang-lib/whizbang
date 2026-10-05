// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// Digests a perspective's rows for a known set of streams, over the Postgres driver.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is stable.</strong> The projected document is stored as <c>jsonb</c>, which Postgres keeps in a
/// normalized form: key order and insignificant whitespace are not preserved, so <c>data::text</c> is the same text
/// for the same content no matter how the serializer happened to write it. That takes the usual hazard of hashing
/// serialized state — a formatting change reading as a data change — off the table, without this code having to
/// define a canonical form of its own.
/// </para>
/// <para>
/// <strong>What is excluded, and why.</strong> <c>metadata</c> is left out. Every write touches it (the applied
/// event's id, its commit sequence), so including it would make the digest move whenever a row was rewritten,
/// which is the question a cursor already answers. Excluding it makes a moved digest mean the row's content moved.
/// </para>
/// <para>
/// Ordered so the digest does not depend on the order rows come back in, and computed in one statement so it does
/// not depend on reading them into memory either. The connection is opened once for both the table lookup and the
/// digest: EF's <c>OpenConnectionAsync</c> is reference counted, so this nests safely inside a caller that already
/// has one open and leaves that caller's connection as it found it.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/rebuild#rebuild-row-digest</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRowDigestIntegrationTests.cs</tests>
public sealed class EFCorePostgresPerspectiveRowDigest(
    DbContext dbContext,
    IPerspectiveRunnerRegistry registry,
    IServiceInstanceProvider serviceInstance) : IPerspectiveRowDigest {

  private const string GLOBAL_PREFIX = "global::";

  // A generated perspective table name: lower-case, leading letter or underscore, then letters, digits and
  // underscores. Deliberately narrow.
  private static readonly Regex _safeIdentifier =
      new("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

  /// <inheritdoc/>
  public async Task<string?> ComputeAsync(
      string perspectiveName, IReadOnlyCollection<Guid> streamIds, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(streamIds);

    // No rows asked for means there is nothing to say. A digest of the empty set would compare equal to another
    // digest of the empty set and read as "the rebuild changed nothing", which is a claim this cannot make.
    if (streamIds.Count == 0) { return null; }

    var modelType = registry.GetRegisteredPerspectives()
        .FirstOrDefault(p => string.Equals(p.ClrTypeName, perspectiveName, StringComparison.Ordinal))
        ?.ModelType;
    if (string.IsNullOrWhiteSpace(modelType)) { return null; }

    // The two sides spell the same type differently. PerspectiveRegistrationInfo.ModelType comes from the
    // runner-registry generator and keeps the global:: prefix; wh_perspective_registry.clr_type_name does
    // not, because migration 034 stripped it and the generator that writes it was fixed to stop adding it.
    // Joining them without normalizing matches nothing, which this silently reported as "no digest".
    if (modelType.StartsWith(GLOBAL_PREFIX, StringComparison.Ordinal)) {
      modelType = modelType[GLOBAL_PREFIX.Length..];
    }

    await dbContext.Database.OpenConnectionAsync(ct);
    try {
      var tableName = await _resolveTableNameAsync(modelType, ct);
      if (tableName is null) { return null; }

      // Belt and braces. The name came from the framework's own registry, but it is about to be interpolated
      // into SQL because an identifier cannot be a parameter, so it is checked against the shape a generated
      // table name actually has.
      if (!_safeIdentifier.IsMatch(tableName)) { return null; }

      return await _digestRowsAsync(tableName, streamIds, ct);
    } finally {
      await dbContext.Database.CloseConnectionAsync();
    }
  }

  /// <summary>
  /// The model type's table, scoped to this service.
  /// </summary>
  /// <remarks>
  /// A model type is registered once per service that hosts it — the registry's identity is the
  /// (clr_type_name, service_name) pair, which is what <c>uq_perspective_registry_type_service</c> enforces and
  /// what the reconcile function matches on. Looking the table up by type name alone would be a read over a
  /// non-unique key, and could return a table belonging to a different service.
  /// </remarks>
  private async Task<string?> _resolveTableNameAsync(string modelType, CancellationToken ct) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    await using var command = connection.CreateCommand();
    command.CommandText =
        "SELECT table_name FROM wh_perspective_registry WHERE clr_type_name = @model AND service_name = @service";
    command.Parameters.AddWithValue("model", modelType);
    command.Parameters.AddWithValue("service", serviceInstance.ServiceName);

    var table = await command.ExecuteScalarAsync(ct);
    return table is null or DBNull ? null : (string)table;
  }

  [SuppressMessage("Security", "S2077:Use a parameterized query instead of string formatting",
      Justification =
        "A table identifier cannot be a SQL parameter. The name is not caller-supplied: it is read from the " +
        "framework's own wh_perspective_registry by the model type that the runner registry reports for this " +
        "perspective, scoped to this service. ComputeAsync matches it against _safeIdentifier and this method double-quotes it. A " +
        "name failing either step never reaches here. The row ids, which ARE caller-" +
        "supplied, go through a parameter.")]
  private async Task<string> _digestRowsAsync(
      string tableName, IReadOnlyCollection<Guid> streamIds, CancellationToken ct) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    await using var command = connection.CreateCommand();
    command.CommandText = string.Format(
        CultureInfo.InvariantCulture,
        """
        SELECT md5(string_agg(line, E'\n' ORDER BY line))
          FROM (SELECT r.id::text || '|' || coalesce(r.version::text, '')
                       || '|' || coalesce(r.data::text, '') AS line
                  FROM {0} r
                 WHERE r.id = ANY(@ids)) rows
        """,
        _quote(tableName));
    command.Parameters.AddWithValue("ids", streamIds.ToArray());

    var result = await command.ExecuteScalarAsync(ct);
    // Null when no targeted row exists yet. That is a real state, distinct from "could not compute", so it gets
    // a stable marker rather than null.
    return result is null or DBNull ? "empty" : (string)result;
  }

  private static string _quote(string identifier) =>
    "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
