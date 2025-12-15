using TUnit.Assertions;
using Whizbang.Core.Generated;

namespace Whizbang.Core.Tests.Generated;

/// <summary>
/// Tests for DiagnosticEntry record - diagnostic metadata captured at build time.
/// </summary>
[Category("Diagnostics")]
public class DiagnosticEntryTests {

  [Test]
  public async Task DiagnosticEntry_Constructor_CreatesInstanceWithAllPropertiesAsync() {
    // Arrange
    var generatorName = "TestGenerator";
    var timestamp = "2024-01-01T00:00:00Z";
    var category = DiagnosticCategory.Dispatcher;
    var message = "Test diagnostic message";

    // Act
    var entry = new DiagnosticEntry(generatorName, timestamp, category, message);

    // Assert
    await Assert.That(entry.GeneratorName).IsEqualTo(generatorName);
    await Assert.That(entry.Timestamp).IsEqualTo(timestamp);
    await Assert.That(entry.Category).IsEqualTo(category);
    await Assert.That(entry.Message).IsEqualTo(message);
  }

  [Test]
  public async Task DiagnosticEntry_RecordEquality_ComparesAllPropertiesAsync() {
    // Arrange
    var generatorName = "TestGenerator";
    var timestamp = "2024-01-01T00:00:00Z";
    var category = DiagnosticCategory.Dispatcher;
    var message = "Test diagnostic message";

    var entry1 = new DiagnosticEntry(generatorName, timestamp, category, message);
    var entry2 = new DiagnosticEntry(generatorName, timestamp, category, message);
    var differentEntry = new DiagnosticEntry("DifferentGenerator", timestamp, category, message);

    // Act & Assert - Two instances with same values should be equal
    await Assert.That(entry1).IsEqualTo(entry2);
    await Assert.That(entry1.GetHashCode()).IsEqualTo(entry2.GetHashCode());

    // Act & Assert - Instances with different values should not be equal
    await Assert.That(entry1).IsNotEqualTo(differentEntry);
  }

  [Test]
  public async Task DiagnosticEntry_ToString_ReturnsReadableStringAsync() {
    // Arrange
    var generatorName = "TestGenerator";
    var timestamp = "2024-01-01T00:00:00Z";
    var category = DiagnosticCategory.Dispatcher;
    var message = "Test diagnostic message";

    var entry = new DiagnosticEntry(generatorName, timestamp, category, message);

    // Act
    var result = entry.ToString();

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsNotEmpty();
  }
}
