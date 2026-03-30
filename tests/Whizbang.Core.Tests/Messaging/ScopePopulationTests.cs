using System.Text.Json;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Dispatch;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for scope population on OutboxMessage and InboxMessage records.
/// Verifies that the Scope property carries PerspectiveScope data
/// extracted from envelope hops.
/// </summary>
/// <tests>OutboxMessage,InboxMessage</tests>
public class ScopePopulationTests {

  private static readonly JsonSerializerOptions _jsonOptions = new() {
    TypeInfoResolver = Whizbang.Core.Generated.InfrastructureJsonContext.Default,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
  };

  // Use the SAME combined options path that the real application uses
  private static readonly JsonSerializerOptions _combinedOptions = JsonContextRegistry.CreateCombinedOptions();

  private static PerspectiveScope _createTestScope(
      string? tenantId = null,
      string? userId = null) =>
    new() { TenantId = tenantId, UserId = userId };

  private static MessageEnvelope<JsonElement> _createJsonEnvelope(
      string? tenantId = null,
      string? userId = null) {
    var scopeDelta = ScopeDelta.FromSecurityContext(new SecurityContext {
      TenantId = tenantId,
      UserId = userId
    });

    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { test = true }),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo { ServiceName = "Test", InstanceId = Guid.NewGuid(), HostName = "test-host", ProcessId = 1 },
          Topic = "test",
          Timestamp = DateTimeOffset.UtcNow,
          Scope = scopeDelta
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  // === OutboxMessage Scope Tests ===

  [Test]
  public async Task OutboxMessage_WithScope_StoresPerspectiveScopeAsync() {
    // Arrange
    var scope = _createTestScope("tenant-1", "user-1");
    var envelope = _createJsonEnvelope("tenant-1", "user-1");

    // Act
    var outboxMsg = new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = "test-topic",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata {
        MessageId = envelope.MessageId,
        Hops = envelope.Hops?.ToList() ?? []
      },
      EnvelopeType = "TestEnvelope",
      StreamId = Guid.NewGuid(),
      IsEvent = true,
      Scope = scope,
      MessageType = "TestEvent"
    };

