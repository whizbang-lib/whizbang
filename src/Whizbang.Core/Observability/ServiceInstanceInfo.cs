using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whizbang.Core.Observability;

/// <summary>
/// Immutable record containing service instance identification and metadata.
/// Used for distributed tracing and observability to track which service instance
/// processed a message at each hop in its journey.
/// IMPORTANT: Can only be created by IServiceInstanceProvider - constructor is internal.
/// </summary>
/// <tests>tests/Whizbang.Observability.Tests/SerializationTests.cs</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs</tests>
[JsonConverter(typeof(ServiceInstanceInfoConverter))]
public record ServiceInstanceInfo {
  /// <summary>
  /// The name of the service (e.g., "OrderService", "InventoryWorker")
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/SerializationTests.cs</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs</tests>
  [JsonPropertyName("sn")]
  public required string ServiceName { get; init; }

  /// <summary>
  /// Unique UUIDv7 identifier for this specific service instance
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/SerializationTests.cs</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs</tests>
  [JsonPropertyName("ii")]
  public required Guid InstanceId { get; init; }

  /// <summary>
  /// The machine/host name where the service is running
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/SerializationTests.cs</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs</tests>
  [JsonPropertyName("hn")]
  public required string HostName { get; init; }

  /// <summary>
  /// The operating system process ID
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/SerializationTests.cs</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs</tests>
  [JsonPropertyName("pi")]
  public required int ProcessId { get; init; }

  /// <summary>
  /// Public parameterless constructor required for JSON deserialization.
  /// The 'required' modifier on properties ensures all fields are set during initialization.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/SerializationTests.cs</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs</tests>
  public ServiceInstanceInfo() { }

  /// <summary>
  /// Sentinel value for when service instance information is not available.
  /// Used when IServiceInstanceProvider is not configured in TransportManager.
  /// </summary>
  public static readonly ServiceInstanceInfo Unknown = new() {
    ServiceName = "Unknown",
    InstanceId = Guid.Empty,
    HostName = "Unknown",
    ProcessId = 0
  };
}

/// <summary>
/// Custom JSON converter for ServiceInstanceInfo that accepts both short and long property names.
/// Short names (new format): sn, ii, hn, pi
/// Long names (legacy format): ServiceName, InstanceId, HostName, ProcessId
/// </summary>
public sealed class ServiceInstanceInfoConverter : JsonConverter<ServiceInstanceInfo> {
  /// <inheritdoc/>
  public override ServiceInstanceInfo? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
    if (reader.TokenType != JsonTokenType.StartObject) {
      throw new JsonException("Expected start of object for ServiceInstanceInfo");
    }

    // Read entire object as JsonElement first to handle property name variations
    using var doc = JsonDocument.ParseValue(ref reader);
    var root = doc.RootElement;

    // Extract ServiceName - accept both short and long names
    string? serviceName = null;
    if (root.TryGetProperty("sn", out var snElem) || root.TryGetProperty("ServiceName", out snElem)) {
      serviceName = snElem.GetString();
    }
    if (serviceName is null) {
      throw new JsonException("Missing required property: ServiceName (or sn)");
    }

    // Extract InstanceId - accept both short and long names
    Guid? instanceId = null;
    if (root.TryGetProperty("ii", out var iiElem) || root.TryGetProperty("InstanceId", out iiElem)) {
      instanceId = iiElem.GetGuid();
    }
    if (instanceId is null) {
      throw new JsonException("Missing required property: InstanceId (or ii)");
    }

    // Extract HostName - accept both short and long names
    string? hostName = null;
    if (root.TryGetProperty("hn", out var hnElem) || root.TryGetProperty("HostName", out hnElem)) {
      hostName = hnElem.GetString();
    }
    if (hostName is null) {
      throw new JsonException("Missing required property: HostName (or hn)");
    }

    // Extract ProcessId - accept both short and long names
    int? processId = null;
    if (root.TryGetProperty("pi", out var piElem) || root.TryGetProperty("ProcessId", out piElem)) {
      processId = piElem.GetInt32();
    }
    if (processId is null) {
      throw new JsonException("Missing required property: ProcessId (or pi)");
    }

    return new ServiceInstanceInfo {
      ServiceName = serviceName,
      InstanceId = instanceId.Value,
      HostName = hostName,
      ProcessId = processId.Value
    };
  }

  /// <inheritdoc/>
  public override void Write(Utf8JsonWriter writer, ServiceInstanceInfo value, JsonSerializerOptions options) {
    // Always write with short property names
    writer.WriteStartObject();
    writer.WriteString("sn", value.ServiceName);
    writer.WriteString("ii", value.InstanceId);
    writer.WriteString("hn", value.HostName);
    writer.WriteNumber("pi", value.ProcessId);
    writer.WriteEndObject();
  }
}
