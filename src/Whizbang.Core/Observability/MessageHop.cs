using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Medo;
using Whizbang.Core.Policies;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Observability;

/// <summary>
/// The type of hop - whether it represents processing of the current message
/// or carry-forward information from the causation/parent message.
/// </summary>
public enum HopType {
  /// <summary>
  /// This hop represents processing of the current message.
  /// </summary>
  Current = 0,

  /// <summary>
  /// This hop is carried forward from the causation/parent message.
  /// Used for distributed tracing to show what led to the current message.
  /// </summary>
  Causation = 1
}

/// <summary>
/// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithRequiredProperties_InitializesWithDefaultsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithCausationType_StoresCausationTypeAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithAllProperties_StoresAllValuesAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_TypeDefaultsToCurrent_WhenNotSpecifiedAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_MachineName_UsesEnvironmentMachineName_ByDefaultAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithCausationAndCorrelationIds_SetsIdsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithSecurityContext_SetsSecurityContextAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithMetadata_SetsMetadataAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithTrail_SetsPolicyDecisionTrailAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithExpression_CreatesModifiedCopyAsync</tests>
/// Represents a single hop in a message's journey through the system.
/// Records where and when the message was processed, including caller information for debugging.
/// Can represent either a hop for the current message or carry-forward hop from the causation message.
/// </summary>
/// <docs>fundamentals/persistence/observability</docs>
[JsonConverter(typeof(MessageHopConverter))]
public record MessageHop {
  /// <summary>
  /// The type of hop - Current (for this message) or Causation (from parent message).
  /// Defaults to Current. Causation hops are carried forward for distributed tracing.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithCausationType_StoresCausationTypeAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_TypeDefaultsToCurrent_WhenNotSpecifiedAsync</tests>
  [JsonPropertyName("ty")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public HopType Type { get; init; } = HopType.Current;

  /// <summary>
  /// The MessageId of the causation/parent message (only for Causation hops).
  /// Null for Current hops (the current message's ID is on the envelope).
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithCausationAndCorrelationIds_SetsIdsAsync</tests>
  [JsonPropertyName("ca")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public MessageId? CausationId { get; init; }

  /// <summary>
  /// The MessageId of the causation/parent message (only for Causation hops).
  /// Null for Current hops (the current message's ID is on the envelope).
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithCausationAndCorrelationIds_SetsIdsAsync</tests>
  [JsonPropertyName("co")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public CorrelationId? CorrelationId { get; init; }

  /// <summary>
  /// The type name of the causation/parent message (only for Causation hops).
  /// Useful for debugging to understand what type of message led to this one.
  /// Null for Current hops.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithCausationType_StoresCausationTypeAsync</tests>
  [JsonPropertyName("ct")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? CausationType { get; init; }

  /// <summary>
  /// Information about the service instance that processed this message.
  /// Includes service name, instance ID, host name, and process ID for complete traceability.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithRequiredProperties_InitializesWithDefaultsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithAllProperties_StoresAllValuesAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_MachineName_UsesEnvironmentMachineName_ByDefaultAsync</tests>
  [JsonPropertyName("si")]
  public required ServiceInstanceInfo ServiceInstance { get; init; }

  /// <summary>
  /// When this hop occurred.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithRequiredProperties_InitializesWithDefaultsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithAllProperties_StoresAllValuesAsync</tests>
  [JsonPropertyName("ts")]
  public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

  /// <summary>
  /// The topic at this hop.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithRequiredProperties_InitializesWithDefaultsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithAllProperties_StoresAllValuesAsync</tests>
  [JsonPropertyName("to")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public string Topic { get; init; } = string.Empty;

  /// <summary>
  /// The stream key at this hop.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithRequiredProperties_InitializesWithDefaultsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithAllProperties_StoresAllValuesAsync</tests>
  [JsonPropertyName("st")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public string StreamId { get; init; } = string.Empty;

  /// <summary>
  /// The partition index at this hop (if applicable).
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithAllProperties_StoresAllValuesAsync</tests>
  [JsonPropertyName("pi")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public int? PartitionIndex { get; init; }

  /// <summary>
  /// The sequence number at this hop (if applicable).
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithAllProperties_StoresAllValuesAsync</tests>
  [JsonPropertyName("sn")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public long? SequenceNumber { get; init; }

  /// <summary>
  /// The execution strategy used at this hop (e.g., "SerialExecutor", "ParallelExecutor").
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithRequiredProperties_InitializesWithDefaultsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithAllProperties_StoresAllValuesAsync</tests>
  [JsonPropertyName("es")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public string ExecutionStrategy { get; init; } = string.Empty;

  /// <summary>
  /// Scope delta at this hop (changes to security/authorization context).
  /// Stores only what changed from the previous hop (delta storage pattern).
  /// Use envelope.GetCurrentScope() to merge deltas and get full ScopeContext.
  /// If null, scope is inherited unchanged from previous hop.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Security/ScopeDeltaTests.cs</tests>
  /// <tests>tests/Whizbang.Core.Tests/Observability/ScopeDeltaIntegrationTests.cs</tests>
  [JsonPropertyName("sc")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public ScopeDelta? Scope { get; init; }

  /// <summary>
  /// Metadata for this hop (tags, flags, custom data).
  /// Later hops override earlier hops for same keys when stitched together.
  /// If null, inherits from previous hop.
  /// Supports any JSON value type (string, number, boolean, object, array) via JsonElement.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithMetadata_SetsMetadataAsync</tests>
  [JsonPropertyName("md")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }

  /// <summary>
  /// Policy decisions made at this hop.
  /// Records all policy evaluations that occurred during processing at this point.
  /// If null, no policies were evaluated at this hop.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithTrail_SetsPolicyDecisionTrailAsync</tests>
  [JsonPropertyName("tr")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public PolicyDecisionTrail? Trail { get; init; }

  /// <summary>
  /// The name of the calling method.
  /// Automatically captured via [CallerMemberName] attribute.
  /// Enables "jump to line" functionality in VSCode extension.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithAllProperties_StoresAllValuesAsync</tests>
  [JsonPropertyName("cm")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? CallerMemberName { get; init; }

  /// <summary>
  /// The file path of the calling code.
  /// Automatically captured via [CallerFilePath] attribute.
  /// Enables "jump to line" functionality in VSCode extension.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithAllProperties_StoresAllValuesAsync</tests>
  [JsonPropertyName("cf")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? CallerFilePath { get; init; }

  /// <summary>
  /// The line number of the calling code.
  /// Automatically captured via [CallerLineNumber] attribute.
  /// Enables "jump to line" functionality in VSCode extension.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithAllProperties_StoresAllValuesAsync</tests>
  [JsonPropertyName("cl")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public int? CallerLineNumber { get; init; }

  /// <summary>
  /// How long this hop took to process.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageHopTests.cs:MessageHop_WithAllProperties_StoresAllValuesAsync</tests>
  [JsonPropertyName("du")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public TimeSpan Duration { get; init; }

  /// <summary>
  /// W3C Trace Context traceparent header value for distributed tracing.
  /// Format: {version}-{trace-id}-{parent-id}-{trace-flags}
  /// Example: "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"
  /// </summary>
  /// <remarks>
  /// This enables correlation with OpenTelemetry spans and external tracing systems.
  /// The value is captured from Activity.Current when the hop is created.
  /// </remarks>
  [JsonPropertyName("tp")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? TraceParent { get; init; }
}

/// <summary>
/// Custom JSON converter for MessageHop that accepts both short and long property names.
/// Short names (new format): ty, ca, co, ct, si, ts, to, st, pi, sn, es, sc, md, tr, cm, cf, cl, du, tp
/// Long names (legacy format): Type, CausationId, CorrelationId, CausationType, ServiceInstance, Timestamp, etc.
/// </summary>
public sealed class MessageHopConverter : JsonConverter<MessageHop> {
  public override MessageHop? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
    if (reader.TokenType != JsonTokenType.StartObject) {
      throw new JsonException("Expected start of object for MessageHop");
    }

    // Read entire object as JsonElement first to handle property name variations
    using var doc = JsonDocument.ParseValue(ref reader);
    var root = doc.RootElement;

    // ServiceInstance is required - get type info only for required types
    var serviceInstanceTypeInfo = (JsonTypeInfo<ServiceInstanceInfo>)options.GetTypeInfo(typeof(ServiceInstanceInfo));

    JsonElement siElem;
    if (!root.TryGetProperty("si", out siElem) && !root.TryGetProperty("ServiceInstance", out siElem)) {
      throw new JsonException("Missing required property: ServiceInstance (or si)");
    }
    var serviceInstance = siElem.Deserialize(serviceInstanceTypeInfo)
      ?? throw new JsonException("Failed to deserialize ServiceInstance");

    // Type (optional, defaults to Current)
    var hopType = HopType.Current;
    if (root.TryGetProperty("ty", out var tyElem) || root.TryGetProperty("Type", out tyElem)) {
      hopType = (HopType)tyElem.GetInt32();
    }

    // CausationId (optional) - deserialize using UUID7 format directly
    MessageId? causationId = null;
    if ((root.TryGetProperty("ca", out var caElem) || root.TryGetProperty("CausationId", out caElem)) &&
        caElem.ValueKind != JsonValueKind.Null) {
      var uuid7String = caElem.GetString()!;
      var uuid7 = Uuid7.Parse(uuid7String, System.Globalization.CultureInfo.InvariantCulture);
      causationId = MessageId.From(uuid7.ToGuid());
    }

    // CorrelationId (optional) - deserialize using UUID7 format directly
    CorrelationId? correlationId = null;
    if ((root.TryGetProperty("co", out var coElem) || root.TryGetProperty("CorrelationId", out coElem)) &&
        coElem.ValueKind != JsonValueKind.Null) {
      var uuid7String = coElem.GetString()!;
      var uuid7 = Uuid7.Parse(uuid7String, System.Globalization.CultureInfo.InvariantCulture);
      correlationId = CorrelationId.From(uuid7.ToGuid());
    }

    // CausationType (optional)
    string? causationType = null;
    if (root.TryGetProperty("ct", out var ctElem) || root.TryGetProperty("CausationType", out ctElem)) {
      causationType = ctElem.GetString();
    }

    // Timestamp (optional, defaults to now)
    var timestamp = DateTimeOffset.UtcNow;
    if (root.TryGetProperty("ts", out var tsElem) || root.TryGetProperty("Timestamp", out tsElem)) {
      timestamp = tsElem.GetDateTimeOffset();
    }

    // Topic (optional, defaults to empty)
    string topic = string.Empty;
    if (root.TryGetProperty("to", out var toElem) || root.TryGetProperty("Topic", out toElem)) {
      topic = toElem.GetString() ?? string.Empty;
    }

    // StreamId (optional, defaults to empty)
    string streamId = string.Empty;
    if (root.TryGetProperty("st", out var stElem) || root.TryGetProperty("StreamId", out stElem)) {
      streamId = stElem.GetString() ?? string.Empty;
    }

    // PartitionIndex (optional)
    int? partitionIndex = null;
    if ((root.TryGetProperty("pi", out var piElem) || root.TryGetProperty("PartitionIndex", out piElem)) &&
        piElem.ValueKind != JsonValueKind.Null) {
      partitionIndex = piElem.GetInt32();
    }

    // SequenceNumber (optional)
    long? sequenceNumber = null;
    if ((root.TryGetProperty("sn", out var snElem) || root.TryGetProperty("SequenceNumber", out snElem)) &&
        snElem.ValueKind != JsonValueKind.Null) {
      sequenceNumber = snElem.GetInt64();
    }

    // ExecutionStrategy (optional, defaults to empty)
    string executionStrategy = string.Empty;
    if (root.TryGetProperty("es", out var esElem) || root.TryGetProperty("ExecutionStrategy", out esElem)) {
      executionStrategy = esElem.GetString() ?? string.Empty;
    }

    // Scope (optional) - only get type info if property exists
    ScopeDelta? scope = null;
    if ((root.TryGetProperty("sc", out var scElem) || root.TryGetProperty("Scope", out scElem)) &&
        scElem.ValueKind != JsonValueKind.Null) {
      var scopeDeltaTypeInfo = (JsonTypeInfo<ScopeDelta>)options.GetTypeInfo(typeof(ScopeDelta));
      scope = scElem.Deserialize(scopeDeltaTypeInfo);
    }

    // Metadata (optional) - only get type info if property exists
    IReadOnlyDictionary<string, JsonElement>? metadata = null;
    if ((root.TryGetProperty("md", out var mdElem) || root.TryGetProperty("Metadata", out mdElem)) &&
        mdElem.ValueKind != JsonValueKind.Null) {
      var metadataTypeInfo = (JsonTypeInfo<Dictionary<string, JsonElement>>)options.GetTypeInfo(typeof(Dictionary<string, JsonElement>));
      metadata = mdElem.Deserialize(metadataTypeInfo);
    }

    // Trail (optional) - only get type info if property exists
    PolicyDecisionTrail? trail = null;
    if ((root.TryGetProperty("tr", out var trElem) || root.TryGetProperty("Trail", out trElem)) &&
        trElem.ValueKind != JsonValueKind.Null) {
      var trailTypeInfo = (JsonTypeInfo<PolicyDecisionTrail>)options.GetTypeInfo(typeof(PolicyDecisionTrail));
      trail = trElem.Deserialize(trailTypeInfo);
    }

    // CallerMemberName (optional)
    string? callerMemberName = null;
    if (root.TryGetProperty("cm", out var cmElem) || root.TryGetProperty("CallerMemberName", out cmElem)) {
      callerMemberName = cmElem.GetString();
    }

    // CallerFilePath (optional)
    string? callerFilePath = null;
    if (root.TryGetProperty("cf", out var cfElem) || root.TryGetProperty("CallerFilePath", out cfElem)) {
      callerFilePath = cfElem.GetString();
    }

    // CallerLineNumber (optional)
    int? callerLineNumber = null;
    if ((root.TryGetProperty("cl", out var clElem) || root.TryGetProperty("CallerLineNumber", out clElem)) &&
        clElem.ValueKind != JsonValueKind.Null) {
      callerLineNumber = clElem.GetInt32();
    }

    // Duration (optional)
    TimeSpan duration = default;
    if (root.TryGetProperty("du", out var duElem) || root.TryGetProperty("Duration", out duElem)) {
      duration = TimeSpan.Parse(duElem.GetString() ?? "00:00:00", System.Globalization.CultureInfo.InvariantCulture);
    }

    // TraceParent (optional)
    string? traceParent = null;
    if (root.TryGetProperty("tp", out var tpElem) || root.TryGetProperty("TraceParent", out tpElem)) {
      traceParent = tpElem.GetString();
    }

    return new MessageHop {
      Type = hopType,
      CausationId = causationId,
      CorrelationId = correlationId,
      CausationType = causationType,
      ServiceInstance = serviceInstance,
      Timestamp = timestamp,
      Topic = topic,
      StreamId = streamId,
      PartitionIndex = partitionIndex,
      SequenceNumber = sequenceNumber,
      ExecutionStrategy = executionStrategy,
      Scope = scope,
      Metadata = metadata,
      Trail = trail,
      CallerMemberName = callerMemberName,
      CallerFilePath = callerFilePath,
      CallerLineNumber = callerLineNumber,
      Duration = duration,
      TraceParent = traceParent
    };
  }

  public override void Write(Utf8JsonWriter writer, MessageHop value, JsonSerializerOptions options) {
    // Get type info for ServiceInstance (always required)
    var serviceInstanceTypeInfo = (JsonTypeInfo<ServiceInstanceInfo>)options.GetTypeInfo(typeof(ServiceInstanceInfo));

    writer.WriteStartObject();

    // Type (only if not default)
    if (value.Type != HopType.Current) {
      writer.WriteNumber("ty", (int)value.Type);
    }

    // CausationId (only if not null) - serialize using UUID7 format directly
    if (value.CausationId is not null) {
      var uuid7 = new Uuid7(value.CausationId.Value.Value);
      writer.WriteString("ca", uuid7.ToString());
    }

    // CorrelationId (only if not null) - serialize using UUID7 format directly
    if (value.CorrelationId is not null) {
      var uuid7 = new Uuid7(value.CorrelationId.Value.Value);
      writer.WriteString("co", uuid7.ToString());
    }

    // CausationType (only if not null)
    if (value.CausationType is not null) {
      writer.WriteString("ct", value.CausationType);
    }

    // ServiceInstance (required)
    writer.WritePropertyName("si");
    JsonSerializer.Serialize(writer, value.ServiceInstance, serviceInstanceTypeInfo);

    // Timestamp
    writer.WriteString("ts", value.Timestamp);

    // Topic (only if not empty)
    if (!string.IsNullOrEmpty(value.Topic)) {
      writer.WriteString("to", value.Topic);
    }

    // StreamId (only if not empty)
    if (!string.IsNullOrEmpty(value.StreamId)) {
      writer.WriteString("st", value.StreamId);
    }

    // PartitionIndex (only if not null)
    if (value.PartitionIndex is not null) {
      writer.WriteNumber("pi", value.PartitionIndex.Value);
    }

    // SequenceNumber (only if not null)
    if (value.SequenceNumber is not null) {
      writer.WriteNumber("sn", value.SequenceNumber.Value);
    }

    // ExecutionStrategy (only if not empty)
    if (!string.IsNullOrEmpty(value.ExecutionStrategy)) {
      writer.WriteString("es", value.ExecutionStrategy);
    }

    // Scope (only if not null)
    if (value.Scope is not null) {
      var scopeDeltaTypeInfo = (JsonTypeInfo<ScopeDelta>)options.GetTypeInfo(typeof(ScopeDelta));
      writer.WritePropertyName("sc");
      JsonSerializer.Serialize(writer, value.Scope, scopeDeltaTypeInfo);
    }

    // Metadata (only if not null)
    // Note: Use Dictionary<string, JsonElement> type info since IReadOnlyDictionary is an interface
    // and source generators have limited support for interface serialization
    if (value.Metadata is not null) {
      writer.WritePropertyName("md");
      writer.WriteStartObject();
      foreach (var kvp in value.Metadata) {
        writer.WritePropertyName(kvp.Key);
        kvp.Value.WriteTo(writer);
      }
      writer.WriteEndObject();
    }

    // Trail (only if not null)
    if (value.Trail is not null) {
      var trailTypeInfo = (JsonTypeInfo<PolicyDecisionTrail>)options.GetTypeInfo(typeof(PolicyDecisionTrail));
      writer.WritePropertyName("tr");
      JsonSerializer.Serialize(writer, value.Trail, trailTypeInfo);
    }

    // CallerMemberName (only if not null)
    if (value.CallerMemberName is not null) {
      writer.WriteString("cm", value.CallerMemberName);
    }

    // CallerFilePath (only if not null)
    if (value.CallerFilePath is not null) {
      writer.WriteString("cf", value.CallerFilePath);
    }

    // CallerLineNumber (only if not null)
    if (value.CallerLineNumber is not null) {
      writer.WriteNumber("cl", value.CallerLineNumber.Value);
    }

    // Duration (only if not default)
    if (value.Duration != default) {
      writer.WriteString("du", value.Duration.ToString());
    }

    // TraceParent (only if not null)
    if (value.TraceParent is not null) {
      writer.WriteString("tp", value.TraceParent);
    }

    writer.WriteEndObject();
  }
}
