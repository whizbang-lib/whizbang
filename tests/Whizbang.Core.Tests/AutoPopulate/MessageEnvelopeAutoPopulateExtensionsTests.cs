using System.Text.Json;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.AutoPopulate;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.AutoPopulate;

/// <summary>
/// Tests for MessageEnvelopeAutoPopulateExtensions.
/// </summary>
/// <tests>src/Whizbang.Core/AutoPopulate/MessageEnvelopeAutoPopulateExtensions.cs</tests>
public class MessageEnvelopeAutoPopulateExtensionsTests {
  private static MessageEnvelope<TestMessage> _createEnvelopeWithAutoPopulatedMetadata(
      Dictionary<string, JsonElement> metadata) {
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "localhost",
        ProcessId = 1234
      },
      Timestamp = DateTimeOffset.UtcNow,
      Topic = "test-topic",
      Metadata = metadata
    };

    return new MessageEnvelope<TestMessage>(
        MessageId.New(),
        new TestMessage("Test", null, null, null, null),
        [hop]);
  }

  #region GetAutoPopulated<T> Tests

  [Test]
  public async Task GetAutoPopulated_ReturnsValue_WhenPropertyExistsAsync() {
    // Arrange
    var expectedValue = DateTimeOffset.UtcNow;
    var metadata = new Dictionary<string, JsonElement> {
      [$"{AutoPopulateProcessor.METADATA_PREFIX}SentAt"] =
          JsonSerializer.SerializeToElement(expectedValue)
    };
    var envelope = _createEnvelopeWithAutoPopulatedMetadata(metadata);

    // Act
    var result = envelope.GetAutoPopulated<DateTimeOffset>("SentAt");

    // Assert
    await Assert.That(result.UtcDateTime).IsEqualTo(expectedValue.UtcDateTime);
  }

  [Test]
  public async Task GetAutoPopulated_ReturnsDefault_WhenPropertyDoesNotExistAsync() {
    // Arrange
    var envelope = _createEnvelopeWithAutoPopulatedMetadata(new Dictionary<string, JsonElement>());

    // Act
    var result = envelope.GetAutoPopulated<DateTimeOffset>("NonExistent");

    // Assert
    await Assert.That(result).IsEqualTo(default(DateTimeOffset));
  }

  [Test]
  public async Task GetAutoPopulated_ReturnsNull_ForNullableWhenPropertyDoesNotExistAsync() {
    // Arrange
    var envelope = _createEnvelopeWithAutoPopulatedMetadata(new Dictionary<string, JsonElement>());

    // Act
    var result = envelope.GetAutoPopulated<string>("NonExistent");

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetAutoPopulated_DeserializesString_CorrectlyAsync() {
    // Arrange
    var expectedValue = "user-123";
    var metadata = new Dictionary<string, JsonElement> {
      [$"{AutoPopulateProcessor.METADATA_PREFIX}CreatedBy"] =
          JsonSerializer.SerializeToElement(expectedValue)
    };
    var envelope = _createEnvelopeWithAutoPopulatedMetadata(metadata);

    // Act
    var result = envelope.GetAutoPopulated<string>("CreatedBy");

    // Assert
    await Assert.That(result).IsEqualTo(expectedValue);
  }

  [Test]
  public async Task GetAutoPopulated_DeserializesGuid_CorrectlyAsync() {
    // Arrange
    var expectedValue = Guid.NewGuid();
    var metadata = new Dictionary<string, JsonElement> {
      [$"{AutoPopulateProcessor.METADATA_PREFIX}CorrelationId"] =
          JsonSerializer.SerializeToElement(expectedValue)
    };
    var envelope = _createEnvelopeWithAutoPopulatedMetadata(metadata);

    // Act
    var result = envelope.GetAutoPopulated<Guid>("CorrelationId");

    // Assert
    await Assert.That(result).IsEqualTo(expectedValue);
  }

  [Test]
  public async Task GetAutoPopulated_DeserializesInt_CorrectlyAsync() {
    // Arrange
    var expectedValue = 12345;
    var metadata = new Dictionary<string, JsonElement> {
      [$"{AutoPopulateProcessor.METADATA_PREFIX}ProcessId"] =
          JsonSerializer.SerializeToElement(expectedValue)
    };
    var envelope = _createEnvelopeWithAutoPopulatedMetadata(metadata);

    // Act
    var result = envelope.GetAutoPopulated<int>("ProcessId");

    // Assert
    await Assert.That(result).IsEqualTo(expectedValue);
  }

  [Test]
  public async Task GetAutoPopulated_ReturnsDefault_WhenMetadataKeyMissingPrefixAsync() {
    // Arrange - metadata stored without the "auto:" prefix
    var metadata = new Dictionary<string, JsonElement> {
      ["SentAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow)
    };
    var envelope = _createEnvelopeWithAutoPopulatedMetadata(metadata);

    // Act
    var result = envelope.GetAutoPopulated<DateTimeOffset>("SentAt");

    // Assert - should return default since it looks for "auto:SentAt"
    await Assert.That(result).IsEqualTo(default(DateTimeOffset));
  }

  #endregion

  #region TryGetAutoPopulated<T> Tests

  [Test]
  public async Task TryGetAutoPopulated_ReturnsTrue_WhenPropertyExistsAsync() {
    // Arrange
    var expectedValue = "tenant-abc";
    var metadata = new Dictionary<string, JsonElement> {
      [$"{AutoPopulateProcessor.METADATA_PREFIX}TenantId"] =
          JsonSerializer.SerializeToElement(expectedValue)
    };
    var envelope = _createEnvelopeWithAutoPopulatedMetadata(metadata);

    // Act
    var found = envelope.TryGetAutoPopulated<string>("TenantId", out var result);

    // Assert
    await Assert.That(found).IsTrue();
    await Assert.That(result).IsEqualTo(expectedValue);
  }

  [Test]
  public async Task TryGetAutoPopulated_ReturnsFalse_WhenPropertyDoesNotExistAsync() {
    // Arrange
    var envelope = _createEnvelopeWithAutoPopulatedMetadata(new Dictionary<string, JsonElement>());

    // Act
    var found = envelope.TryGetAutoPopulated<string>("NonExistent", out var result);

    // Assert
    await Assert.That(found).IsFalse();
    await Assert.That(result).IsNull();
  }

  #endregion

  #region HasAutoPopulated Tests

  [Test]
  public async Task HasAutoPopulated_ReturnsTrue_WhenPropertyExistsAsync() {
    // Arrange
    var metadata = new Dictionary<string, JsonElement> {
      [$"{AutoPopulateProcessor.METADATA_PREFIX}SentAt"] =
          JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow)
    };
    var envelope = _createEnvelopeWithAutoPopulatedMetadata(metadata);

    // Act
    var result = envelope.HasAutoPopulated("SentAt");

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task HasAutoPopulated_ReturnsFalse_WhenPropertyDoesNotExistAsync() {
    // Arrange
    var envelope = _createEnvelopeWithAutoPopulatedMetadata(new Dictionary<string, JsonElement>());

    // Act
    var result = envelope.HasAutoPopulated("NonExistent");

    // Assert
    await Assert.That(result).IsFalse();
  }

  #endregion

  #region GetAllAutoPopulatedKeys Tests

  [Test]
  public async Task GetAllAutoPopulatedKeys_ReturnsAllKeys_WithoutPrefixAsync() {
    // Arrange
    var metadata = new Dictionary<string, JsonElement> {
      [$"{AutoPopulateProcessor.METADATA_PREFIX}SentAt"] =
          JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow),
      [$"{AutoPopulateProcessor.METADATA_PREFIX}CreatedBy"] =
          JsonSerializer.SerializeToElement("user-123"),
      ["other:key"] = JsonSerializer.SerializeToElement("ignored")
    };
    var envelope = _createEnvelopeWithAutoPopulatedMetadata(metadata);

    // Act
    var keys = envelope.GetAllAutoPopulatedKeys().ToList();

    // Assert
    await Assert.That(keys.Count).IsEqualTo(2);
    await Assert.That(keys).Contains("SentAt");
    await Assert.That(keys).Contains("CreatedBy");
    await Assert.That(keys).DoesNotContain("other:key");
  }

  [Test]
  public async Task GetAllAutoPopulatedKeys_ReturnsEmpty_WhenNoAutoPopulatedMetadataAsync() {
    // Arrange
    var metadata = new Dictionary<string, JsonElement> {
      ["other:key"] = JsonSerializer.SerializeToElement("ignored")
    };
    var envelope = _createEnvelopeWithAutoPopulatedMetadata(metadata);

    // Act
    var keys = envelope.GetAllAutoPopulatedKeys().ToList();

    // Assert
    await Assert.That(keys).IsEmpty();
  }

  #endregion

  // Test message for extension method tests
  private sealed record TestMessage(
      string Name,
      DateTimeOffset? SentAt,
      string? CreatedBy,
      string? TenantId,
      Guid? CorrelationId);
}
