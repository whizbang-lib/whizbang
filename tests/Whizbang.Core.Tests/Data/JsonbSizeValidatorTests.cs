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
}
