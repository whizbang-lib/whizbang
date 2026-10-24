// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Capabilities;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The capabilities a context's generated schema requires, in force whatever configuration says: pgvector for
/// every vector column, explicitly, because the column cannot exist without it.
/// </summary>
/// <remarks>
/// The generated schema extensions call it with the columns they declare; the generated registration puts the
/// result in force as a <see cref="FixedCapabilityRequirementSource"/>, and the data source passes it to
/// <see cref="PostgresRequiredExtension.EnsureAsync"/> so a refusal names the columns.
/// </remarks>
/// <docs>fundamentals/perspectives/vector-fields#pgvector-extension</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresSchemaRequirementsTests.cs</tests>
public static class PostgresSchemaRequirements {
  private const string VECTOR = "vector";

  /// <summary>The requirements of a schema's vector columns.</summary>
  /// <param name="vectorColumns">Each vector column, as <c>table.column</c>.</param>
  /// <returns>One explicit pgvector requirement per column.</returns>
  public static IReadOnlyList<ActiveCapabilityRequirement> From(IReadOnlyList<string> vectorColumns) {
    ArgumentNullException.ThrowIfNull(vectorColumns);
    var vector = new CapabilityId(PostgresExtensionCatalog.PROVIDER, VECTOR);
    var feature = PostgresRequiredExtension.Describe(VECTOR).Features[0];
    return [.. vectorColumns.Select(column => new ActiveCapabilityRequirement(
      new CapabilityRequirement(vector, feature, column, Value: null, Default: null), column, Value: null, CapabilityActivation.Explicit))];
  }
}
