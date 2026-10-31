// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres.Perspectives;

/// <summary>One jsonb document column of a perspective table, and what its reader declares.</summary>
/// <param name="Column">The column: <c>data</c>, <c>metadata</c> or <c>scope</c>.</param>
/// <param name="Shape">The keys its reader reads.</param>
/// <docs>fundamentals/perspectives/stored-form-migrations#declared-names</docs>
public sealed record DeclaredKeyColumn(string Column, DeclaredKeyShape Shape);

/// <summary>A perspective table whose stored keys are brought to their declared spelling at startup (#1874).</summary>
/// <param name="Table">The table, unqualified.</param>
/// <param name="Columns">Its document columns.</param>
/// <docs>fundamentals/perspectives/stored-form-migrations#declared-names</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DeclaredKeyShapesTests.cs</tests>
public sealed record DeclaredKeyTable(string Table, IReadOnlyList<DeclaredKeyColumn> Columns) {
  /// <summary>The prefix of every name this check records in the stored-form journal.</summary>
  public const string JOURNAL_PREFIX = "declared-names:";

  /// <summary>
  /// The name the check of this table is recorded under in <c>wh_stored_form_migrations</c>: the table and a hash of
  /// what its readers declare. A table found clean is settled and not scanned again until its declarations change.
  /// </summary>
  public string JournalName {
    get {
      var described = string.Join("\n", Columns.Select(c => c.Column + "=" + c.Shape.Describe()));
      var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(described)))[..12];
      return $"{JOURNAL_PREFIX}{Table}:{hash}";
    }
  }

  /// <summary>
  /// The tables of models a driver stores with the serializer, each described by the serializer's metadata for its
  /// model, <see cref="PerspectiveMetadata"/> and <see cref="PerspectiveScope"/>.
  /// </summary>
  /// <param name="models">Each table and its model, as the host's assemblies registered them.</param>
  /// <param name="options">The options the documents are read with.</param>
  public static IReadOnlyList<DeclaredKeyTable> ForSerializedModels(IEnumerable<PerspectiveDocumentModel> models, JsonSerializerOptions options) {
    ArgumentNullException.ThrowIfNull(models);
    ArgumentNullException.ThrowIfNull(options);
    var metadata = DeclaredKeyShape.FromSerializer(options, typeof(PerspectiveMetadata));
    var scope = DeclaredKeyShape.FromSerializer(options, typeof(PerspectiveScope));
    return [.. models
      .OrderBy(m => m.Table, StringComparer.Ordinal)
      .Select(m => new DeclaredKeyTable(m.Table, [.. _columns(
        ("data", DeclaredKeyShape.FromSerializer(options, m.Model)), ("metadata", metadata), ("scope", scope))]))];
  }

  private static IEnumerable<DeclaredKeyColumn> _columns(params (string Column, DeclaredKeyShape? Shape)[] columns) =>
    columns.Where(c => c.Shape is not null).Select(c => new DeclaredKeyColumn(c.Column, c.Shape!));
}
