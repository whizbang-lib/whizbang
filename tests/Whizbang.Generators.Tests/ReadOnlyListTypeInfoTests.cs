namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for ReadOnlyListTypeInfo - ensures value equality for incremental generator caching.
/// </summary>
/// <tests>src/Whizbang.Generators/ReadOnlyListTypeInfo.cs</tests>
public class IReadOnlyListTypeInfoTests {

  /// <summary>
  /// Value equality is critical for incremental generator caching - ensures two records
  /// with the same field values are considered equal.
  /// </summary>
  [Test]
  public async Task IReadOnlyListTypeInfo_ValueEquality_ComparesFieldsAsync() {
    // Arrange - Create two instances with same values
    var info1 = new ReadOnlyListTypeInfo(
      "global::System.Collections.Generic.IReadOnlyList<global::MyApp.CatalogItem>",
      "global::MyApp.CatalogItem",
      "CatalogItem"
    );
    var info2 = new ReadOnlyListTypeInfo(
      "global::System.Collections.Generic.IReadOnlyList<global::MyApp.CatalogItem>",
      "global::MyApp.CatalogItem",
      "CatalogItem"
    );

    // Act & Assert - Records use value equality
    await Assert.That(info1).IsEqualTo(info2);
    await Assert.That(info1.GetHashCode()).IsEqualTo(info2.GetHashCode());
  }

  /// <summary>
  /// Verifies that the constructor sets all properties correctly.
  /// </summary>
  [Test]
  public async Task IReadOnlyListTypeInfo_Constructor_SetsPropertiesAsync() {
    // Arrange & Act
    var info = new ReadOnlyListTypeInfo(
      "global::System.Collections.Generic.IReadOnlyList<global::MyApp.Product>",
      "global::MyApp.Product",
      "Product"
    );

    // Assert
    await Assert.That(info.IReadOnlyListTypeName).IsEqualTo("global::System.Collections.Generic.IReadOnlyList<global::MyApp.Product>");
    await Assert.That(info.ElementTypeName).IsEqualTo("global::MyApp.Product");
    await Assert.That(info.ElementSimpleName).IsEqualTo("Product");
  }

  /// <summary>
  /// Tests that the ElementUniqueIdentifier property generates a valid C# identifier.
  /// </summary>
  [Test]
  public async Task IReadOnlyListTypeInfo_ElementUniqueIdentifier_GeneratesValidIdentifierAsync() {
    // Arrange
    var info = new ReadOnlyListTypeInfo(
      "global::System.Collections.Generic.IReadOnlyList<global::MyApp.Models.CatalogItem>",
      "global::MyApp.Models.CatalogItem",
      "CatalogItem"
    );

    // Act
    var identifier = info.ElementUniqueIdentifier;

    // Assert - Should strip global:: and replace dots with underscores
    await Assert.That(identifier).IsEqualTo("MyApp_Models_CatalogItem");
  }

  /// <summary>
  /// Tests ElementUniqueIdentifier with nullable element type.
  /// </summary>
  [Test]
  public async Task IReadOnlyListTypeInfo_ElementUniqueIdentifier_HandlesNullableElementTypeAsync() {
    // Arrange
    var info = new ReadOnlyListTypeInfo(
      "global::System.Collections.Generic.IReadOnlyList<global::System.Guid?>",
      "global::System.Guid?",
      "Guid"
    );

    // Act
    var identifier = info.ElementUniqueIdentifier;

    // Assert - Should replace ? with __Nullable
    await Assert.That(identifier).IsEqualTo("System_Guid__Nullable");
  }
}