    // Assert
    await Assert.That(outboxMsg.Scope).IsNotNull();
    await Assert.That(outboxMsg.Scope!.TenantId).IsEqualTo("tenant-1");
    await Assert.That(outboxMsg.Scope.UserId).IsEqualTo("user-1");
  }

  [Test]
  public async Task OutboxMessage_WithNullScope_DefaultsToNullAsync() {
    // Arrange
    var envelope = _createJsonEnvelope();

    // Act
    var outboxMsg = new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = "test-topic",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata {
        MessageId = envelope.MessageId,
        Hops = envelope.Hops?.ToList() ?? []
      },
      EnvelopeType = "TestEnvelope",
      StreamId = Guid.NewGuid(),
      IsEvent = false,
      MessageType = "TestCommand"
    };

    // Assert
    await Assert.That(outboxMsg.Scope).IsNull();
  }

  [Test]
  public async Task OutboxMessage_ScopeFromEnvelope_ExtractsCorrectlyAsync() {
    // Arrange - simulates Dispatcher._extractScope pattern
    var envelope = _createJsonEnvelope("extract-tenant", "extract-user");

    // Act - extract scope the same way Dispatcher does
    var scopeContext = envelope.GetCurrentScope();
    var scope = scopeContext?.Scope;

    var outboxMsg = new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = "test-topic",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata {
        MessageId = envelope.MessageId,
        Hops = envelope.Hops?.ToList() ?? []
      },
      EnvelopeType = "TestEnvelope",
      StreamId = Guid.NewGuid(),
      IsEvent = true,
      Scope = scope,
      MessageType = "TestEvent"
    };

    // Assert
    await Assert.That(outboxMsg.Scope).IsNotNull();
    await Assert.That(outboxMsg.Scope!.TenantId).IsEqualTo("extract-tenant");
    await Assert.That(outboxMsg.Scope.UserId).IsEqualTo("extract-user");
  }

  // === InboxMessage Scope Tests ===

  [Test]
  public async Task InboxMessage_WithScope_StoresPerspectiveScopeAsync() {
    // Arrange
    var scope = _createTestScope("inbox-tenant", "inbox-user");
    var envelope = _createJsonEnvelope("inbox-tenant", "inbox-user");

    // Act
    var inboxMsg = new InboxMessage {
      MessageId = envelope.MessageId.Value,
      HandlerName = "TestHandler",
      Envelope = envelope,
      EnvelopeType = "TestEnvelope",
      StreamId = Guid.NewGuid(),
      IsEvent = true,
      Scope = scope,
      MessageType = "TestEvent"
    };

    // Assert
    await Assert.That(inboxMsg.Scope).IsNotNull();
    await Assert.That(inboxMsg.Scope!.TenantId).IsEqualTo("inbox-tenant");
    await Assert.That(inboxMsg.Scope.UserId).IsEqualTo("inbox-user");
  }

  [Test]
  public async Task InboxMessage_WithNullScope_DefaultsToNullAsync() {
    // Arrange
    var envelope = _createJsonEnvelope();

    // Act
    var inboxMsg = new InboxMessage {
      MessageId = envelope.MessageId.Value,
      HandlerName = "TestHandler",
      Envelope = envelope,
      EnvelopeType = "TestEnvelope",
      StreamId = Guid.NewGuid(),
      IsEvent = false,
      MessageType = "TestCommand"
    };

    // Assert
    await Assert.That(inboxMsg.Scope).IsNull();
  }

  [Test]
  public async Task InboxMessage_ScopeFromEnvelope_ExtractsCorrectlyAsync() {
    // Arrange - simulates consumer worker extraction pattern
    var envelope = _createJsonEnvelope("worker-tenant", "worker-user");

    // Act - same extraction pattern as consumer workers
    var scope = envelope.GetCurrentScope()?.Scope;

    var inboxMsg = new InboxMessage {
      MessageId = envelope.MessageId.Value,
      HandlerName = "TestHandler",
      Envelope = envelope,
      EnvelopeType = "TestEnvelope",
      StreamId = Guid.NewGuid(),
      IsEvent = true,
      Scope = scope,
      MessageType = "TestEvent"
    };

    // Assert
    await Assert.That(inboxMsg.Scope).IsNotNull();
    await Assert.That(inboxMsg.Scope!.TenantId).IsEqualTo("worker-tenant");
    await Assert.That(inboxMsg.Scope.UserId).IsEqualTo("worker-user");
  }

  // === JSON Serialization Tests (verifies work coordinator serialization path) ===

  [Test]
  public async Task OutboxMessage_WithScope_SerializesToJsonWithScopePropertyAsync() {
    // Arrange - exact same serialization path as EFCoreWorkCoordinator/DapperWorkCoordinator
    var envelope = _createJsonEnvelope("ser-tenant", "ser-user");
    var scope = envelope.GetCurrentScope()?.Scope;

    var outboxMsg = new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = "test-topic",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata {
        MessageId = envelope.MessageId,
        Hops = envelope.Hops?.ToList() ?? []
      },
      EnvelopeType = "TestEnvelope",
      StreamId = Guid.NewGuid(),
      IsEvent = true,
      Scope = scope,
      MessageType = "TestEvent"
    };

    // Act - serialize exactly as the work coordinators do: OutboxMessage[] via InfrastructureJsonContext
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(OutboxMessage[]));
    var json = JsonSerializer.Serialize(new[] { outboxMsg }, typeInfo!);

    // Assert - the JSON array element must contain "Scope" key with PerspectiveScope short keys
    var doc = JsonDocument.Parse(json);
    var firstElem = doc.RootElement[0];

    await Assert.That(firstElem.TryGetProperty("Scope", out var scopeProp)).IsTrue();
    await Assert.That(scopeProp.TryGetProperty("t", out var tenantProp)).IsTrue();
    await Assert.That(tenantProp.GetString()).IsEqualTo("ser-tenant");
    await Assert.That(scopeProp.TryGetProperty("u", out var userProp)).IsTrue();
    await Assert.That(userProp.GetString()).IsEqualTo("ser-user");
  }

  [Test]
  public async Task OutboxMessage_WithNullScope_OmitsScopeFromJsonAsync() {
    // Arrange - no scope
    var envelope = _createJsonEnvelope();

    var outboxMsg = new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = "test-topic",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata {
        MessageId = envelope.MessageId,
        Hops = envelope.Hops?.ToList() ?? []
      },
      EnvelopeType = "TestEnvelope",
      StreamId = Guid.NewGuid(),
      IsEvent = false,
      MessageType = "TestCommand"
    };

    // Act
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(OutboxMessage[]));
    var json = JsonSerializer.Serialize(new[] { outboxMsg }, typeInfo!);

    // Assert - "Scope" should NOT be present (WhenWritingNull omits it)
    var doc = JsonDocument.Parse(json);
    var firstElem = doc.RootElement[0];
    await Assert.That(firstElem.TryGetProperty("Scope", out _)).IsFalse();
  }

  [Test]
  public async Task InboxMessage_WithScope_SerializesToJsonWithScopePropertyAsync() {
    // Arrange
    var envelope = _createJsonEnvelope("inbox-ser-tenant", "inbox-ser-user");
    var scope = envelope.GetCurrentScope()?.Scope;

    var inboxMsg = new InboxMessage {
      MessageId = envelope.MessageId.Value,
      HandlerName = "TestHandler",
      Envelope = envelope,
      EnvelopeType = "TestEnvelope",
      StreamId = Guid.NewGuid(),
      IsEvent = true,
      Scope = scope,
      MessageType = "TestEvent"
    };

    // Act
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(InboxMessage[]));
    var json = JsonSerializer.Serialize(new[] { inboxMsg }, typeInfo!);

    // Assert
    var doc = JsonDocument.Parse(json);
    var firstElem = doc.RootElement[0];

    await Assert.That(firstElem.TryGetProperty("Scope", out var scopeProp)).IsTrue();
    await Assert.That(scopeProp.TryGetProperty("t", out var tenantProp)).IsTrue();
    await Assert.That(tenantProp.GetString()).IsEqualTo("inbox-ser-tenant");
    await Assert.That(scopeProp.TryGetProperty("u", out var userProp)).IsTrue();
    await Assert.That(userProp.GetString()).IsEqualTo("inbox-ser-user");
  }

  // === SQL Extraction Simulation Tests ===

  [Test]
  public async Task SqlExtraction_ScopePropertyFromSerializedOutboxMessage_MatchesSqlPathAsync() {
    // This test simulates what the SQL function does: elem->'Scope'
    // The SQL extracts the 'Scope' key from the JSONB array element.
    // This verifies the C# serialization produces JSON that the SQL can extract.
    var envelope = _createJsonEnvelope("sql-tenant", "sql-user");
    var scope = envelope.GetCurrentScope()?.Scope;

    var outboxMsg = new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = "test-topic",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata {
        MessageId = envelope.MessageId,
        Hops = envelope.Hops?.ToList() ?? []
      },
      EnvelopeType = "TestEnvelope",
      StreamId = Guid.NewGuid(),
      IsEvent = true,
      Scope = scope,
      MessageType = "TestEvent"
    };

    // Serialize as work coordinator does
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(OutboxMessage[]));
    var json = JsonSerializer.Serialize(new[] { outboxMsg }, typeInfo!);

    // Simulate SQL: elem->'Scope' extracts the Scope property value
    var doc = JsonDocument.Parse(json);
    var elem = doc.RootElement[0];
    var hasScopeProperty = elem.TryGetProperty("Scope", out var scopeValue);

    await Assert.That(hasScopeProperty).IsTrue();

    // The scope value is what SQL stores in wh_outbox.scope column
    var scopeJson = scopeValue.GetRawText();
    await Assert.That(scopeJson).Contains("\"t\":\"sql-tenant\"");
    await Assert.That(scopeJson).Contains("\"u\":\"sql-user\"");

    // Verify it can be deserialized back as PerspectiveScope
    var deserialized = JsonSerializer.Deserialize<PerspectiveScope>(scopeJson, _jsonOptions);
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.TenantId).IsEqualTo("sql-tenant");
    await Assert.That(deserialized.UserId).IsEqualTo("sql-user");
  }

  // === Combined Options Tests (reproduces actual runtime serialization path) ===

  [Test]
  public async Task OutboxMessage_WithCombinedOptions_SerializesScopeWithShortKeysAsync() {
    // This test uses CreateCombinedOptions() - the EXACT same path the real application uses.
    // If this test fails with full property names, the combined resolver is the problem.
    var envelope = _createJsonEnvelope("combined-tenant", "combined-user");
    var scope = envelope.GetCurrentScope()?.Scope;

    var outboxMsg = new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = "test-topic",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata {
        MessageId = envelope.MessageId,
        Hops = envelope.Hops?.ToList() ?? []
      },
      EnvelopeType = "TestEnvelope",
      StreamId = Guid.NewGuid(),
      IsEvent = true,
      Scope = scope,
      MessageType = "TestEvent"
    };

    // Act - use CreateCombinedOptions() exactly as the work coordinator does
    var typeInfo = _combinedOptions.GetTypeInfo(typeof(OutboxMessage[]));
    var json = JsonSerializer.Serialize(new[] { outboxMsg }, typeInfo!);

    // Assert - must use short keys (t, u) not full names (TenantId, UserId)
    var doc = JsonDocument.Parse(json);
    var firstElem = doc.RootElement[0];

    await Assert.That(firstElem.TryGetProperty("Scope", out var scopeProp)).IsTrue();

    // Verify short keys are used (not full property names)
    var scopeJson = scopeProp.GetRawText();
    await Assert.That(scopeJson).Contains("\"t\":");
    await Assert.That(scopeJson).Contains("\"u\":");
    await Assert.That(scopeJson).DoesNotContain("\"TenantId\"");
    await Assert.That(scopeJson).DoesNotContain("\"UserId\"");
    await Assert.That(scopeJson).DoesNotContain("\"AllowedPrincipals\"");
    await Assert.That(scopeJson).DoesNotContain("\"Extensions\"");
  }

  [Test]
  public async Task PerspectiveScope_DirectSerialization_CombinedOptionsUsesShortKeysAsync() {
    // Serialize PerspectiveScope directly with combined options
    var scope = new PerspectiveScope { TenantId = "direct-tenant", UserId = "direct-user" };

    // Diagnostic: which resolver provides PerspectiveScope?
    var combinedOptions = JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = combinedOptions.GetTypeInfo(typeof(PerspectiveScope));
    var json = JsonSerializer.Serialize(scope, typeInfo!);

    // Also test: serialize using JUST InfrastructureJsonContext.Default as resolver
    var infraOnlyOptions = new JsonSerializerOptions {
      TypeInfoResolver = Whizbang.Core.Generated.InfrastructureJsonContext.Default,
      DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    // Add the same converters as combined
    infraOnlyOptions.Converters.Add(new Whizbang.Core.ValueObjects.MessageIdJsonConverter());
    infraOnlyOptions.Converters.Add(new Whizbang.Core.ValueObjects.CorrelationIdJsonConverter());
    infraOnlyOptions.Converters.Add(new Whizbang.Core.Security.SecurityPrincipalIdJsonConverter());
    infraOnlyOptions.Converters.Add(new Whizbang.Core.ValueObjects.TrackedGuidJsonConverter());
    var infraJson = JsonSerializer.Serialize(scope, infraOnlyOptions.GetTypeInfo(typeof(PerspectiveScope))!);

    // This will help diagnose: does adding converters change behavior?
    await Assert.That(infraJson).Contains("\"t\":");

    // The combined options should also use short keys
    await Assert.That(json).Contains("\"t\":");
    await Assert.That(json).DoesNotContain("\"TenantId\"");
  }

  [Test]
  public async Task PerspectiveScope_DirectSerialization_InfrastructureContextUsesShortKeysAsync() {
    // Serialize PerspectiveScope directly with InfrastructureJsonContext only
    var scope = new PerspectiveScope { TenantId = "infra-tenant", UserId = "infra-user" };

    var typeInfo = _jsonOptions.GetTypeInfo(typeof(PerspectiveScope));
    var json = JsonSerializer.Serialize(scope, typeInfo!);

    await Assert.That(json).Contains("\"t\":");
    await Assert.That(json).DoesNotContain("\"TenantId\"");
  }

  // === Scope Extraction Edge Cases ===

  [Test]
  public async Task ScopeExtraction_NoHops_ReturnsNullAsync() {
    // Arrange
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { }),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var scope = envelope.GetCurrentScope()?.Scope;

    // Assert
    await Assert.That(scope).IsNull();
  }

  [Test]
  public async Task ScopeExtraction_HopsWithoutScope_ReturnsNullAsync() {
    // Arrange
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { }),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo { ServiceName = "Test", InstanceId = Guid.NewGuid(), HostName = "test-host", ProcessId = 1 },
          Topic = "test",
          Timestamp = DateTimeOffset.UtcNow,
          Scope = null
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var scope = envelope.GetCurrentScope()?.Scope;

    // Assert
    await Assert.That(scope).IsNull();
  }

  [Test]
  public async Task ScopeExtraction_MultipleHops_LastScopeWinsAsync() {
    // Arrange - second hop replaces scope entirely (ScopeDelta replaces PerspectiveScope per hop)
    var hop1Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "hop1-tenant", UserId = "hop1-user" });
    var hop2Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "hop2-tenant", UserId = "hop2-user" });

    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { }),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo { ServiceName = "Service1", InstanceId = Guid.NewGuid(), HostName = "host1", ProcessId = 1 },
          Topic = "topic1",
          Timestamp = DateTimeOffset.UtcNow,
          Scope = hop1Scope
        },
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo { ServiceName = "Service2", InstanceId = Guid.NewGuid(), HostName = "host2", ProcessId = 2 },
          Topic = "topic2",
          Timestamp = DateTimeOffset.UtcNow,
          Scope = hop2Scope
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var scopeContext = envelope.GetCurrentScope();
    var scope = scopeContext?.Scope;

    // Assert - last hop's scope wins (ScopeDelta replaces entire scope)
    await Assert.That(scope).IsNotNull();
    await Assert.That(scope!.TenantId).IsEqualTo("hop2-tenant");
    await Assert.That(scope.UserId).IsEqualTo("hop2-user");
  }
}
