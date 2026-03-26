extern alias shared;
using shared::Whizbang.Generators.Shared.Utilities;
using TUnit.Assertions;
using TUnit.Core;

namespace Whizbang.Generators.Tests.Utilities;

/// <summary>
/// TDD tests for SchemaHashUtilities - canonical JSON serialization and SHA-256 hashing.
/// Tests ensure consistent hash generation across platforms for perspective schema comparison.
/// </summary>
/// <tests>src/Whizbang.Generators.Shared/Utilities/SchemaHashUtilities.cs</tests>
public class SchemaHashUtilitiesTests {
  #region ComputeHash Tests

  /// <summary>
  /// RED TEST: ComputeHash should return SHA-256 hash as lowercase hex string.
  /// </summary>
  [Test]
  public async Task ComputeHash_EmptyString_ReturnsKnownSha256HashAsync() {
    // Arrange
    const string input = "";
    // SHA-256 of empty string is known: e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855

    // Act
    var hash = SchemaHashUtilities.ComputeHash(input);

    // Assert
    await Assert.That(hash).IsEqualTo("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
  }

  /// <summary>
  /// RED TEST: ComputeHash should return lowercase hex string (64 characters for SHA-256).
  /// </summary>
  [Test]
  public async Task ComputeHash_AnyInput_ReturnsLowercaseHex64CharactersAsync() {
    // Arrange
    const string input = "test";

    // Act
    var hash = SchemaHashUtilities.ComputeHash(input);

    // Assert
    await Assert.That(hash).Length().IsEqualTo(64);
    await Assert.That(hash).Matches("^[a-f0-9]+$");
  }

  /// <summary>
  /// RED TEST: ComputeHash should return consistent results for the same input.
  /// </summary>
  [Test]
  public async Task ComputeHash_SameInput_ReturnsSameHashAsync() {
    // Arrange
    const string input = "{\"columns\":[{\"name\":\"id\",\"type\":\"uuid\"}]}";

    // Act
    var hash1 = SchemaHashUtilities.ComputeHash(input);
    var hash2 = SchemaHashUtilities.ComputeHash(input);

    // Assert
    await Assert.That(hash1).IsEqualTo(hash2);
  }

  /// <summary>
  /// RED TEST: ComputeHash should return different results for different inputs.
  /// </summary>
  [Test]
  public async Task ComputeHash_DifferentInputs_ReturnsDifferentHashesAsync() {
    // Arrange
    const string input1 = "{\"columns\":[{\"name\":\"id\"}]}";
    const string input2 = "{\"columns\":[{\"name\":\"data\"}]}";

    // Act
    var hash1 = SchemaHashUtilities.ComputeHash(input1);
    var hash2 = SchemaHashUtilities.ComputeHash(input2);

    // Assert
    await Assert.That(hash1).IsNotEqualTo(hash2);
  }

  /// <summary>
  /// RED TEST: ComputeHash should handle UTF-8 characters correctly.
  /// </summary>
  [Test]
  public async Task ComputeHash_Utf8Input_ReturnsValidHashAsync() {
    // Arrange - Japanese characters for "hello"
    const string input = "こんにちは";

    // Act
    var hash = SchemaHashUtilities.ComputeHash(input);

    // Assert
    await Assert.That(hash).Length().IsEqualTo(64);
    // SHA-256 of "こんにちは" in UTF-8
    await Assert.That(hash).IsEqualTo("125aeadf27b0459b8760c13a3d80912dfa8a81a68261906f60d87f4a0268646c");
  }

  #endregion

  #region ToCanonicalJson Tests

  /// <summary>
  /// RED TEST: ToCanonicalJson should sort object keys alphabetically.
  /// </summary>
  [Test]
  public async Task ToCanonicalJson_UnsortedKeys_ReturnsSortedKeysAsync() {
    // Arrange - keys in non-alphabetical order
    var columns = new List<ColumnSchema> {
      new("id", "uuid", false, true, false, null)
    };
    var indexes = new List<IndexSchema>();
    var schema = new PerspectiveTableSchema(columns, indexes);

    // Act
    var json = SchemaHashUtilities.ToCanonicalJson(schema);

    // Assert - "columns" should come before "indexes" alphabetically
    var columnsIndex = json.IndexOf("\"columns\"", StringComparison.Ordinal);
    var indexesIndex = json.IndexOf("\"indexes\"", StringComparison.Ordinal);
    await Assert.That(columnsIndex).IsLessThan(indexesIndex);
  }

  /// <summary>
  /// RED TEST: ToCanonicalJson should produce no whitespace.
  /// </summary>
  [Test]
  public async Task ToCanonicalJson_AnySchema_ReturnsNoWhitespaceAsync() {
    // Arrange
    var columns = new List<ColumnSchema> {
      new("id", "uuid", false, true, false, null),
      new("data", "jsonb", false, false, false, null)
    };
    var indexes = new List<IndexSchema>();
    var schema = new PerspectiveTableSchema(columns, indexes);

    // Act
    var json = SchemaHashUtilities.ToCanonicalJson(schema);

    // Assert - no spaces, newlines, or tabs
    await Assert.That(json).DoesNotContain(" ");
    await Assert.That(json).DoesNotContain("\n");
    await Assert.That(json).DoesNotContain("\t");
    await Assert.That(json).DoesNotContain("\r");
  }

  /// <summary>
  /// RED TEST: ToCanonicalJson should use lowercase for property names (camelCase).
  /// </summary>
  [Test]
  public async Task ToCanonicalJson_Properties_UsesCamelCaseAsync() {
    // Arrange
    var columns = new List<ColumnSchema> {
      new("id", "uuid", false, true, false, null)
    };
    var indexes = new List<IndexSchema>();
    var schema = new PerspectiveTableSchema(columns, indexes);

    // Act
    var json = SchemaHashUtilities.ToCanonicalJson(schema);

    // Assert - property names should be camelCase
    await Assert.That(json).Contains("\"isPrimaryKey\"");
    await Assert.That(json).DoesNotContain("\"IsPrimaryKey\"");
  }

  /// <summary>
  /// RED TEST: ToCanonicalJson should use lowercase for type names.
  /// </summary>
  [Test]
  public async Task ToCanonicalJson_TypeNames_UsesLowercaseAsync() {
    // Arrange
    var columns = new List<ColumnSchema> {
      new("id", "UUID", false, true, false, null) // Input with uppercase
    };
    var indexes = new List<IndexSchema>();
    var schema = new PerspectiveTableSchema(columns, indexes);

    // Act
    var json = SchemaHashUtilities.ToCanonicalJson(schema);

    // Assert - type should be lowercase
    await Assert.That(json).Contains("\"type\":\"uuid\"");
    await Assert.That(json).DoesNotContain("\"type\":\"UUID\"");
  }

  /// <summary>
  /// RED TEST: ToCanonicalJson should use lowercase booleans.
  /// Note: False values are omitted from JSON (stored as null).
  /// Only true values appear in output.
  /// </summary>
  [Test]
  public async Task ToCanonicalJson_Booleans_UsesLowercaseAsync() {
    // Arrange - Create column with isPrimaryKey=true to ensure 'true' is output
    var columns = new List<ColumnSchema> {
      new("id", "uuid", false, true, false, null)
    };
    var indexes = new List<IndexSchema>();
    var schema = new PerspectiveTableSchema(columns, indexes);

    // Act
    var json = SchemaHashUtilities.ToCanonicalJson(schema);

    // Assert - true should be lowercase (not True)
    // Note: false values are omitted (stored as null and excluded via JsonIgnoreCondition)
    await Assert.That(json).Contains("true");
    await Assert.That(json).DoesNotContain("True");
    await Assert.That(json).DoesNotContain("False");
  }

  /// <summary>
  /// RED TEST: ToCanonicalJson should omit null values.
  /// </summary>
  [Test]
  public async Task ToCanonicalJson_NullValues_OmitsNullPropertiesAsync() {
    // Arrange - vectorDimensions is null
    var columns = new List<ColumnSchema> {
      new("data", "jsonb", false, false, false, null)
    };
    var indexes = new List<IndexSchema>();
    var schema = new PerspectiveTableSchema(columns, indexes);

    // Act
    var json = SchemaHashUtilities.ToCanonicalJson(schema);

    // Assert - vectorDimensions should not appear at all
    await Assert.That(json).DoesNotContain("vectorDimensions");
  }

  /// <summary>
  /// RED TEST: ToCanonicalJson should include non-null vector dimensions.
  /// </summary>
  [Test]
  public async Task ToCanonicalJson_VectorField_IncludesVectorDimensionsAsync() {
    // Arrange
    var columns = new List<ColumnSchema> {
      new("embedding", "vector", false, false, true, 1536)
    };
    var indexes = new List<IndexSchema>();
    var schema = new PerspectiveTableSchema(columns, indexes);

    // Act
    var json = SchemaHashUtilities.ToCanonicalJson(schema);

    // Assert
    await Assert.That(json).Contains("\"vectorDimensions\":1536");
  }

  /// <summary>
  /// RED TEST: ToCanonicalJson should sort columns within columns array alphabetically by name.
  /// </summary>
  [Test]
  public async Task ToCanonicalJson_Columns_SortedByNameAsync() {
    // Arrange - columns in non-alphabetical order
    var columns = new List<ColumnSchema> {
      new("updated_at", "timestamptz", false, false, false, null),
      new("id", "uuid", false, true, false, null),
      new("data", "jsonb", false, false, false, null)
    };
    var indexes = new List<IndexSchema>();
    var schema = new PerspectiveTableSchema(columns, indexes);

    // Act
    var json = SchemaHashUtilities.ToCanonicalJson(schema);

    // Assert - columns should be sorted by name: data, id, updated_at
    var dataIndex = json.IndexOf("\"name\":\"data\"", StringComparison.Ordinal);
    var idIndex = json.IndexOf("\"name\":\"id\"", StringComparison.Ordinal);
    var updatedAtIndex = json.IndexOf("\"name\":\"updated_at\"", StringComparison.Ordinal);

    await Assert.That(dataIndex).IsLessThan(idIndex);
    await Assert.That(idIndex).IsLessThan(updatedAtIndex);
  }

  /// <summary>
  /// RED TEST: ToCanonicalJson should sort indexes within indexes array alphabetically by name.
  /// </summary>
  [Test]
  public async Task ToCanonicalJson_Indexes_SortedByNameAsync() {
    // Arrange - indexes in non-alphabetical order
    var columns = new List<ColumnSchema> {
      new("id", "uuid", false, true, false, null)
    };
    var indexes = new List<IndexSchema> {
      new("idx_order_created_at", ["created_at"], "btree", false),
      new("idx_order_data_gin", ["data"], "gin", false)
    };
    var schema = new PerspectiveTableSchema(columns, indexes);

    // Act
    var json = SchemaHashUtilities.ToCanonicalJson(schema);

    // Assert - indexes should be sorted by name: idx_order_created_at, idx_order_data_gin
    var createdAtIndex = json.IndexOf("\"name\":\"idx_order_created_at\"", StringComparison.Ordinal);
    var dataGinIndex = json.IndexOf("\"name\":\"idx_order_data_gin\"", StringComparison.Ordinal);

    await Assert.That(createdAtIndex).IsLessThan(dataGinIndex);
  }

  /// <summary>
  /// RED TEST: ToCanonicalJson should produce identical JSON for semantically equivalent schemas.
  /// Order of input should not matter.
  /// </summary>
  [Test]
  public async Task ToCanonicalJson_SemanticallyEquivalentSchemas_ProducesSameJsonAsync() {
    // Arrange - same columns in different order
    var columns1 = new List<ColumnSchema> {
      new("id", "uuid", false, true, false, null),
      new("data", "jsonb", false, false, false, null)
    };
    var columns2 = new List<ColumnSchema> {
      new("data", "jsonb", false, false, false, null),
      new("id", "uuid", false, true, false, null)
    };

    var schema1 = new PerspectiveTableSchema(columns1, []);
    var schema2 = new PerspectiveTableSchema(columns2, []);

    // Act
    var json1 = SchemaHashUtilities.ToCanonicalJson(schema1);
    var json2 = SchemaHashUtilities.ToCanonicalJson(schema2);

    // Assert - should produce identical JSON
    await Assert.That(json1).IsEqualTo(json2);
  }

  #endregion

  #region Integration Tests

  /// <summary>
  /// RED TEST: End-to-end test - same schema produces same hash.
  /// </summary>
  [Test]
  public async Task ComputeSchemaHash_SameSchema_ProducesSameHashAsync() {
    // Arrange
    var columns = new List<ColumnSchema> {
      new("id", "uuid", false, true, false, null),
      new("data", "jsonb", false, false, false, null),
      new("created_at", "timestamptz", false, false, false, null)
    };
    var indexes = new List<IndexSchema> {
      new("idx_created_at", ["created_at"], "btree", false)
    };
    var schema1 = new PerspectiveTableSchema(columns, indexes);
    var schema2 = new PerspectiveTableSchema(columns, indexes);

    // Act
    var hash1 = SchemaHashUtilities.ComputeSchemaHash(schema1);
    var hash2 = SchemaHashUtilities.ComputeSchemaHash(schema2);

    // Assert
    await Assert.That(hash1).IsEqualTo(hash2);
  }

  /// <summary>
  /// RED TEST: Schemas differing only in order should produce same hash.
  /// </summary>
  [Test]
  public async Task ComputeSchemaHash_DifferentOrder_ProducesSameHashAsync() {
    // Arrange - same columns/indexes in different order
    var columns1 = new List<ColumnSchema> {
      new("id", "uuid", false, true, false, null),
      new("data", "jsonb", false, false, false, null)
    };
    var columns2 = new List<ColumnSchema> {
      new("data", "jsonb", false, false, false, null),
      new("id", "uuid", false, true, false, null)
    };

    var schema1 = new PerspectiveTableSchema(columns1, []);
    var schema2 = new PerspectiveTableSchema(columns2, []);

    // Act
    var hash1 = SchemaHashUtilities.ComputeSchemaHash(schema1);
    var hash2 = SchemaHashUtilities.ComputeSchemaHash(schema2);

    // Assert
    await Assert.That(hash1).IsEqualTo(hash2);
  }

  /// <summary>
  /// RED TEST: Schemas with different columns should produce different hashes.
  /// </summary>
  [Test]
  public async Task ComputeSchemaHash_DifferentColumns_ProducesDifferentHashAsync() {
    // Arrange
    var columns1 = new List<ColumnSchema> {
      new("id", "uuid", false, true, false, null)
    };
    var columns2 = new List<ColumnSchema> {
      new("id", "uuid", false, true, false, null),
      new("data", "jsonb", false, false, false, null)
    };

    var schema1 = new PerspectiveTableSchema(columns1, []);
    var schema2 = new PerspectiveTableSchema(columns2, []);

    // Act
    var hash1 = SchemaHashUtilities.ComputeSchemaHash(schema1);
    var hash2 = SchemaHashUtilities.ComputeSchemaHash(schema2);

    // Assert
    await Assert.That(hash1).IsNotEqualTo(hash2);
  }

  #endregion
}

