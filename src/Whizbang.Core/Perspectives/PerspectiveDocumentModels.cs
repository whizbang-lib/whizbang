// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Whizbang.Core.Perspectives;

/// <summary>A perspective table and the model type its <c>data</c> document holds.</summary>
/// <param name="Table">The perspective table, unqualified.</param>
/// <param name="Model">The model type.</param>
/// <docs>fundamentals/perspectives/stored-form-migrations#declared-names</docs>
public sealed record PerspectiveDocumentModel(string Table, Type Model);

/// <summary>
/// The model type each perspective table stores, as the host's assemblies registered them at load. The
/// driver-neutral schema generator emits a <c>[ModuleInitializer]</c> in each assembly that declares perspectives,
/// with a <see langword="typeof"/> per table, so a driver that has no model of its own (the Dapper driver) can read a
/// table's declared JSON names from the serializer's metadata for that type, with no reflection (#1874).
/// </summary>
/// <docs>fundamentals/perspectives/stored-form-migrations#declared-names</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveDocumentModelsTests.cs</tests>
public static class PerspectiveDocumentModels {
  private static readonly ConcurrentDictionary<string, Type> _models = new(StringComparer.Ordinal);

  /// <summary>
  /// Registers the model <paramref name="table"/> stores, replacing any registered before. Called from generated
  /// module initializers; two perspectives of one model share its table, so the table is the key.
  /// </summary>
  /// <param name="table">The perspective table.</param>
  /// <param name="model">The model type.</param>
  public static void Register(string table, Type model) {
    ArgumentException.ThrowIfNullOrEmpty(table);
    ArgumentNullException.ThrowIfNull(model);
    _models[table] = model;
  }

  /// <summary>Every registered table and its model.</summary>
  public static IReadOnlyList<PerspectiveDocumentModel> Registered =>
    [.. _models.Select(m => new PerspectiveDocumentModel(m.Key, m.Value))];
}
