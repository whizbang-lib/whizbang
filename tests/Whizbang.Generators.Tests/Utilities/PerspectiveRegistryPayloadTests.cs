// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Generators.Shared.Models;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Tests.Utilities;

/// <summary>
/// The registry payload builder both drivers' generators share (#1548).
/// </summary>
/// <code-under-test>src/Whizbang.Generators.Shared/Utilities/PerspectiveRegistryPayload.cs</code-under-test>
public class PerspectiveRegistryPayloadTests {

  private static string _columnType(PhysicalFieldInfo field) => "TEXT";

  [Test]
  public async Task ToJson_NoEntries_IsAnEmptyArrayAsync() {
    await Assert.That(PerspectiveRegistryPayload.ToJson([], "svc", _columnType)).IsEqualTo("[]");
  }

  [Test]
  public async Task ToJson_TwoEntries_SeparatesThemAndEscapesEachNameAsync() {
    var json = PerspectiveRegistryPayload.ToJson([
      new PerspectiveRegistryEntry("A.Outer+Model", "wh_per_a", "A.Perspective", [], false, false),
      new PerspectiveRegistryEntry("B.\"Quoted\"", "wh_per_b", "B.Perspective", [], true, true),
    ], "svc\\name", _columnType);

    await Assert.That(json).StartsWith("[{\"ClrTypeName\":\"A.Outer+Model\"");
    await Assert.That(json).Contains("},{\"ClrTypeName\":\"B.\\\"Quoted\\\"\"");
    await Assert.That(json).Contains("\"ServiceName\":\"svc\\\\name\"");
  }

  [Test]
  public async Task Indexes_FollowTheDocumentIndexDecisionsAsync() {
    var none = PerspectiveRegistryPayload.Indexes(new("M", "wh_per_m", "P", [], false, false)).Select(i => i.Name).ToList();
    var both = PerspectiveRegistryPayload.Indexes(new("M", "wh_per_m", "P", [], true, true)).Select(i => i.Name).ToList();

    await Assert.That(none).IsEquivalentTo(["idx_m_created_at", "idx_m_updated_at", "idx_m_scope_gin"]);
    await Assert.That(both).Contains("idx_m_data_gin");
    await Assert.That(both).Contains("idx_m_metadata_gin");
  }

  [Test]
  [Arguments(GeneratorVectorIndexType.HNSW, "hnsw")]
  [Arguments(GeneratorVectorIndexType.IVFFlat, "ivfflat")]
  [Arguments(GeneratorVectorIndexType.None, null)]
  [Arguments((GeneratorVectorIndexType)99, null)]
  public async Task Indexes_VectorField_UseTheMethodItNames_OrNoneAsync(GeneratorVectorIndexType indexType, string? method) {
    var field = new PhysicalFieldInfo("Embedding", "embedding", "float[]", IsIndexed: true, IsUnique: false, MaxLength: null,
      IsVector: true, VectorDimensions: 3, VectorDistanceMetric: null, VectorIndexType: indexType, VectorIndexLists: null);

    var vec = PerspectiveRegistryPayload.Indexes(new("M", "wh_per_m", "P", [field], false, false))
      .SingleOrDefault(i => i.Name == "idx_m_embedding_vec");

    await Assert.That(vec?.Type).IsEqualTo(method)
      .Because("the registry describes the index the DDL builds, and the DDL builds none it cannot name");
  }

  [Test]
  public async Task Indexes_IndexedScalarField_IsABtreeAsync() {
    var field = new PhysicalFieldInfo("Price", "price", "decimal", IsIndexed: true, IsUnique: false, MaxLength: null,
      IsVector: false, VectorDimensions: null, VectorDistanceMetric: null, VectorIndexType: null, VectorIndexLists: null);

    var index = PerspectiveRegistryPayload.Indexes(new("M", "wh_per_m", "P", [field], false, false)).Single(i => i.Name == "idx_m_price");

    await Assert.That(index.Type).IsEqualTo("btree");
  }

  [Test]
  public async Task EscapeJsonString_EscapesControlCharactersAsync() {
    await Assert.That(PerspectiveRegistryPayload.EscapeJsonString("a\nb\rc\td")).IsEqualTo("a\\nb\\rc\\td");
  }
}
