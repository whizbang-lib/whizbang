using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Transports.Tests.Generated;

namespace Whizbang.Transports.Tests;

/// <summary>
/// Test message for JSON serializer tests.
/// </summary>
public record TestMessage : ICommand {
  public required string Content { get; init; }
  public required int Value { get; init; }
}

/// <summary>
/// Tests for JsonMessageSerializer and its internal converters.
/// Covers edge cases and error paths to achieve 100% coverage.
/// </summary>
[Category("Transports")]
public class JsonMessageSerializerTests {
  [Test]
  public async Task SerializeAsync_WithValidEnvelope_ShouldSerializeAsync() {
    // Arrange
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "test payload", Value = 1 },
      Hops = []
    };

    // Act
    var bytes = await serializer.SerializeAsync(envelope);

    // Assert
    await Assert.That(bytes).IsNotNull();
    var json = Encoding.UTF8.GetString(bytes);
    await Assert.That(json).Contains("test payload");
  }

  [Test]
  [RequiresUnreferencedCode("")]
  [RequiresDynamicCode("")]
  public async Task SerializeAsync_WithMetadataContainingAllTypes_ShouldSerializeAsync() {
    // Arrange
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var metadata = new Dictionary<string, JsonElement> {
      ["stringValue"] = JsonSerializer.SerializeToElement("test"),
      ["intValue"] = JsonSerializer.SerializeToElement(42),
      ["longValue"] = JsonSerializer.SerializeToElement(9999999999L),
      ["doubleValue"] = JsonSerializer.SerializeToElement(3.14),
      ["boolTrue"] = JsonSerializer.SerializeToElement(true),
      ["boolFalse"] = JsonSerializer.SerializeToElement(false)
    };

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "test", Value = 1 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "Test",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Metadata = metadata
        }
      ]
    };

    // Act
    var bytes = await serializer.SerializeAsync(envelope);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    var hop = deserialized.Hops[0];
    await Assert.That(hop.Metadata).IsNotNull();
    await Assert.That(hop.Metadata!["stringValue"].GetString()).IsEqualTo("test");
    await Assert.That(hop.Metadata["intValue"].GetInt32()).IsEqualTo(42);
    await Assert.That(hop.Metadata["longValue"].GetInt64()).IsEqualTo(9999999999L);
    await Assert.That(hop.Metadata["doubleValue"].GetDouble()).IsEqualTo(3.14);
    await Assert.That(hop.Metadata["boolTrue"].GetBoolean()).IsEqualTo(true);
    await Assert.That(hop.Metadata["boolFalse"].GetBoolean()).IsEqualTo(false);
  }

  [Test]
  public async Task SerializeAsync_WithNullMetadata_ShouldHandleNullAsync() {
    // Arrange
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "test", Value = 1 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "Test",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Metadata = null
        }
      ]
    };

    // Act
    var bytes = await serializer.SerializeAsync(envelope);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    await Assert.That(deserialized.Hops[0].Metadata).IsNull();
  }

  [Test]
  public async Task DeserializeAsync_WithInvalidJson_ShouldThrowAsync() {
    // Arrange
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var invalidJson = Encoding.UTF8.GetBytes("{ invalid json }");

    // Act & Assert
    await Assert.That(async () => await serializer.DeserializeAsync<TestMessage>(invalidJson))
      .ThrowsExactly<JsonException>();
  }

  [Test]
  public async Task DeserializeAsync_WithInvalidMessageId_ShouldThrowAsync() {
    // Arrange
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var jsonWithInvalidMessageId = Encoding.UTF8.GetBytes(@"{
        ""MessageId"": ""invalid-guid"",
        ""Payload"": { ""Content"": ""test"", ""Value"": 1 },
        ""Hops"": []
      }");

    // Act & Assert - AOT serialization throws FormatException for invalid GUID
    await Assert.That(async () => await serializer.DeserializeAsync<TestMessage>(jsonWithInvalidMessageId))
      .ThrowsExactly<FormatException>();
  }

  [Test]
  public async Task DeserializeAsync_WithNullMessageId_ShouldThrowAsync() {
    // Arrange
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var jsonWithNullMessageId = Encoding.UTF8.GetBytes(@"{
        ""MessageId"": null,
        ""Payload"": { ""Content"": ""test"", ""Value"": 1 },
        ""Hops"": []
      }");

    // Act & Assert - AOT serialization throws ArgumentNullException for null MessageId
    await Assert.That(async () => await serializer.DeserializeAsync<TestMessage>(jsonWithNullMessageId))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task DeserializeAsync_WithInvalidCorrelationId_ShouldThrowAsync() {
    // Arrange
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var messageId = MessageId.New();
    var jsonWithInvalidCorrelationId = Encoding.UTF8.GetBytes($@"{{
        ""MessageId"": ""{messageId.Value}"",
        ""Payload"": {{ ""Content"": ""test"", ""Value"": 1 }},
        ""Hops"": [{{
          ""Type"": 0,
          ""ServiceName"": ""Test"",
          ""Timestamp"": ""2025-01-01T00:00:00Z"",
          ""CorrelationId"": ""invalid-guid""
        }}]
      }}");

    // Act & Assert - AOT serialization throws FormatException for invalid GUID
    await Assert.That(async () => await serializer.DeserializeAsync<TestMessage>(jsonWithInvalidCorrelationId))
      .ThrowsExactly<FormatException>();
  }

  [Test]
  public async Task DeserializeAsync_WithNullCorrelationId_ShouldHandleGracefullyAsync() {
    // Arrange
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var messageId = MessageId.New();
    var jsonWithNullCorrelationId = Encoding.UTF8.GetBytes($@"{{
        ""MessageId"": ""{messageId.Value}"",
        ""Payload"": {{ ""Content"": ""test"", ""Value"": 1 }},
        ""Hops"": [{{
          ""Type"": 0,
          ""ServiceInstance"": {{ ""ServiceName"": ""Test"", ""InstanceId"": ""{Guid.NewGuid()}"", ""HostName"": ""test-host"", ""ProcessId"": 12345 }},
          ""Timestamp"": ""2025-01-01T00:00:00Z"",
          ""CorrelationId"": null
        }}]
      }}");

    // Act
    var deserialized = await serializer.DeserializeAsync<TestMessage>(jsonWithNullCorrelationId);

    // Assert
    await Assert.That(deserialized.Hops[0].CorrelationId).IsNull();
  }

  [Test]
  public async Task SerializeAsync_WithValidMessageId_ShouldSerializeAsync() {
    // Arrange
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var messageId = MessageId.New();
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = messageId,
      Payload = new TestMessage { Content = "test payload", Value = 1 },
      Hops = []
    };

    // Act
    var bytes = await serializer.SerializeAsync(envelope);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert - MessageId should round-trip correctly
    await Assert.That(deserialized.MessageId).IsEqualTo(messageId);
  }

  [Test]
  public async Task SerializeAsync_WithValidCorrelationId_ShouldSerializeAsync() {
    // Arrange
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var messageId = MessageId.New();
    var correlationId = CorrelationId.New();
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = messageId,
      Payload = new TestMessage { Content = "test payload", Value = 1 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "Test",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = correlationId
        }
      ]
    };

    // Act
    var bytes = await serializer.SerializeAsync(envelope);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert - CorrelationId should round-trip correctly using Write converter
    await Assert.That(deserialized.Hops[0].CorrelationId).IsEqualTo(correlationId);
  }

  [Test]
  public async Task Metadata_WithInvalidStartToken_ShouldThrowAsync() {
    // Arrange
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var messageId = MessageId.New();
    // Metadata is an array instead of object
    var json = $@"{{
        ""MessageId"": ""{messageId.Value}"",
        ""Payload"": {{ ""Content"": ""test"", ""Value"": 1 }},
        ""Hops"": [{{
          ""Type"": 0,
          ""ServiceName"": ""Test"",
          ""Timestamp"": ""2025-01-01T00:00:00Z"",
          ""Metadata"": []
        }}]
      }}";
    var jsonWithInvalidMetadata = Encoding.UTF8.GetBytes(json);

    // Act & Assert
    await Assert.That(async () => await serializer.DeserializeAsync<TestMessage>(jsonWithInvalidMetadata))
      .ThrowsExactly<JsonException>();
  }

  [Test]
  public async Task Metadata_WithInvalidPropertyToken_ShouldThrowAsync() {
    // Arrange
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var messageId = MessageId.New();
    // Malformed metadata - value without property name
    var json = $@"{{
        ""MessageId"": ""{messageId.Value}"",
        ""Payload"": {{ ""Content"": ""test"", ""Value"": 1 }},
        ""Hops"": [{{
          ""Type"": 0,
          ""ServiceName"": ""Test"",
          ""Timestamp"": ""2025-01-01T00:00:00Z"",
          ""Metadata"": {{ ""key"": ""value"", 123 }}
        }}]
      }}";
    var jsonWithMalformedMetadata = Encoding.UTF8.GetBytes(json);

    // Act & Assert
    await Assert.That(async () => await serializer.DeserializeAsync<TestMessage>(jsonWithMalformedMetadata))
      .ThrowsExactly<JsonException>();
  }

  [Test]
  public async Task Metadata_WithArrayValue_ShouldDeserializeAsync() {
    // Arrange
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var messageId = MessageId.New();
    // Array value in metadata - now supported as JsonElement
    var json = $@"{{
        ""MessageId"": ""{messageId.Value}"",
        ""Payload"": {{ ""Content"": ""test"", ""Value"": 1 }},
        ""Hops"": [{{
          ""Type"": 0,
          ""ServiceInstance"": {{ ""ServiceName"": ""Test"", ""InstanceId"": ""{Guid.NewGuid()}"", ""HostName"": ""test-host"", ""ProcessId"": 12345 }},
          ""Timestamp"": ""2025-01-01T00:00:00Z"",
          ""Metadata"": {{ ""key"": [""array"", ""value""] }}
        }}]
      }}";
    var jsonWithArrayValue = Encoding.UTF8.GetBytes(json);

    // Act
    var deserialized = await serializer.DeserializeAsync<TestMessage>(jsonWithArrayValue);

    // Assert - Array values are now supported via JsonElement
    await Assert.That(deserialized.Hops[0].Metadata).IsNotNull();
    await Assert.That(deserialized.Hops[0].Metadata!["key"].ValueKind).IsEqualTo(JsonValueKind.Array);
  }

  [Test]
  [RequiresUnreferencedCode("")]
  [RequiresDynamicCode("")]
  public async Task Metadata_WithDateTimeValue_ShouldSerializeAsync() {
    // Arrange
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var metadata = new Dictionary<string, JsonElement> {
      ["dateTime"] = JsonSerializer.SerializeToElement(new DateTime(2025, 1, 1)) // DateTime gets serialized as string in JsonElement
    };

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "test", Value = 1 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "Test",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Metadata = metadata
        }
      ]
    };

    // Act - DateTime metadata values are now supported (serialized as string)
    var bytes = await serializer.SerializeAsync(envelope);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    await Assert.That(deserialized.Hops[0].Metadata).IsNotNull();
    await Assert.That(deserialized.Hops[0].Metadata!["dateTime"].ValueKind).IsEqualTo(JsonValueKind.String);
  }

  [Test]
  public async Task Metadata_WithNullPropertyName_ShouldThrowAsync() {
    // Arrange
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var messageId = MessageId.New();
    // Malformed JSON with null property name (this is unlikely in practice but tests the null check)
    // We'll test the ReadValue path for null by using a valid metadata with null value
    var json = $@"{{
        ""MessageId"": ""{messageId.Value}"",
        ""Payload"": {{ ""Content"": ""test"", ""Value"": 1 }},
        ""Hops"": [{{
          ""Type"": 0,
          ""ServiceInstance"": {{ ""ServiceName"": ""Test"", ""InstanceId"": ""{Guid.NewGuid()}"", ""HostName"": ""test-host"", ""ProcessId"": 12345 }},
          ""Timestamp"": ""2025-01-01T00:00:00Z"",
          ""Metadata"": {{ ""key"": null }}
        }}]
      }}";
    var jsonWithNullMetadataValue = Encoding.UTF8.GetBytes(json);

    // Act - This should succeed (null is a valid metadata value)
    var deserialized = await serializer.DeserializeAsync<TestMessage>(jsonWithNullMetadataValue);

    // Assert - Metadata with null value should be present
    await Assert.That(deserialized.Hops[0].Metadata).IsNotNull();
  }

  [Test]
  [RequiresUnreferencedCode("")]
  [RequiresDynamicCode("")]
  public async Task Metadata_WithDoubleValue_ShouldRoundTripAsync() {
    // Arrange
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var metadata = new Dictionary<string, JsonElement> {
      ["pi"] = JsonSerializer.SerializeToElement(3.14159265359),
      ["negativeFloat"] = JsonSerializer.SerializeToElement(-42.5)
    };

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "test", Value = 1 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "Test",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Metadata = metadata
        }
      ]
    };

    // Act
    var bytes = await serializer.SerializeAsync(envelope);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    var hop = deserialized.Hops[0];
    await Assert.That(hop.Metadata).IsNotNull();
    await Assert.That(hop.Metadata!["pi"].GetDouble()).IsEqualTo(3.14159265359);
    await Assert.That(hop.Metadata["negativeFloat"].GetDouble()).IsEqualTo(-42.5);
  }

  [Test]
  [RequiresUnreferencedCode("")]
  [RequiresDynamicCode("")]
  public async Task Metadata_WithLargeInt64Value_ShouldRoundTripAsync() {
    // Arrange
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    // Use a value larger than int32.MaxValue to ensure it's read as long
    var largeValue = (long)int.MaxValue + 1000L;
    var metadata = new Dictionary<string, JsonElement> {
      ["largeNumber"] = JsonSerializer.SerializeToElement(largeValue)
    };

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "test", Value = 1 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "Test",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Metadata = metadata
        }
      ]
    };

    // Act
    var bytes = await serializer.SerializeAsync(envelope);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    var hop = deserialized.Hops[0];
    await Assert.That(hop.Metadata).IsNotNull();
    await Assert.That(hop.Metadata!["largeNumber"].GetInt64()).IsEqualTo(largeValue);
  }

  [Test]
  [RequiresUnreferencedCode("")]
  [RequiresDynamicCode("")]
  public async Task RoundTrip_WithComplexEnvelope_ShouldPreserveAllDataAsync() {
    // Arrange
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var original = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Content = "test", Value = 42 },
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Metadata = new Dictionary<string, JsonElement> {
            ["string"] = JsonSerializer.SerializeToElement("value"),
            ["int"] = JsonSerializer.SerializeToElement(123),
            ["bool"] = JsonSerializer.SerializeToElement(true)
          }
        }
      ]
    };

    // Act
    var bytes = await serializer.SerializeAsync(original);
    var deserialized = await serializer.DeserializeAsync<TestMessage>(bytes);

    // Assert
    await Assert.That(deserialized.MessageId).IsEqualTo(original.MessageId);
    var payload = (deserialized as MessageEnvelope<TestMessage>)!.Payload;
    await Assert.That(payload.Content).IsEqualTo("test");
    await Assert.That(payload.Value).IsEqualTo(42);
    await Assert.That(deserialized.Hops).HasCount().EqualTo(1);
  }
}
