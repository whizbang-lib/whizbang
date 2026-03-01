namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for DictionaryTypeInfo - ensures value equality for incremental generator caching.
/// </summary>
/// <tests>src/Whizbang.Generators/DictionaryTypeInfo.cs</tests>
public class DictionaryTypeInfoTests {

  /// <summary>
  /// Value equality is critical for incremental generator caching - ensures two records
  /// with the same field values are considered equal.
  /// </summary>
  [Test]
  public async Task DictionaryTypeInfo_ValueEquality_ComparesFieldsAsync() {
    // Arrange - Create two instances with same values
    var info1 = new DictionaryTypeInfo(
      "global::System.Collections.Generic.Dictionary<string, global::MyApp.SeedContext>",
      "string",
      "global::MyApp.SeedContext",
      "SeedContext"
    );
    var info2 = new DictionaryTypeInfo(
      "global::System.Collections.Generic.Dictionary<string, global::MyApp.SeedContext>",
      "string",
      "global::MyApp.SeedContext",
      "SeedContext"
    );

    // Act & Assert - Records use value equality
    await Assert.That(info1).IsEqualTo(info2);
    await Assert.That(info1.GetHashCode()).IsEqualTo(info2.GetHashCode());
  }

  /// <summary>
  /// Verifies that the constructor sets all properties correctly.
  /// </summary>
  [Test]
  public async Task DictionaryTypeInfo_Constructor_SetsPropertiesAsync() {
    // Arrange & Act
    var info = new DictionaryTypeInfo(
      "global::System.Collections.Generic.Dictionary<global::System.Int32, global::MyApp.Product>",
      "global::System.Int32",
      "global::MyApp.Product",
      "Product"
    );

    // Assert
    await Assert.That(info.DictionaryTypeName).IsEqualTo("global::System.Collections.Generic.Dictionary<global::System.Int32, global::MyApp.Product>");
    await Assert.That(info.KeyTypeName).IsEqualTo("global::System.Int32");
    await Assert.That(info.ValueTypeName).IsEqualTo("global::MyApp.Product");
    await Assert.That(info.ValueSimpleName).IsEqualTo("Product");
  }

  /// <summary>
  /// Tests that the UniqueIdentifier property generates a valid C# identifier from key and value types.
  /// </summary>
  [Test]
  public async Task DictionaryTypeInfo_UniqueIdentifier_GeneratesValidIdentifierAsync() {
    // Arrange
    var info = new DictionaryTypeInfo(
      "global::System.Collections.Generic.Dictionary<string, global::MyApp.Models.SeedContext>",
      "string",
      "global::MyApp.Models.SeedContext",
      "SeedContext"
    );

    // Act
    var identifier = info.UniqueIdentifier;

    // Assert - Should strip global:: and replace dots with underscores
    await Assert.That(identifier).IsEqualTo("string_MyApp_Models_SeedContext");
  }

  /// <summary>
  /// Tests UniqueIdentifier with nullable value type.
  /// </summary>
  [Test]
  public async Task DictionaryTypeInfo_UniqueIdentifier_HandlesNullableValueTypeAsync() {
    // Arrange
    var info = new DictionaryTypeInfo(
      "global::System.Collections.Generic.Dictionary<string, global::System.Guid?>",
      "string",
      "global::System.Guid?",
      "Guid"
    );

    // Act
    var identifier = info.UniqueIdentifier;

    // Assert - Should replace ? with __Nullable
    await Assert.That(identifier).IsEqualTo("string_System_Guid__Nullable");
  }

  /// <summary>
  /// Tests UniqueIdentifier with generic value type containing angle brackets.
  /// </summary>
  [Test]
  public async Task DictionaryTypeInfo_UniqueIdentifier_HandlesGenericValueTypeAsync() {
    // Arrange - Dictionary<string, List<Item>>
    var info = new DictionaryTypeInfo(
      "global::System.Collections.Generic.Dictionary<string, global::System.Collections.Generic.List<global::MyApp.Item>>",
      "string",
      "global::System.Collections.Generic.List<global::MyApp.Item>",
      "List"
    );

    // Act
    var identifier = info.UniqueIdentifier;

    // Assert - Should replace < > with underscores
    await Assert.That(identifier).IsEqualTo("string_System_Collections_Generic_List_MyApp_Item_");
  }

  /// <summary>
  /// Tests that different values produce different UniqueIdentifiers (no collisions).
  /// </summary>
  [Test]
  public async Task DictionaryTypeInfo_UniqueIdentifier_DifferentValuesProduceDifferentIdentifiersAsync() {
    // Arrange - Two dictionaries with same ValueSimpleName but different namespaces
    var info1 = new DictionaryTypeInfo(
      "global::System.Collections.Generic.Dictionary<string, global::MyApp.Models.SeedContext>",
      "string",
      "global::MyApp.Models.SeedContext",
      "SeedContext"
    );
    var info2 = new DictionaryTypeInfo(
      "global::System.Collections.Generic.Dictionary<string, global::OtherApp.SeedContext>",
      "string",
      "global::OtherApp.SeedContext",
      "SeedContext"
    );

    // Act
    var id1 = info1.UniqueIdentifier;
    var id2 = info2.UniqueIdentifier;

    // Assert - Different namespaces should produce different identifiers
    await Assert.That(id1).IsNotEqualTo(id2);
    await Assert.That(id1).IsEqualTo("string_MyApp_Models_SeedContext");
    await Assert.That(id2).IsEqualTo("string_OtherApp_SeedContext");
  }
}
