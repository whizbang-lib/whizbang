// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Capabilities;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The capabilities a context's generated schema requires, in force whatever configuration says: pgvector for
/// every vector column, explicitly, because the column cannot exist without it; and the extension of every
/// optional-extension block (pg_trgm for a trigram index), implicitly, because the schema pass skips the block and
/// the perspective's queries scan instead.
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

  /// <summary>The requirements of a schema's vector columns and its perspectives' optional-extension blocks.</summary>
  /// <param name="vectorColumns">Each vector column, as <c>table.column</c>.</param>
  /// <param name="perspectives">Each perspective's name and the schema SQL the schema pass runs for it.</param>
  /// <returns>
  /// One explicit pgvector requirement per column, then one implicit requirement per perspective and extension its
  /// blocks need.
  /// </returns>
  public static IReadOnlyList<ActiveCapabilityRequirement> From(
      IReadOnlyList<string> vectorColumns, IReadOnlyList<(string Name, string Sql)> perspectives) {
    ArgumentNullException.ThrowIfNull(vectorColumns);
    ArgumentNullException.ThrowIfNull(perspectives);
    var requirements = new List<ActiveCapabilityRequirement>();
    foreach (var column in vectorColumns) {
      requirements.Add(_requirement(VECTOR, column, CapabilityActivation.Explicit));
    }
    foreach (var (name, sql) in perspectives) {
      var extensions = OptionalExtensionBlocks.Split(sql)
        .Select(piece => piece.Extension)
        .OfType<string>()
        .Distinct(StringComparer.Ordinal);
      requirements.AddRange(extensions.Select(extension => _requirement(extension, name, CapabilityActivation.Implicit)));
    }
    return requirements;
  }

  private static ActiveCapabilityRequirement _requirement(string extension, string key, CapabilityActivation activation) {
    var features = PostgresRequiredExtension.Describe(extension).Features;
    var feature = features.Count > 0 ? features[0] : $"Indexes that need {extension}";
    var requirement = new CapabilityRequirement(
      new CapabilityId(PostgresExtensionCatalog.PROVIDER, extension), feature, key, Value: null, Default: null);
    return new ActiveCapabilityRequirement(requirement, key, Value: null, activation);
  }
}
