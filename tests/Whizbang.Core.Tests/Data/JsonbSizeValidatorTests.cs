using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Policies;

namespace Whizbang.Core.Tests.Data;

/// <summary>
/// Tests for JsonbSizeValidator - validates JSONB column sizes before persistence.
/// Covers threshold detection, metadata warning injection, and policy behavior.
/// </summary>
[Category("Data")]
[Category("Validation")]
public class JsonbSizeValidatorTests {
  private static readonly NullLogger<JsonbSizeValidator> _nullLogger = NullLogger<JsonbSizeValidator>.Instance;

  // ========================================
  // Constructor Tests
  // ========================================

  [Test]
  public async Task Constructor_WithNullLogger_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    await Assert.That(() => new JsonbSizeValidator(null!))
      .Throws<ArgumentNullException>();
  }

  // ========================================
  // SuppressSizeWarnings Tests
  // ========================================

  [Test]
  public async Task Validate_WithSuppressSizeWarnings_SkipsValidationAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    var largeData = new string('x', 10_000); // > externalization threshold
    var model = new JsonbPersistenceModel { DataJson = largeData };
    var policy = new PolicyConfiguration().WithPersistenceSize(suppressWarnings: true);

    // Act
    var result = validator.Validate(model, "TestType", policy);

    // Assert - Model returned unchanged (no warning added)
    await Assert.That(result.MetadataJson).IsEqualTo(model.MetadataJson);
    await Assert.That(result.DataJson).IsEqualTo(model.DataJson);
  }

  // ========================================
  // Threshold Tests
  // ========================================

  [Test]
  public async Task Validate_WithSmallData_ReturnsUnchangedModelAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    var smallData = new string('x', 1_000); // < compression threshold (2KB)
    var model = new JsonbPersistenceModel { DataJson = smallData };

    // Act
    var result = validator.Validate(model, "TestType", null);

    // Assert - Model unchanged, no warning metadata
    await Assert.That(result.MetadataJson).IsEqualTo(model.MetadataJson);
    await Assert.That(result.MetadataJson).DoesNotContain("__size_warning");
  }

  [Test]
  public async Task Validate_WithDataAboveCompressionThreshold_AddsCompressedWarningAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    var dataAboveCompression = new string('x', 3_000); // > 2KB compression, < 7KB externalization
    var model = new JsonbPersistenceModel { DataJson = dataAboveCompression };

    // Act
    var result = validator.Validate(model, "TestType", null);

    // Assert - Warning metadata added with "compressed" type
    await Assert.That(result.MetadataJson).Contains("__size_warning");
    await Assert.That(result.MetadataJson).Contains("compressed");
    await Assert.That(result.MetadataJson).Contains("__size_bytes");
    await Assert.That(result.MetadataJson).Contains("__size_threshold");
  }

  [Test]
  public async Task Validate_WithDataAboveExternalizationThreshold_AddsExternalizedWarningAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    var largeData = new string('x', 8_000); // > 7KB externalization threshold
    var model = new JsonbPersistenceModel { DataJson = largeData };

    // Act
    var result = validator.Validate(model, "TestType", null);

    // Assert - Warning metadata added with "externalized" type
    await Assert.That(result.MetadataJson).Contains("__size_warning");
    await Assert.That(result.MetadataJson).Contains("externalized");
    await Assert.That(result.MetadataJson).Contains("__size_bytes");
    await Assert.That(result.MetadataJson).Contains("__size_threshold");
  }

  [Test]
  public async Task Validate_WithCustomThreshold_UsesCustomThresholdAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    var dataAtCustomThreshold = new string('x', 500); // > custom 400 byte threshold
    var model = new JsonbPersistenceModel { DataJson = dataAtCustomThreshold };
    var policy = new PolicyConfiguration().WithPersistenceSize(maxDataSizeBytes: 400);

    // Act
    var result = validator.Validate(model, "TestType", policy);

    // Assert - Warning added using custom threshold
    await Assert.That(result.MetadataJson).Contains("__size_warning");
    await Assert.That(result.MetadataJson).Contains("externalized");
    await Assert.That(result.MetadataJson).Contains("\"__size_threshold\":400");
  }

  // ========================================
  // ThrowOnSizeExceeded Tests
  // ========================================

  [Test]
  public async Task Validate_WithThrowOnSizeExceeded_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    var largeData = new string('x', 8_000); // > externalization threshold
    var model = new JsonbPersistenceModel { DataJson = largeData };
    var policy = new PolicyConfiguration().WithPersistenceSize(throwOnExceeded: true);

    // Act & Assert
    await Assert.That(() => validator.Validate(model, "TestType", policy))
      .Throws<InvalidOperationException>()
      .WithMessageMatching("*exceeds TOAST externalization threshold*");
  }

  [Test]
  public async Task Validate_WithThrowOnSizeExceeded_IncludesTypeName_InExceptionAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    var largeData = new string('x', 8_000);
    var model = new JsonbPersistenceModel { DataJson = largeData };
    var policy = new PolicyConfiguration().WithPersistenceSize(throwOnExceeded: true);

    // Act & Assert
    await Assert.That(() => validator.Validate(model, "MySpecialEvent", policy))
      .Throws<InvalidOperationException>()
      .WithMessageMatching("*MySpecialEvent*");
  }

  // ========================================
  // Metadata Preservation Tests
  // ========================================

  [Test]
  public async Task Validate_WithExistingMetadata_PreservesExistingFieldsAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    var largeData = new string('x', 3_000); // > compression threshold
    var existingMetadata = "{\"correlationId\":\"abc123\",\"custom\":\"value\"}";
    var model = new JsonbPersistenceModel {
      DataJson = largeData,
      MetadataJson = existingMetadata
    };

    // Act
    var result = validator.Validate(model, "TestType", null);

    // Assert - Original metadata preserved, warning added
    await Assert.That(result.MetadataJson).Contains("correlationId");
    await Assert.That(result.MetadataJson).Contains("abc123");
    await Assert.That(result.MetadataJson).Contains("custom");
    await Assert.That(result.MetadataJson).Contains("value");
    await Assert.That(result.MetadataJson).Contains("__size_warning");
  }

  [Test]
  public async Task Validate_WithEmptyMetadata_AddsWarningMetadataAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    var largeData = new string('x', 3_000); // > compression threshold
    var model = new JsonbPersistenceModel {
      DataJson = largeData,
      MetadataJson = ""
    };

    // Act
    var result = validator.Validate(model, "TestType", null);

    // Assert - Warning metadata added despite empty original metadata
    await Assert.That(result.MetadataJson).Contains("__size_warning");
  }

  [Test]
  public async Task Validate_WithMalformedMetadata_ReturnsOriginalModelAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    var largeData = new string('x', 3_000); // > compression threshold
    var malformedMetadata = "not valid json {{{";
    var model = new JsonbPersistenceModel {
      DataJson = largeData,
      MetadataJson = malformedMetadata
    };

    // Act
    var result = validator.Validate(model, "TestType", null);

    // Assert - Original model returned (error handled gracefully)
    await Assert.That(result.MetadataJson).IsEqualTo(malformedMetadata);
  }

  // ========================================
  // Null Policy Tests
  // ========================================

  [Test]
  public async Task Validate_WithNullPolicy_UsesDefaultThresholdAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    var dataAboveDefault = new string('x', 8_000); // > 7KB default externalization
    var model = new JsonbPersistenceModel { DataJson = dataAboveDefault };

    // Act
    var result = validator.Validate(model, "TestType", null);

    // Assert - Uses default 7KB threshold
    await Assert.That(result.MetadataJson).Contains("__size_warning");
    await Assert.That(result.MetadataJson).Contains("externalized");
  }

  // ========================================
  // Boundary Tests
  // ========================================

  [Test]
  public async Task Validate_AtExactCompressionThreshold_ReturnsUnchangedAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    // 1999 bytes - just below 2KB compression threshold
    var dataBelowCompression = new string('x', 1_999);
    var model = new JsonbPersistenceModel { DataJson = dataBelowCompression };

    // Act
    var result = validator.Validate(model, "TestType", null);

    // Assert - Model unchanged (below threshold)
    await Assert.That(result.MetadataJson).DoesNotContain("__size_warning");
  }

  [Test]
  public async Task Validate_JustAboveCompressionThreshold_AddsWarningAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    // 2001 bytes - just above 2KB compression threshold
    var dataAboveCompression = new string('x', 2_001);
    var model = new JsonbPersistenceModel { DataJson = dataAboveCompression };

    // Act
    var result = validator.Validate(model, "TestType", null);

    // Assert - Warning added
    await Assert.That(result.MetadataJson).Contains("__size_warning");
    await Assert.That(result.MetadataJson).Contains("compressed");
  }

  [Test]
  public async Task Validate_AtExactExternalizationThreshold_AddsCompressedWarningAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    // 6999 bytes - just below 7KB externalization threshold (but above compression)
    var dataBetweenThresholds = new string('x', 6_999);
    var model = new JsonbPersistenceModel { DataJson = dataBetweenThresholds };

    // Act
    var result = validator.Validate(model, "TestType", null);

    // Assert - Compressed warning (not externalized)
    await Assert.That(result.MetadataJson).Contains("__size_warning");
    await Assert.That(result.MetadataJson).Contains("compressed");
    await Assert.That(result.MetadataJson).DoesNotContain("externalized");
  }

  [Test]
  public async Task Validate_JustAboveExternalizationThreshold_AddsExternalizedWarningAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    // 7001 bytes - just above 7KB externalization threshold
    var dataAboveExternalization = new string('x', 7_001);
    var model = new JsonbPersistenceModel { DataJson = dataAboveExternalization };

    // Act
    var result = validator.Validate(model, "TestType", null);

    // Assert - Externalized warning
    await Assert.That(result.MetadataJson).Contains("__size_warning");
    await Assert.That(result.MetadataJson).Contains("externalized");
  }

  // ========================================
  // Default MetadataJson Tests
  // ========================================

  [Test]
  public async Task Validate_WithDefaultMetadata_AddsWarningMetadataAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    var largeData = new string('x', 3_000); // > compression threshold
    // Use default (empty string) MetadataJson
    var model = new JsonbPersistenceModel {
      DataJson = largeData
    };

    // Act
    var result = validator.Validate(model, "TestType", null);

    // Assert - Warning metadata added with default empty metadata
    await Assert.That(result.MetadataJson).Contains("__size_warning");
    await Assert.That(result.MetadataJson).Contains("compressed");
  }

  // ========================================
  // Size Value Verification Tests
  // ========================================

  [Test]
  public async Task Validate_IncludesCorrectSizeInMetadataAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    var dataSize = 3_500; // Known size above compression threshold
    var data = new string('x', dataSize);
    var model = new JsonbPersistenceModel { DataJson = data };

    // Act
    var result = validator.Validate(model, "TestType", null);

    // Assert - Size is stored in metadata (UTF8 encoding means 3500 chars = 3500 bytes for ASCII)
    await Assert.That(result.MetadataJson).Contains($"\"__size_bytes\":{dataSize}");
    await Assert.That(result.MetadataJson).Contains("\"__size_threshold\":2000");
  }

  [Test]
  public async Task Validate_WithExternalization_IncludesCorrectThresholdAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    var data = new string('x', 8_000);
    var model = new JsonbPersistenceModel { DataJson = data };

    // Act
    var result = validator.Validate(model, "TestType", null);

    // Assert - Default externalization threshold (7000)
    await Assert.That(result.MetadataJson).Contains("\"__size_threshold\":7000");
  }

  // ========================================
  // Exception Message Content Tests
  // ========================================

  [Test]
  public async Task Validate_WithThrowOnSizeExceeded_IncludesPerformanceWarningAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    var largeData = new string('x', 8_000);
    var model = new JsonbPersistenceModel { DataJson = largeData };
    var policy = new PolicyConfiguration().WithPersistenceSize(throwOnExceeded: true);

    // Act & Assert
    await Assert.That(() => validator.Validate(model, "TestType", policy))
      .Throws<InvalidOperationException>()
      .WithMessageMatching("*5-10× performance degradation*");
  }

  [Test]
  public async Task Validate_WithThrowOnSizeExceeded_IncludesRecommendationAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    var largeData = new string('x', 8_000);
    var model = new JsonbPersistenceModel { DataJson = largeData };
    var policy = new PolicyConfiguration().WithPersistenceSize(throwOnExceeded: true);

    // Act & Assert
    await Assert.That(() => validator.Validate(model, "TestType", policy))
      .Throws<InvalidOperationException>()
      .WithMessageMatching("*Consider splitting data*");
  }

  // ========================================
  // Policy Configuration Combinations
  // ========================================

  [Test]
  public async Task Validate_WithCustomThresholdAndThrow_UsesCustomThresholdAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    var data = new string('x', 500);
    var model = new JsonbPersistenceModel { DataJson = data };
    var policy = new PolicyConfiguration()
      .WithPersistenceSize(maxDataSizeBytes: 400, throwOnExceeded: true);

    // Act & Assert - Should throw at custom threshold
    await Assert.That(() => validator.Validate(model, "TestType", policy))
      .Throws<InvalidOperationException>()
      .WithMessageMatching("*400 bytes*");
  }

  [Test]
  public async Task Validate_WithSuppressWarnings_DoesNotThrowAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    var largeData = new string('x', 10_000);
    var model = new JsonbPersistenceModel { DataJson = largeData };
    // SuppressWarnings should take precedence over ThrowOnExceeded
    var policy = new PolicyConfiguration()
      .WithPersistenceSize(suppressWarnings: true, throwOnExceeded: true);

    // Act - Should not throw due to suppress warnings
    var result = validator.Validate(model, "TestType", policy);

    // Assert - Returns unchanged model
    await Assert.That(result.DataJson).IsEqualTo(model.DataJson);
    await Assert.That(result.MetadataJson).IsEqualTo(model.MetadataJson);
  }

  // ========================================
  // Complex Metadata Tests
  // ========================================

  [Test]
  public async Task Validate_WithNestedMetadata_PreservesStructureAsync() {
    // Arrange
    var validator = new JsonbSizeValidator(_nullLogger);
    var largeData = new string('x', 3_000);
    var nestedMetadata = "{\"outer\":{\"inner\":\"value\"},\"array\":[1,2,3]}";
    var model = new JsonbPersistenceModel {
      DataJson = largeData,
      MetadataJson = nestedMetadata
    };

    // Act
    var result = validator.Validate(model, "TestType", null);

    // Assert - Nested structure preserved
    await Assert.That(result.MetadataJson).Contains("\"outer\":{\"inner\":\"value\"}");
    await Assert.That(result.MetadataJson).Contains("\"array\":[1,2,3]");
    await Assert.That(result.MetadataJson).Contains("__size_warning");
  }
}
