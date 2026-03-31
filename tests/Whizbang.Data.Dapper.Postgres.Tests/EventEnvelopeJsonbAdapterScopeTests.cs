using System.Text.Json;
using Whizbang.Core.Data;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Testing.Contracts;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Tests for EventEnvelopeJsonbAdapter scope serialization/deserialization.
/// Verifies PerspectiveScope short key format and backward compatibility with legacy snake_case format.
/// </summary>
/// <tests>EventEnvelopeJsonbAdapter</tests>
public class EventEnvelopeJsonbAdapterScopeTests {

  private static EventEnvelopeJsonbAdapter _createAdapter() {
    var jsonOptions = JsonOptionsHelper.CreateOptions();
    return new EventEnvelopeJsonbAdapter(jsonOptions);
  }

  private static MessageEnvelope<TestEvent> _createEnvelopeWithScope(
      string? tenantId = null,
      string? userId = null) {
    var scopeDelta = ScopeDelta.FromSecurityContext(new SecurityContext {
      TenantId = tenantId,
      UserId = userId
    });

    return new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent { StreamId = Guid.NewGuid(), Payload = "test" },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 1
          },
          Topic = "test-topic",
          Timestamp = DateTimeOffset.UtcNow,
          Scope = scopeDelta
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  [Test]
  public async Task ToJsonb_WithScope_SerializesAsPerspectiveScopeShortKeysAsync() {
    // Arrange
    var adapter = _createAdapter();
    var envelope = _createEnvelopeWithScope(tenantId: "tenant-abc", userId: "user-123");

    // Act
    var result = adapter.ToJsonb(envelope);

    // Assert
    await Assert.That(result.ScopeJson).IsNotNull();

    var scopeDoc = JsonDocument.Parse(result.ScopeJson!);
    var root = scopeDoc.RootElement;

    // Should use short keys ("t", "u") not legacy ("tenant_id", "user_id")
    await Assert.That(root.TryGetProperty("t", out var tenantProp)).IsTrue();
    await Assert.That(tenantProp.GetString()).IsEqualTo("tenant-abc");
    await Assert.That(root.TryGetProperty("u", out var userProp)).IsTrue();
    await Assert.That(userProp.GetString()).IsEqualTo("user-123");

    // Should NOT have legacy keys
    await Assert.That(root.TryGetProperty("tenant_id", out _)).IsFalse();
    await Assert.That(root.TryGetProperty("user_id", out _)).IsFalse();
  }

  [Test]
  public async Task ToJsonb_WithNullScope_ReturnsNullScopeJsonAsync() {
    // Arrange
    var adapter = _createAdapter();
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent { StreamId = Guid.NewGuid(), Payload = "test" },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 1
          },
          Topic = "test-topic",
          Timestamp = DateTimeOffset.UtcNow,
          Scope = null
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = adapter.ToJsonb(envelope);

    // Assert
    await Assert.That(result.ScopeJson).IsNull();
  }

  [Test]
  public async Task FromJsonb_WithPerspectiveScopeFormat_RestoresScopeDeltaAsync() {
    // Arrange
    var adapter = _createAdapter();
    var envelope = _createEnvelopeWithScope(tenantId: "tenant-xyz", userId: "user-456");

    // Round-trip: serialize then deserialize
    var jsonb = adapter.ToJsonb(envelope);

    // Act
    var restored = adapter.FromJsonb<TestEvent>(jsonb);

    // Assert
    var scope = restored.GetCurrentScope();
    await Assert.That(scope).IsNotNull();
    await Assert.That(scope!.Scope.TenantId).IsEqualTo("tenant-xyz");
    await Assert.That(scope.Scope.UserId).IsEqualTo("user-456");
  }

  [Test]
  public async Task FromJsonb_WithLegacySnakeCaseFormat_RestoresScopeDeltaAsync() {
    // Arrange - simulate legacy format stored in DB
    var adapter = _createAdapter();
    var envelope = _createEnvelopeWithScope(tenantId: "legacy-tenant", userId: "legacy-user");
    var jsonb = adapter.ToJsonb(envelope);

    // Replace scope with legacy snake_case format
    const string legacyScope = """{"tenant_id":"legacy-tenant","user_id":"legacy-user"}""";
    var legacyJsonb = new JsonbPersistenceModel {
      DataJson = jsonb.DataJson,
      MetadataJson = jsonb.MetadataJson,
      ScopeJson = legacyScope
    };

    // Act
    var restored = adapter.FromJsonb<TestEvent>(legacyJsonb);

    // Assert
    var scope = restored.GetCurrentScope();
    await Assert.That(scope).IsNotNull();
    await Assert.That(scope!.Scope.TenantId).IsEqualTo("legacy-tenant");
    await Assert.That(scope.Scope.UserId).IsEqualTo("legacy-user");
  }

  [Test]
  public async Task ToJsonb_WithTenantOnly_OmitsNullFieldsAsync() {
    // Arrange
    var adapter = _createAdapter();
    var envelope = _createEnvelopeWithScope(tenantId: "tenant-only");

    // Act
    var result = adapter.ToJsonb(envelope);

    // Assert
    await Assert.That(result.ScopeJson).IsNotNull();
    var scopeDoc = JsonDocument.Parse(result.ScopeJson!);
    var root = scopeDoc.RootElement;
    await Assert.That(root.TryGetProperty("t", out var tenantProp)).IsTrue();
    await Assert.That(tenantProp.GetString()).IsEqualTo("tenant-only");
    // "u" should not be present (WhenWritingNull)
    await Assert.That(root.TryGetProperty("u", out _)).IsFalse();
  }

  [Test]
  public async Task RoundTrip_PreservesFullScopeAsync() {
    // Arrange
    var adapter = _createAdapter();
    var envelope = _createEnvelopeWithScope(tenantId: "rt-tenant", userId: "rt-user");

    // Act - round trip
    var jsonb = adapter.ToJsonb(envelope);
    var restored = adapter.FromJsonb<TestEvent>(jsonb);

    // Assert
    var originalScope = envelope.GetCurrentScope();
    var restoredScope = restored.GetCurrentScope();

    await Assert.That(restoredScope).IsNotNull();
    await Assert.That(restoredScope!.Scope.TenantId).IsEqualTo(originalScope!.Scope.TenantId);
    await Assert.That(restoredScope.Scope.UserId).IsEqualTo(originalScope.Scope.UserId);
  }
}
