using System.Text.Json;
using Whizbang.Core.Attributes;
using Whizbang.Core.Generated;
using Whizbang.Core.Observability;

namespace Whizbang.Core.AutoPopulate;

/// <summary>
/// Default implementation of <see cref="IAutoPopulateProcessor"/>.
/// Processes auto-populate registrations and stores values in envelope metadata.
/// </summary>
/// <remarks>
/// <para>
/// The processor walks through all auto-populate registrations for a message type
/// and extracts the appropriate values from the current hop's context (timestamps,
/// security context, service info, identifiers). Values are stored in the hop's
/// metadata dictionary with an "auto:" prefix for the property name.
/// </para>
/// <para>
/// Values can later be retrieved via <c>envelope.GetMetadata("auto:PropertyName")</c>
/// or materialized into a new message instance using envelope extension methods.
/// </para>
/// </remarks>
/// <docs>attributes/auto-populate</docs>
/// <tests>tests/Whizbang.Core.Tests/AutoPopulate/AutoPopulateProcessorTests.cs</tests>
public sealed class AutoPopulateProcessor : IAutoPopulateProcessor {
  /// <summary>
  /// Prefix used for auto-populate metadata keys.
  /// </summary>
  internal const string METADATA_PREFIX = "auto:";

  /// <summary>
  /// Processes auto-populate registrations for a message and stores values in envelope metadata.
  /// </summary>
  /// <param name="envelope">The message envelope to populate.</param>
  /// <param name="messageType">The runtime type of the message.</param>
  /// <remarks>
  /// This method looks up all auto-populate registrations for the message type,
  /// extracts values from the current hop's context, and adds a new hop with
  /// the auto-populate values stored as metadata.
  /// </remarks>
  public void ProcessAutoPopulate(IMessageEnvelope envelope, Type messageType) {
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(messageType);

    var registrations = AutoPopulateRegistry.GetRegistrationsFor(messageType).ToList();
    if (registrations.Count == 0) {
      return;
    }

    // Get the current hop for extracting context values
    var currentHop = _getCurrentHop(envelope);
    if (currentHop == null) {
      return;
    }

    // Extract values and build metadata dictionary
    var metadata = new Dictionary<string, JsonElement>();

    foreach (var registration in registrations) {
      var element = _extractValueAsJsonElement(registration, envelope, currentHop);
      if (element.HasValue) {
        var key = $"{METADATA_PREFIX}{registration.PropertyName}";
        metadata[key] = element.Value;
      }
    }

    if (metadata.Count == 0) {
      return;
    }

    // Create a new hop with the auto-populate metadata
    var autoPopulateHop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = currentHop.ServiceInstance,
      Timestamp = currentHop.Timestamp,
      Topic = currentHop.Topic,
      StreamId = currentHop.StreamId,
      SecurityContext = currentHop.SecurityContext,
      CorrelationId = currentHop.CorrelationId,
      Metadata = metadata
    };

