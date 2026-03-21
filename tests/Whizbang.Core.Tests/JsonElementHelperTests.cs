using System.Text.Json;
using TUnit.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for JsonElementHelper - AOT-compatible helper methods for creating JsonElement values.
/// </summary>
public class JsonElementHelperTests {
  [Test]
  public async Task JsonElementHelper_FromString_WithValidString_ReturnsJsonElementAsync() {
    // Arrange
    const string value = "test string";

    // Act
    var result = JsonElementHelper.FromString(value);

    // Assert
    await Assert.That(result.ValueKind).IsEqualTo(JsonValueKind.String);
    await Assert.That(result.GetString()).IsEqualTo(value);
  }

  [Test]
  public async Task JsonElementHelper_FromString_WithNull_ReturnsNullJsonElementAsync() {
    // Arrange
    string? value = null;

    // Act
    var result = JsonElementHelper.FromString(value);

    // Assert
    await Assert.That(result.ValueKind).IsEqualTo(JsonValueKind.Null);
  }

  [Test]
  public async Task JsonElementHelper_FromString_WithSpecialCharacters_EscapesCorrectlyAsync() {
    // Arrange
    var value = "test\"with\\special\ncharacters\r\n\t\b\f";

    // Act
    var result = JsonElementHelper.FromString(value);

    // Assert
    await Assert.That(result.ValueKind).IsEqualTo(JsonValueKind.String);
    await Assert.That(result.GetString()).IsEqualTo(value);
  }

  [Test]
  public async Task JsonElementHelper_FromString_WithEmptyString_ReturnsEmptyJsonStringAsync() {
    // Arrange
    const string value = "";

    // Act
    var result = JsonElementHelper.FromString(value);

    // Assert
    await Assert.That(result.ValueKind).IsEqualTo(JsonValueKind.String);
    await Assert.That(result.GetString()).IsEqualTo(value);
  }

  [Test]
  public async Task JsonElementHelper_FromInt32_WithValidInt_ReturnsJsonElementAsync() {
    // Arrange
    const int value = 42;

    // Act
    var result = JsonElementHelper.FromInt32(value);

    // Assert
    await Assert.That(result.ValueKind).IsEqualTo(JsonValueKind.Number);
    await Assert.That(result.GetInt32()).IsEqualTo(value);
  }

  [Test]
  public async Task JsonElementHelper_FromInt32_WithZero_ReturnsZeroJsonElementAsync() {
    // Arrange
    const int value = 0;

    // Act
    var result = JsonElementHelper.FromInt32(value);

    // Assert
    await Assert.That(result.ValueKind).IsEqualTo(JsonValueKind.Number);
    await Assert.That(result.GetInt32()).IsEqualTo(value);
  }

  [Test]
  public async Task JsonElementHelper_FromInt32_WithNegative_ReturnsNegativeJsonElementAsync() {
    // Arrange
    const int value = -123;

    // Act
    var result = JsonElementHelper.FromInt32(value);

    // Assert
    await Assert.That(result.ValueKind).IsEqualTo(JsonValueKind.Number);
    await Assert.That(result.GetInt32()).IsEqualTo(value);
  }

  [Test]
  public async Task JsonElementHelper_FromBoolean_WithTrue_ReturnsJsonElementAsync() {
    // Arrange
    const bool value = true;

    // Act
    var result = JsonElementHelper.FromBoolean(value);

    // Assert
    await Assert.That(result.ValueKind).IsEqualTo(JsonValueKind.True);
    await Assert.That(result.GetBoolean()).IsEqualTo(value);
  }

  [Test]
  public async Task JsonElementHelper_FromBoolean_WithFalse_ReturnsJsonElementAsync() {
    // Arrange
    const bool value = false;

    // Act
    var result = JsonElementHelper.FromBoolean(value);

    // Assert
    await Assert.That(result.ValueKind).IsEqualTo(JsonValueKind.False);
    await Assert.That(result.GetBoolean()).IsEqualTo(value);
  }
}
