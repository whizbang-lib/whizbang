using System.Text.Json;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.AutoPopulate;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.AutoPopulate;

/// <summary>
/// Coverage-round tests for MessageEnvelopeAutoPopulateExtensions, targeting the null-Hops
/// and non-Current-hop branches of <see cref="MessageEnvelopeAutoPopulateExtensions.GetAllAutoPopulatedKeys"/>
/// and the primitive-deserialization branches of the private <c>_deserializeElement</c> helper
/// that the main suite (MessageEnvelopeAutoPopulateExtensionsTests) does not exercise.
/// </summary>
/// <tests>src/Whizbang.Core/AutoPopulate/MessageEnvelopeAutoPopulateExtensions.cs</tests>
public class MessageEnvelopeAutoPopulateExtensionsCoverageTests {
  private static MessageEnvelope<TestMessage> _createEnvelopeWithHops(List<MessageHop>? hops) {
    return new MessageEnvelope<TestMessage>(
        MessageId.New(),
        new TestMessage("Test"),
        hops!);
  }

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
        new TestMessage("Test"),
        [hop]);
  }

  private static MessageHop _hop(HopType type, Dictionary<string, JsonElement>? metadata) {
    return new MessageHop {
      Type = type,
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "localhost",
        ProcessId = 1234
      },
      Timestamp = DateTimeOffset.UtcNow,
      Metadata = metadata
    };
  }

  #region GetAllAutoPopulatedKeys - defensive/filter branches

  // A null Hops list can arrive on an envelope built without any hop recorded yet; if this
  // guard regressed, scanning it would NullReferenceException instead of reporting "no
  // auto-populated fields yet" -- turning a benign gap into a crash.
  [Test]
  public async Task GetAllAutoPopulatedKeys_WithNullHops_ReturnsEmptyAsync() {
    // Arrange
    var envelope = _createEnvelopeWithHops(null);

    // Act
    var keys = envelope.GetAllAutoPopulatedKeys().ToList();

    // Assert
    await Assert.That(keys).IsEmpty()
      .Because("a null Hops list has nothing to scan and must not throw");
  }

  // Causation hops carry forward the parent message's metadata for tracing; if they were not
  // skipped (or a hop with no metadata at all were not skipped), a value the parent
  // auto-populated would appear to belong to this message too, corrupting provenance for
  // every caller of GetAllAutoPopulatedKeys.
  [Test]
  public async Task GetAllAutoPopulatedKeys_SkipsCausationHopsAndHopsWithNullMetadataAsync() {
    // Arrange
    var currentMetadata = new Dictionary<string, JsonElement> {
      [$"{AutoPopulateProcessor.METADATA_PREFIX}SentAt"] =
          JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow)
    };
    var causationMetadata = new Dictionary<string, JsonElement> {
      [$"{AutoPopulateProcessor.METADATA_PREFIX}ParentOnly"] =
          JsonSerializer.SerializeToElement("should-not-appear")
    };
    var envelope = _createEnvelopeWithHops([
      _hop(HopType.Causation, causationMetadata),
      _hop(HopType.Current, null),
      _hop(HopType.Current, currentMetadata)
    ]);

    // Act
    var keys = envelope.GetAllAutoPopulatedKeys().ToList();

    // Assert
    await Assert.That(keys.Count).IsEqualTo(1)
      .Because("only the Current hop with non-null metadata may contribute keys");
    await Assert.That(keys).Contains("SentAt");
    await Assert.That(keys).DoesNotContain("ParentOnly");
  }

  #endregion

  #region _deserializeElement primitive branches (via GetAutoPopulated<T>)

  // If Int64 deserialization regressed, a long value such as an epoch-millisecond timestamp
  // would silently come back as 0, and a receptor keying off it would treat the field as unset.
  [Test]
  public async Task GetAutoPopulated_DeserializesLong_CorrectlyAsync() {
    // Arrange
    const long expectedValue = 9_876_543_210L;
    var metadata = new Dictionary<string, JsonElement> {
      [$"{AutoPopulateProcessor.METADATA_PREFIX}SequenceNumber"] =
          JsonSerializer.SerializeToElement(expectedValue)
    };
    var envelope = _createEnvelopeWithAutoPopulatedMetadata(metadata);

    // Act
    var result = envelope.GetAutoPopulated<long>("SequenceNumber");

    // Assert
    await Assert.That(result).IsEqualTo(expectedValue);
  }

  // If Boolean deserialization regressed, a populated flag (e.g. "IsRetry") would read back as
  // false regardless of what was actually recorded, hiding true provenance from consumers.
  [Test]
  public async Task GetAutoPopulated_DeserializesBool_CorrectlyAsync() {
    // Arrange
    var metadata = new Dictionary<string, JsonElement> {
      [$"{AutoPopulateProcessor.METADATA_PREFIX}IsRetry"] =
          JsonSerializer.SerializeToElement(true)
    };
    var envelope = _createEnvelopeWithAutoPopulatedMetadata(metadata);

    // Act
    var result = envelope.GetAutoPopulated<bool>("IsRetry");

    // Assert
    await Assert.That(result).IsTrue();
  }

  // If Double deserialization regressed, a populated measurement would come back as 0.0,
  // silently discarding the recorded value.
  [Test]
  public async Task GetAutoPopulated_DeserializesDouble_CorrectlyAsync() {
    // Arrange
    const double expectedValue = 3.14159;
    var metadata = new Dictionary<string, JsonElement> {
      [$"{AutoPopulateProcessor.METADATA_PREFIX}Score"] =
          JsonSerializer.SerializeToElement(expectedValue)
    };
    var envelope = _createEnvelopeWithAutoPopulatedMetadata(metadata);

    // Act
    var result = envelope.GetAutoPopulated<double>("Score");

    // Assert
    await Assert.That(result).IsEqualTo(expectedValue);
  }

  // If Decimal deserialization regressed, a populated monetary amount would come back as 0m --
  // a field that looks valid but has silently lost the value it was meant to carry.
  [Test]
  public async Task GetAutoPopulated_DeserializesDecimal_CorrectlyAsync() {
    // Arrange
    const decimal expectedValue = 1234.56m;
    var metadata = new Dictionary<string, JsonElement> {
      [$"{AutoPopulateProcessor.METADATA_PREFIX}Amount"] =
          JsonSerializer.SerializeToElement(expectedValue)
    };
    var envelope = _createEnvelopeWithAutoPopulatedMetadata(metadata);

    // Act
    var result = envelope.GetAutoPopulated<decimal>("Amount");

    // Assert
    await Assert.That(result).IsEqualTo(expectedValue);
  }

  // If DateTime deserialization regressed, a populated wall-clock stamp would come back as
  // DateTime.MinValue, dropping the recorded "when" for the field.
  [Test]
  public async Task GetAutoPopulated_DeserializesDateTime_CorrectlyAsync() {
    // Arrange
    var expectedValue = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc);
    var metadata = new Dictionary<string, JsonElement> {
      [$"{AutoPopulateProcessor.METADATA_PREFIX}ProcessedAt"] =
          JsonSerializer.SerializeToElement(expectedValue)
    };
    var envelope = _createEnvelopeWithAutoPopulatedMetadata(metadata);

    // Act
    var result = envelope.GetAutoPopulated<DateTime>("ProcessedAt");

    // Assert
    await Assert.That(result).IsEqualTo(expectedValue);
  }

  // Types outside the supported primitive set fall back to default rather than using
  // reflection, to preserve AOT compatibility. A caller who forgot this and expected the
  // complex value back would otherwise see a silent, unexplained default instead of real data.
  [Test]
  public async Task GetAutoPopulated_WithUnsupportedComplexType_ReturnsDefaultAsync() {
    // Arrange
    var metadata = new Dictionary<string, JsonElement> {
      [$"{AutoPopulateProcessor.METADATA_PREFIX}Complex"] =
          JsonSerializer.SerializeToElement(new UnsupportedPayload(5))
    };
    var envelope = _createEnvelopeWithAutoPopulatedMetadata(metadata);

    // Act
    var result = envelope.GetAutoPopulated<UnsupportedPayload>("Complex");

    // Assert
    await Assert.That(result).IsNull()
      .Because("unsupported types must fall back to default instead of reflection-based deserialization");
  }

  #endregion

  private sealed record TestMessage(string Name);

  private sealed record UnsupportedPayload(int Value);
}