    envelope.AddHop(autoPopulateHop);
  }

  /// <summary>
  /// Gets the current (most recent) hop from the envelope.
  /// </summary>
  private static MessageHop? _getCurrentHop(IMessageEnvelope envelope) {
    for (int i = envelope.Hops.Count - 1; i >= 0; i--) {
      if (envelope.Hops[i].Type == HopType.Current) {
        return envelope.Hops[i];
      }
    }
    return null;
  }

  /// <summary>
  /// Extracts the value for a registration and returns it as a JsonElement.
  /// Uses AOT-compatible serialization via InfrastructureJsonContext.
  /// </summary>
  private static JsonElement? _extractValueAsJsonElement(
      AutoPopulateRegistration registration,
      IMessageEnvelope envelope,
      MessageHop hop) {

    return registration.PopulateKind switch {
      PopulateKind.Timestamp => _extractTimestampElement(registration, hop),
      PopulateKind.Context => _extractContextElement(registration, hop),
      PopulateKind.Service => _extractServiceElement(registration, hop),
      PopulateKind.Identifier => _extractIdentifierElement(registration, envelope, hop),
      _ => (JsonElement?)null
    };
  }

  /// <summary>
  /// Extracts a timestamp value as JsonElement.
  /// </summary>
  private static JsonElement? _extractTimestampElement(AutoPopulateRegistration registration, MessageHop hop) {
    return registration.TimestampKind switch {
      TimestampKind.SentAt => _serializeDateTimeOffset(hop.Timestamp),
      TimestampKind.QueuedAt => (JsonElement?)null, // Set later by WorkCoordinatorPublisherWorker
      TimestampKind.DeliveredAt => (JsonElement?)null, // Set later by TransportConsumerWorker
      _ => (JsonElement?)null
    };
  }

  /// <summary>
  /// Extracts a security context value as JsonElement.
  /// </summary>
  private static JsonElement? _extractContextElement(AutoPopulateRegistration registration, MessageHop hop) {
    if (hop.SecurityContext == null) {
      return (JsonElement?)null;
    }

    var value = registration.ContextKind switch {
      ContextKind.UserId => hop.SecurityContext.UserId,
      ContextKind.TenantId => hop.SecurityContext.TenantId,
      _ => (string?)null
    };

    return value != null ? _serializeString(value) : (JsonElement?)null;
  }

  /// <summary>
  /// Extracts a service info value as JsonElement.
  /// </summary>
  private static JsonElement? _extractServiceElement(AutoPopulateRegistration registration, MessageHop hop) {
    return registration.ServiceKind switch {
      ServiceKind.ServiceName => _serializeString(hop.ServiceInstance.ServiceName),
      ServiceKind.InstanceId => _serializeGuid(hop.ServiceInstance.InstanceId),
      ServiceKind.HostName => _serializeString(hop.ServiceInstance.HostName),
      ServiceKind.ProcessId => _serializeInt(hop.ServiceInstance.ProcessId),
      _ => (JsonElement?)null
    };
  }

  /// <summary>
  /// Extracts an identifier value as JsonElement.
  /// </summary>
  private static JsonElement? _extractIdentifierElement(
      AutoPopulateRegistration registration,
      IMessageEnvelope envelope,
      MessageHop hop) {

    return registration.IdentifierKind switch {
      IdentifierKind.MessageId => _serializeGuid(envelope.MessageId.Value),
      IdentifierKind.CorrelationId => hop.CorrelationId != null ? _serializeGuid(hop.CorrelationId.Value.Value) : (JsonElement?)null,
      IdentifierKind.CausationId => hop.CausationId != null ? _serializeGuid(hop.CausationId.Value.Value) : (JsonElement?)null,
      IdentifierKind.StreamId => !string.IsNullOrEmpty(hop.StreamId) ? _serializeString(hop.StreamId) : (JsonElement?)null,
      _ => (JsonElement?)null
    };
  }

  /// <summary>
  /// Serializes a DateTimeOffset to JsonElement using AOT-compatible serialization.
  /// </summary>
  private static JsonElement _serializeDateTimeOffset(DateTimeOffset value) {
    return JsonSerializer.SerializeToElement(value, InfrastructureJsonContext.Default.DateTimeOffset);
  }

  /// <summary>
  /// Serializes a string to JsonElement using AOT-compatible serialization.
  /// </summary>
  private static JsonElement _serializeString(string value) {
    return JsonSerializer.SerializeToElement(value, InfrastructureJsonContext.Default.String);
  }

  /// <summary>
  /// Serializes a Guid to JsonElement using AOT-compatible serialization.
  /// </summary>
  private static JsonElement _serializeGuid(Guid value) {
    return JsonSerializer.SerializeToElement(value, InfrastructureJsonContext.Default.Guid);
  }

  /// <summary>
  /// Serializes an int to JsonElement using AOT-compatible serialization.
  /// </summary>
  private static JsonElement _serializeInt(int value) {
    return JsonSerializer.SerializeToElement(value, InfrastructureJsonContext.Default.Int32);
  }
}
