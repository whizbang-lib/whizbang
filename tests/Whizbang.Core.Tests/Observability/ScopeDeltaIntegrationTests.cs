using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Integration tests for ScopeDelta with MessageEnvelope and MessageHop.
/// Tests full delta-based scope propagation across message hops.
/// </summary>
/// <tests>Whizbang.Core/Security/ScopeDelta.cs</tests>
/// <tests>Whizbang.Core/Observability/MessageEnvelope.cs</tests>
[Category("Integration")]
[Category("ScopeDelta")]
public class ScopeDeltaIntegrationTests {
  #region Envelope Delta Merging Tests

  [Test]
  public async Task Envelope_MultipleHops_MergesDeltasCorrectlyAsync() {
    // Arrange - Create envelope with multiple hops that add roles incrementally
    var serviceInstance = _createServiceInstance();

    // Hop 1: Initial scope with User role
    var hop1Scope = _createScopeDelta(
      tenantId: "tenant-1",
      userId: "user-1",
      roles: ["User"]
    );

    // Hop 2: Add Admin role
    var hop2Scope = _createRoleDelta(add: ["Admin"]);

    // Hop 3: Add Manager role, remove User role
    var hop3Scope = _createRoleDelta(add: ["Manager"], remove: ["User"]);

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Data = "test" },
      Hops = [
        new MessageHop { ServiceInstance = serviceInstance, Scope = hop1Scope },
        new MessageHop { ServiceInstance = serviceInstance, Scope = hop2Scope },
        new MessageHop { ServiceInstance = serviceInstance, Scope = hop3Scope }
      ]
    };

    // Act
    var currentScope = envelope.GetCurrentScope();

    // Assert - Final scope should have Admin, Manager but not User
    await Assert.That(currentScope).IsNotNull();
    await Assert.That(currentScope!.Scope.TenantId).IsEqualTo("tenant-1");
    await Assert.That(currentScope.Scope.UserId).IsEqualTo("user-1");
    await Assert.That(currentScope.Roles).Contains("Admin");
    await Assert.That(currentScope.Roles).Contains("Manager");
    await Assert.That(currentScope.Roles).DoesNotContain("User");
  }

  [Test]
  public async Task Envelope_IdenticalScopes_NoScopeOnSubsequentHopsAsync() {
    // Arrange - Same scope context at each hop
    var serviceInstance = _createServiceInstance();
    var scope = _createTestScopeContext("tenant-1", "user-1", ["Admin"]);

    // Act - Create delta for first hop (should have full scope)
    var delta1 = ScopeDelta.CreateDelta(null, scope);

    // Create delta for second hop (should be null - nothing changed)
    var delta2 = ScopeDelta.CreateDelta(scope, scope);

    // Assert
    await Assert.That(delta1).IsNotNull();
    await Assert.That(delta1!.HasChanges).IsTrue();
    await Assert.That(delta2).IsNull(); // No delta needed - scopes are identical
  }

  [Test]
  public async Task Envelope_GetCurrentScope_RebuildsFullContextAsync() {
    // Arrange - Create envelope with progressive scope changes
    var serviceInstance = _createServiceInstance();

    // Hop 1: Initial context with full scope
    var initialScope = _createScopeDelta(
      tenantId: "tenant-1",
      userId: "user-1",
      roles: ["User"],
      permissions: [Permission.Read("orders")],
      principals: [SecurityPrincipalId.User("user-1")]
    );

    // Hop 2: Add permissions
    var hop2Delta = _createPermissionDelta(add: [Permission.Write("orders")]);

    // Hop 3: Add group membership
    var hop3Delta = _createPrincipalDelta(add: [SecurityPrincipalId.Group("sales-team")]);

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Data = "test" },
      Hops = [
        new MessageHop { ServiceInstance = serviceInstance, Scope = initialScope },
        new MessageHop { ServiceInstance = serviceInstance, Scope = hop2Delta },
        new MessageHop { ServiceInstance = serviceInstance, Scope = hop3Delta }
      ]
    };

    // Act
    var currentScope = envelope.GetCurrentScope();

    // Assert - Full context rebuilt with all accumulated changes
    await Assert.That(currentScope).IsNotNull();
    await Assert.That(currentScope!.Scope.TenantId).IsEqualTo("tenant-1");
    await Assert.That(currentScope.Scope.UserId).IsEqualTo("user-1");
    await Assert.That(currentScope.Roles).Contains("User");
    await Assert.That(currentScope.Permissions).Contains(Permission.Read("orders"));
    await Assert.That(currentScope.Permissions).Contains(Permission.Write("orders"));
    await Assert.That(currentScope.SecurityPrincipals).Contains(SecurityPrincipalId.User("user-1"));
    await Assert.That(currentScope.SecurityPrincipals).Contains(SecurityPrincipalId.Group("sales-team"));
  }

  [Test]
  public async Task Envelope_HopsWithNullScope_InheritFromPreviousAsync() {
    // Arrange - Some hops have no scope changes
    var serviceInstance = _createServiceInstance();

    var initialScope = _createScopeDelta(
      tenantId: "tenant-1",
      userId: "user-1",
      roles: ["Admin"]
    );

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Data = "test" },
      Hops = [
        new MessageHop { ServiceInstance = serviceInstance, Scope = initialScope },
        new MessageHop { ServiceInstance = serviceInstance, Scope = null }, // No scope changes
        new MessageHop { ServiceInstance = serviceInstance, Scope = null }  // No scope changes
      ]
    };

    // Act
    var currentScope = envelope.GetCurrentScope();

    // Assert - Initial scope preserved
    await Assert.That(currentScope).IsNotNull();
    await Assert.That(currentScope!.Scope.TenantId).IsEqualTo("tenant-1");
    await Assert.That(currentScope.Roles).Contains("Admin");
  }

  [Test]
  public async Task Envelope_CausationHops_ExcludedFromScopeMergingAsync() {
    // Arrange - Mix of current and causation hops
    var serviceInstance = _createServiceInstance();

    var currentScope = _createScopeDelta(
      tenantId: "current-tenant",
      userId: "current-user",
      roles: ["CurrentRole"]
    );

    var causationScope = _createScopeDelta(
      tenantId: "causation-tenant",
      userId: "causation-user",
      roles: ["CausationRole"]
    );

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Data = "test" },
      Hops = [
        // Causation hop should be ignored
        new MessageHop { Type = HopType.Causation, ServiceInstance = serviceInstance, Scope = causationScope },
        // Current hop should be used
        new MessageHop { Type = HopType.Current, ServiceInstance = serviceInstance, Scope = currentScope }
      ]
    };

    // Act
    var scope = envelope.GetCurrentScope();

    // Assert - Only current hop's scope is used
    await Assert.That(scope).IsNotNull();
    await Assert.That(scope!.Scope.TenantId).IsEqualTo("current-tenant");
    await Assert.That(scope.Roles).Contains("CurrentRole");
    await Assert.That(scope.Roles).DoesNotContain("CausationRole");
  }

  [Test]
  public async Task Envelope_NoHopsWithScope_ReturnsNullAsync() {
    // Arrange - No scope on any hops
    var serviceInstance = _createServiceInstance();

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Data = "test" },
      Hops = [
        new MessageHop { ServiceInstance = serviceInstance, Scope = null },
        new MessageHop { ServiceInstance = serviceInstance, Scope = null }
      ]
    };

    // Act
    var currentScope = envelope.GetCurrentScope();

    // Assert
    await Assert.That(currentScope).IsNull();
  }

  #endregion

  #region Serialization Round-Trip Tests

  [Test]
  public async Task Envelope_WithScopeDeltas_SerializesAndDeserializesCorrectlyAsync() {
    // Arrange
    var serviceInstance = _createServiceInstance();
    var scopeDelta = _createScopeDelta(
      tenantId: "tenant-1",
      userId: "user-1",
      roles: ["Admin", "User"]
    );

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Data = "test-data" },
      Hops = [
        new MessageHop { ServiceInstance = serviceInstance, Scope = scopeDelta }
      ]
    };

    // Act
    var json = JsonSerializer.Serialize(envelope);
    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<TestMessage>>(json);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Hops).Count().IsEqualTo(1);
    await Assert.That(deserialized.Hops[0].Scope).IsNotNull();
    await Assert.That(deserialized.Hops[0].Scope!.HasChanges).IsTrue();

    var scope = deserialized.GetCurrentScope();
    await Assert.That(scope).IsNotNull();
    await Assert.That(scope!.Scope.TenantId).IsEqualTo("tenant-1");
    await Assert.That(scope.Roles).Contains("Admin");
  }

  [Test]
  public async Task Envelope_SerializationUsesShortPropertyNamesAsync() {
    // Arrange
    var serviceInstance = _createServiceInstance();
    var scopeDelta = _createScopeDelta(tenantId: "t1", userId: "u1", roles: ["R"]);

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Data = "test" },
      Hops = [
        new MessageHop { ServiceInstance = serviceInstance, Scope = scopeDelta }
      ]
    };

    // Act
    var json = JsonSerializer.Serialize(envelope);

    // Assert - Uses short property names on properties (not dictionary enum keys)
    await Assert.That(json).Contains("\"sc\":"); // Scope property uses short name
    await Assert.That(json).Contains("\"v\":"); // Values property uses short name
    // Note: Enum dictionary keys (like ScopeProp.Scope) are serialized as strings by default
    await Assert.That(json).DoesNotContain("\"Values\":");  // Property name is "v" not "Values"
    await Assert.That(json).DoesNotContain("\"Collections\":"); // Property name is "c" not "Collections"
  }

  [Test]
  public async Task Envelope_NullScopeDeltas_NotSerializedAsync() {
    // Arrange
    var serviceInstance = _createServiceInstance();

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Data = "test" },
      Hops = [
        new MessageHop { ServiceInstance = serviceInstance, Scope = null }
      ]
    };

    // Act
    var json = JsonSerializer.Serialize(envelope);

    // Assert - No "sc" property when scope is null
    await Assert.That(json).DoesNotContain("\"sc\":");
  }

  #endregion

  #region HasPermission After Hop Tests

  [Test]
  public async Task Envelope_GetCurrentScope_CanCallHasPermissionAsync() {
    // Arrange - Create scope with permissions
    var serviceInstance = _createServiceInstance();
    var scopeDelta = _createScopeDelta(
      tenantId: "tenant-1",
      userId: "user-1",
      roles: [],
      permissions: [Permission.Read("orders"), Permission.Write("orders")]
    );

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Data = "test" },
      Hops = [
        new MessageHop { ServiceInstance = serviceInstance, Scope = scopeDelta }
      ]
    };

    // Act
    var currentScope = envelope.GetCurrentScope();

    // Assert - Can use HasPermission on rebuilt scope
    await Assert.That(currentScope).IsNotNull();
    await Assert.That(currentScope!.HasPermission(Permission.Read("orders"))).IsTrue();
    await Assert.That(currentScope.HasPermission(Permission.Write("orders"))).IsTrue();
    await Assert.That(currentScope.HasPermission(Permission.Delete("orders"))).IsFalse();
  }

  [Test]
  public async Task Envelope_GetCurrentScope_CanCallHasRoleAsync() {
    // Arrange
    var serviceInstance = _createServiceInstance();
    var scopeDelta = _createScopeDelta(
      tenantId: "tenant-1",
      userId: "user-1",
      roles: ["Admin", "User"]
    );

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Data = "test" },
      Hops = [
        new MessageHop { ServiceInstance = serviceInstance, Scope = scopeDelta }
      ]
    };

    // Act
    var currentScope = envelope.GetCurrentScope();

    // Assert
    await Assert.That(currentScope).IsNotNull();
    await Assert.That(currentScope!.HasRole("Admin")).IsTrue();
    await Assert.That(currentScope.HasAnyRole("Admin", "Manager")).IsTrue();
    await Assert.That(currentScope.HasRole("SuperAdmin")).IsFalse();
  }

  [Test]
  public async Task Envelope_GetCurrentScope_CanCallIsMemberOfAsync() {
    // Arrange
    var serviceInstance = _createServiceInstance();
    var scopeDelta = _createScopeDelta(
      tenantId: "tenant-1",
      userId: "user-1",
      principals: [
        SecurityPrincipalId.User("user-1"),
        SecurityPrincipalId.Group("sales-team")
      ]
    );

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Data = "test" },
      Hops = [
        new MessageHop { ServiceInstance = serviceInstance, Scope = scopeDelta }
      ]
    };

    // Act
    var currentScope = envelope.GetCurrentScope();

    // Assert
    await Assert.That(currentScope).IsNotNull();
    await Assert.That(currentScope!.IsMemberOfAny(SecurityPrincipalId.Group("sales-team"))).IsTrue();
    await Assert.That(currentScope.IsMemberOfAny(SecurityPrincipalId.Group("support-team"))).IsFalse();
  }

  #endregion

  #region Test Helpers

  private static ServiceInstanceInfo _createServiceInstance() =>
    new() {
      ServiceName = "TestService",
      InstanceId = Guid.NewGuid(),
      HostName = "localhost",
      ProcessId = 1234
    };

  private sealed record TestMessage {
    public required string Data { get; init; }
  }

  private static ScopeDelta _createScopeDelta(
      string? tenantId = null,
      string? userId = null,
      string[]? roles = null,
      Permission[]? permissions = null,
      SecurityPrincipalId[]? principals = null) {
    var values = new Dictionary<ScopeProp, JsonElement>();
    var collections = new Dictionary<ScopeProp, CollectionChanges>();

    // Add PerspectiveScope
    if (tenantId != null || userId != null) {
      var scopeObj = new { t = tenantId, u = userId };
      values[ScopeProp.Scope] = JsonSerializer.SerializeToElement(scopeObj);
    }

    // Add roles as Set operation
    if (roles != null && roles.Length > 0) {
      var rolesElement = JsonSerializer.SerializeToElement(roles);
      collections[ScopeProp.Roles] = new CollectionChanges { Set = rolesElement };
    }

    // Add permissions as Set operation
    if (permissions != null && permissions.Length > 0) {
      var permsArray = permissions.Select(p => p.Value).ToArray();
      var permsElement = JsonSerializer.SerializeToElement(permsArray);
      collections[ScopeProp.Perms] = new CollectionChanges { Set = permsElement };
    }

    // Add principals as Set operation
    if (principals != null && principals.Length > 0) {
      var principalsArray = principals.Select(p => p.Value).ToArray();
      var principalsElement = JsonSerializer.SerializeToElement(principalsArray);
      collections[ScopeProp.Principals] = new CollectionChanges { Set = principalsElement };
    }

    return new ScopeDelta {
      Values = values.Count > 0 ? values : null,
      Collections = collections.Count > 0 ? collections : null
    };
  }

  private static ScopeDelta _createRoleDelta(string[]? add = null, string[]? remove = null) {
    var changes = new CollectionChanges {
      Add = add != null ? JsonSerializer.SerializeToElement(add) : null,
      Remove = remove != null ? JsonSerializer.SerializeToElement(remove) : null
    };

    return new ScopeDelta {
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Roles] = changes
      }
    };
  }

  private static ScopeDelta _createPermissionDelta(Permission[]? add = null, Permission[]? remove = null) {
    var changes = new CollectionChanges {
      Add = add != null ? JsonSerializer.SerializeToElement(add.Select(p => p.Value).ToArray()) : null,
      Remove = remove != null ? JsonSerializer.SerializeToElement(remove.Select(p => p.Value).ToArray()) : null
    };

    return new ScopeDelta {
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Perms] = changes
      }
    };
  }

  private static ScopeDelta _createPrincipalDelta(SecurityPrincipalId[]? add = null, SecurityPrincipalId[]? remove = null) {
    var changes = new CollectionChanges {
      Add = add != null ? JsonSerializer.SerializeToElement(add.Select(p => p.Value).ToArray()) : null,
      Remove = remove != null ? JsonSerializer.SerializeToElement(remove.Select(p => p.Value).ToArray()) : null
    };

    return new ScopeDelta {
      Collections = new Dictionary<ScopeProp, CollectionChanges> {
        [ScopeProp.Principals] = changes
      }
    };
  }

  private static ScopeContext _createTestScopeContext(
      string? tenantId,
      string? userId,
      string[] roles) =>
    new() {
      Scope = new PerspectiveScope { TenantId = tenantId, UserId = userId },
      Roles = roles.ToHashSet(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

  #endregion

  #region FromSecurityContext Round-Trip Tests

  [Test]
  public async Task FromSecurityContext_WithSystemUserId_SerializesAndDeserializesCorrectlyAsync() {
    // Arrange - This simulates what AsSystem().PublishAsync() does
    var securityContext = new SecurityContext {
      UserId = "SYSTEM",
      TenantId = null
    };
    var scopeDelta = ScopeDelta.FromSecurityContext(securityContext);

    // Act - Serialize to JSON (simulate outbox storage)
    var json = JsonSerializer.Serialize(scopeDelta);
    System.Diagnostics.Debug.WriteLine($"Serialized ScopeDelta: {json}");

    // Deserialize from JSON (simulate worker reading)
    var deserialized = JsonSerializer.Deserialize<ScopeDelta>(json);

    // Assert - Verify the structure is preserved
    await Assert.That(scopeDelta).IsNotNull();
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Values).IsNotNull();
    await Assert.That(deserialized.Values!.ContainsKey(ScopeProp.Scope)).IsTrue();

    // Verify the scope can be extracted
    var foundScope = deserialized.Values.TryGetValue(ScopeProp.Scope, out var scopeElement);
    await Assert.That(foundScope).IsTrue();

    var extractedScope = JsonSerializer.Deserialize<PerspectiveScope>(scopeElement.GetRawText());
    await Assert.That(extractedScope).IsNotNull();
    await Assert.That(extractedScope!.UserId).IsEqualTo("SYSTEM");
  }

  [Test]
  public async Task Envelope_WithFromSecurityContext_ExtractorFindsSystemUserAsync() {
    // Arrange - Simulate the full flow from AsSystem().PublishAsync() through outbox
    var serviceInstance = _createServiceInstance();

    // Create scope delta using FromSecurityContext (like PublishToOutboxAsync does)
    var securityContext = new SecurityContext {
      UserId = "SYSTEM",
      TenantId = null
    };
    var scopeDelta = ScopeDelta.FromSecurityContext(securityContext);

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Data = "test" },
      Hops = [
        new MessageHop { ServiceInstance = serviceInstance, Scope = scopeDelta }
      ]
    };

    // Act - Serialize and deserialize (simulate outbox round-trip)
    var json = JsonSerializer.Serialize(envelope);
    System.Diagnostics.Debug.WriteLine($"Serialized envelope: {json}");

    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<TestMessage>>(json);

    // Assert - Verify MessageHopSecurityExtractor can find the scope
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Hops).Count().IsEqualTo(1);
    await Assert.That(deserialized.Hops[0].Scope).IsNotNull();
    await Assert.That(deserialized.Hops[0].Scope!.Values).IsNotNull();

    // This is what MessageHopSecurityExtractor does
    var hop = deserialized.Hops[0];
    var hasScope = hop.Scope?.Values != null &&
                   hop.Scope.Values.TryGetValue(ScopeProp.Scope, out var scopeElement);
    await Assert.That(hasScope).IsTrue();

    // Verify GetCurrentScope works
    var currentScope = deserialized.GetCurrentScope();
    await Assert.That(currentScope).IsNotNull();
    await Assert.That(currentScope!.Scope.UserId).IsEqualTo("SYSTEM");
  }

  [Test]
  public async Task ScopeDelta_EnumKeyDictionarySerialization_RoundTripPreservesKeysAsync() {
    // Arrange - Test dictionary with enum keys directly
    var dict = new Dictionary<ScopeProp, JsonElement> {
      [ScopeProp.Scope] = JsonSerializer.SerializeToElement(new { t = "tenant", u = "user" }),
      [ScopeProp.Type] = JsonSerializer.SerializeToElement(1)
    };

    var scopeDelta = new ScopeDelta { Values = dict };

    // Act
    var json = JsonSerializer.Serialize(scopeDelta);
    System.Diagnostics.Debug.WriteLine($"Serialized: {json}");
    var deserialized = JsonSerializer.Deserialize<ScopeDelta>(json);

    // Assert - Verify enum keys are preserved after round-trip
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Values).IsNotNull();
    await Assert.That(deserialized.Values!.Count).IsEqualTo(2);
    await Assert.That(deserialized.Values.ContainsKey(ScopeProp.Scope)).IsTrue();
    await Assert.That(deserialized.Values.ContainsKey(ScopeProp.Type)).IsTrue();
  }

  #endregion

  #region RED TEST: InfrastructureJsonContext Round-Trip Tests

  /// <summary>
  /// RED TEST: Verifies ScopeDelta round-trips correctly using InfrastructureJsonContext.
  /// This test reproduces the issue where AsSystem() security context is lost after
  /// outbox serialization/deserialization.
  ///
  /// The outbox uses InfrastructureJsonContext for AOT-compatible serialization.
  /// If Dictionary&lt;ScopeProp, JsonElement&gt; doesn't serialize correctly with the
  /// source-generated context, MessageHopSecurityExtractor will fail to find ScopeProp.Scope.
  /// </summary>
  [Test]
  [Category("RED")]
  public async Task ScopeDelta_WithInfrastructureJsonContext_RoundTripPreservesEnumKeysAsync() {
    // Arrange - Create options using InfrastructureJsonContext (like the outbox does)
    var options = new JsonSerializerOptions {
      TypeInfoResolver = InfrastructureJsonContext.Default
    };

    // Create a ScopeDelta with enum dictionary keys (like AsSystem() does)
    var dict = new Dictionary<ScopeProp, JsonElement> {
      [ScopeProp.Scope] = JsonSerializer.SerializeToElement(new { t = "tenant-123", u = "SYSTEM" })
    };

    var scopeDelta = new ScopeDelta { Values = dict };

    // Act - Serialize using InfrastructureJsonContext (simulates outbox write)
    var typeInfo = options.GetTypeInfo(typeof(ScopeDelta));
    await Assert.That(typeInfo).IsNotNull()
      .Because("InfrastructureJsonContext should have type info for ScopeDelta");

    var json = JsonSerializer.Serialize(scopeDelta, typeInfo!);
    System.Diagnostics.Debug.WriteLine($"Serialized with InfrastructureJsonContext: {json}");

    // Deserialize using InfrastructureJsonContext (simulates outbox read)
    var deserialized = JsonSerializer.Deserialize(json, typeInfo!) as ScopeDelta;

    // Assert - Verify enum keys are preserved
    await Assert.That(deserialized).IsNotNull()
      .Because("ScopeDelta should deserialize correctly");
    await Assert.That(deserialized!.Values).IsNotNull()
      .Because("ScopeDelta.Values dictionary should not be null after deserialization");
    await Assert.That(deserialized.Values!.ContainsKey(ScopeProp.Scope)).IsTrue()
      .Because("ScopeProp.Scope key should be preserved after InfrastructureJsonContext round-trip");

    // This is what MessageHopSecurityExtractor does - extract the scope
    var hasScope = deserialized.Values.TryGetValue(ScopeProp.Scope, out var scopeElement);
    await Assert.That(hasScope).IsTrue()
      .Because("TryGetValue(ScopeProp.Scope) should succeed - this is the MessageHopSecurityExtractor check");

    // Verify we can deserialize the scope properties
    await Assert.That(scopeElement.TryGetProperty("u", out var userElement)).IsTrue()
      .Because("UserId (\"u\") should be present in scope");
    await Assert.That(userElement.GetString()).IsEqualTo("SYSTEM")
      .Because("UserId should be 'SYSTEM' (from AsSystem())");
  }

  /// <summary>
  /// RED TEST: Verifies full MessageHop with ScopeDelta round-trips correctly.
  /// This simulates the complete outbox flow where MessageHop.Scope is serialized
  /// and deserialized via InfrastructureJsonContext.
  /// </summary>
  [Test]
  [Category("RED")]
  public async Task MessageHop_WithScopeDelta_InfrastructureContextRoundTripAsync() {
    // Arrange - Create options using InfrastructureJsonContext
    var options = new JsonSerializerOptions {
      TypeInfoResolver = InfrastructureJsonContext.Default
    };

    // Create hop with ScopeDelta (like the dispatcher does for AsSystem())
    var securityContext = new SecurityContext { UserId = "SYSTEM", TenantId = null };
    var scopeDelta = ScopeDelta.FromSecurityContext(securityContext);

    var hop = new MessageHop {
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      Scope = scopeDelta
    };

    // Act - Serialize using InfrastructureJsonContext
    var typeInfo = options.GetTypeInfo(typeof(MessageHop));
    await Assert.That(typeInfo).IsNotNull();

    var json = JsonSerializer.Serialize(hop, typeInfo!);
    System.Diagnostics.Debug.WriteLine($"MessageHop JSON: {json}");

    // Deserialize
    var deserializedHop = JsonSerializer.Deserialize(json, typeInfo!) as MessageHop;

    // Assert - Verify scope is preserved
    await Assert.That(deserializedHop).IsNotNull();
    await Assert.That(deserializedHop!.Scope).IsNotNull()
      .Because("MessageHop.Scope should not be null after deserialization");
    await Assert.That(deserializedHop.Scope!.Values).IsNotNull()
      .Because("ScopeDelta.Values should not be null");
    await Assert.That(deserializedHop.Scope.Values!.ContainsKey(ScopeProp.Scope)).IsTrue()
      .Because("ScopeProp.Scope key should exist after round-trip");

    // Verify extraction works (like MessageHopSecurityExtractor does)
    var foundScope = deserializedHop.Scope.Values.TryGetValue(ScopeProp.Scope, out var scopeElement);
    await Assert.That(foundScope).IsTrue()
      .Because("MessageHopSecurityExtractor uses TryGetValue(ScopeProp.Scope)");

    // Verify we can get UserId = "SYSTEM"
    await Assert.That(scopeElement.TryGetProperty("u", out var userIdElement)).IsTrue();
    await Assert.That(userIdElement.GetString()).IsEqualTo("SYSTEM");
  }

  /// <summary>
  /// RED TEST: Full envelope round-trip through InfrastructureJsonContext.
  /// Tests GetCurrentScope() after deserialization with source-generated context.
  /// </summary>
  [Test]
  [Category("RED")]
  public async Task Envelope_WithAsSystemScope_InfrastructureContextRoundTrip_ExtractsSecurityAsync() {
    // Arrange
    var options = new JsonSerializerOptions {
      TypeInfoResolver = InfrastructureJsonContext.Default
    };

    var serviceInstance = _createServiceInstance();
    var securityContext = new SecurityContext { UserId = "SYSTEM", TenantId = null };
    var scopeDelta = ScopeDelta.FromSecurityContext(securityContext);

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Data = "test" },
      Hops = [
        new MessageHop { ServiceInstance = serviceInstance, Scope = scopeDelta }
      ]
    };

    // Act - Serialize using InfrastructureJsonContext
    // Note: We need to serialize the hops separately since MessageEnvelope<T> requires T registration
    var hopsTypeInfo = options.GetTypeInfo(typeof(List<MessageHop>));
    await Assert.That(hopsTypeInfo).IsNotNull();

    var hopsJson = JsonSerializer.Serialize(envelope.Hops, hopsTypeInfo!);
    System.Diagnostics.Debug.WriteLine($"Hops JSON: {hopsJson}");

    var deserializedHops = JsonSerializer.Deserialize(hopsJson, hopsTypeInfo!) as List<MessageHop>;

    // Create new envelope with deserialized hops
    var reconstructedEnvelope = new MessageEnvelope<TestMessage> {
      MessageId = envelope.MessageId,
      Payload = envelope.Payload,
      Hops = deserializedHops!
    };

    // Assert - GetCurrentScope() should work and return SYSTEM user
    var currentScope = reconstructedEnvelope.GetCurrentScope();
    await Assert.That(currentScope).IsNotNull()
      .Because("GetCurrentScope() should return non-null after deserializing hops with ScopeDelta");
    await Assert.That(currentScope!.Scope.UserId).IsEqualTo("SYSTEM")
      .Because("UserId should be 'SYSTEM' from AsSystem() security context");
  }

  /// <summary>
  /// Verifies that JsonContextRegistry.CreateCombinedOptions() correctly deserializes
  /// ScopeDelta with both TenantId AND UserId from message hops.
  /// This locks-in the JDNext scenario where PostPerspectiveAsync handlers need TenantContext.
  /// </summary>
  [Test]
  public async Task Envelope_WithTenantIdAndUserId_CombinedContextRoundTrip_ExtractsBothAsync() {
    // Arrange - Use CreateCombinedOptions() like Npgsql does
    var options = JsonContextRegistry.CreateCombinedOptions();

    var serviceInstance = _createServiceInstance();

    // Create scope with BOTH TenantId and UserId (like JDNext BFF does)
    var scopeDelta = _createScopeDelta(
      tenantId: "c0ffee00-cafe-f00d-face-feed12345678",
      userId: "925321d2-9635-49e5-abd8-87b43dcf7e19",
      roles: []
    );

    var hop = new MessageHop {
      ServiceInstance = serviceInstance,
      Timestamp = DateTimeOffset.UtcNow,
      Scope = scopeDelta
    };

    // Act - Serialize using combined context (like Npgsql does for JSONB)
    var hopTypeInfo = options.GetTypeInfo(typeof(MessageHop));
    await Assert.That(hopTypeInfo).IsNotNull()
      .Because("Combined options should have type info for MessageHop");

    var json = JsonSerializer.Serialize(hop, hopTypeInfo!);
    System.Diagnostics.Debug.WriteLine($"Serialized hop JSON: {json}");

    // Verify JSON contains short property names
    await Assert.That(json).Contains("\"sc\":")
      .Because("Scope should be serialized with short property name 'sc'");

    // Deserialize using combined context
    var deserializedHop = JsonSerializer.Deserialize(json, hopTypeInfo!) as MessageHop;

    // Assert - Verify scope is preserved
    await Assert.That(deserializedHop).IsNotNull();
    await Assert.That(deserializedHop!.Scope).IsNotNull()
      .Because("MessageHop.Scope should not be null after combined context round-trip");
    await Assert.That(deserializedHop.Scope!.Values).IsNotNull()
      .Because("ScopeDelta.Values should contain the scope");
    await Assert.That(deserializedHop.Scope.Values!.ContainsKey(ScopeProp.Scope)).IsTrue()
      .Because("ScopeProp.Scope key should exist in Values dictionary");

    // Apply delta to get full ScopeContext
    var scopeContext = deserializedHop.Scope.ApplyTo(null);

    // Verify BOTH TenantId and UserId are extracted correctly
    await Assert.That(scopeContext.Scope.TenantId).IsEqualTo("c0ffee00-cafe-f00d-face-feed12345678")
      .Because("TenantId should be preserved after combined context round-trip");
    await Assert.That(scopeContext.Scope.UserId).IsEqualTo("925321d2-9635-49e5-abd8-87b43dcf7e19")
      .Because("UserId should be preserved after combined context round-trip");
  }

  /// <summary>
  /// Verifies that envelope.GetCurrentScope() correctly extracts TenantId and UserId
  /// after hops are deserialized using JsonContextRegistry.CreateCombinedOptions().
  /// This is the exact code path used in PerspectiveRunner when processing events.
  /// </summary>
  [Test]
  public async Task Envelope_GetCurrentScope_WithCombinedContext_ReturnsTenantIdAndUserIdAsync() {
    // Arrange - Use CreateCombinedOptions() like Npgsql does
    var options = JsonContextRegistry.CreateCombinedOptions();

    var serviceInstance = _createServiceInstance();

    // Create scope with BOTH TenantId and UserId
    var scopeDelta = _createScopeDelta(
      tenantId: "test-tenant-id",
      userId: "test-user-id",
      roles: ["Admin", "User"]
    );

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage { Data = "test" },
      Hops = [
        new MessageHop { ServiceInstance = serviceInstance, Scope = scopeDelta }
      ]
    };

    // Act - Serialize hops using combined context
    var hopsTypeInfo = options.GetTypeInfo(typeof(List<MessageHop>));
    var hopsJson = JsonSerializer.Serialize(envelope.Hops, hopsTypeInfo!);
    System.Diagnostics.Debug.WriteLine($"Hops JSON: {hopsJson}");

    // Deserialize hops
    var deserializedHops = JsonSerializer.Deserialize(hopsJson, hopsTypeInfo!) as List<MessageHop>;

    // Create new envelope with deserialized hops
    var reconstructedEnvelope = new MessageEnvelope<TestMessage> {
      MessageId = envelope.MessageId,
      Payload = envelope.Payload,
      Hops = deserializedHops!
    };

    // Assert - GetCurrentScope() should return both TenantId and UserId
    var currentScope = reconstructedEnvelope.GetCurrentScope();

    await Assert.That(currentScope).IsNotNull()
      .Because("GetCurrentScope() should return non-null after combined context round-trip");
    await Assert.That(currentScope!.Scope.TenantId).IsEqualTo("test-tenant-id")
      .Because("TenantId should be extracted from hops - this is used by PostPerspectiveAsync handlers");
    await Assert.That(currentScope.Scope.UserId).IsEqualTo("test-user-id")
      .Because("UserId should be extracted from hops");
    await Assert.That(currentScope.Roles).Contains("Admin")
      .Because("Roles should also be preserved");
    await Assert.That(currentScope.Roles).Contains("User")
      .Because("All roles should be preserved");
  }

  /// <summary>
  /// Verifies that the exact JSON format stored in PostgreSQL (from JDNext)
  /// deserializes correctly using the combined context.
  /// This locks-in the wire format compatibility.
  /// </summary>
  [Test]
  public async Task ScopeDelta_DeserializesPostgresJsonbFormat_WithEnumStringKeysAsync() {
    // Arrange - This is the exact JSON format stored in JDNext PostgreSQL
    // Note: Enum keys are serialized as strings ("Scope") not integers (0)
    var postgresJson = """{"v":{"Scope":{"t":"tenant-abc","u":"user-xyz"}}}""";

    var options = JsonContextRegistry.CreateCombinedOptions();

    // Act - Deserialize using combined context
    var scopeDeltaTypeInfo = options.GetTypeInfo(typeof(ScopeDelta));
    await Assert.That(scopeDeltaTypeInfo).IsNotNull();

    var scopeDelta = JsonSerializer.Deserialize(postgresJson, scopeDeltaTypeInfo!) as ScopeDelta;

    // Assert
    await Assert.That(scopeDelta).IsNotNull()
      .Because("ScopeDelta should deserialize from PostgreSQL JSONB format");
    await Assert.That(scopeDelta!.Values).IsNotNull()
      .Because("Values dictionary should not be null");
    await Assert.That(scopeDelta.Values!.ContainsKey(ScopeProp.Scope)).IsTrue()
      .Because("Enum key 'Scope' should map to ScopeProp.Scope after deserialization");

    // Apply and verify
    var scopeContext = scopeDelta.ApplyTo(null);
    await Assert.That(scopeContext.Scope.TenantId).IsEqualTo("tenant-abc")
      .Because("TenantId ('t') should be extracted from scope");
    await Assert.That(scopeContext.Scope.UserId).IsEqualTo("user-xyz")
      .Because("UserId ('u') should be extracted from scope");
  }

  #endregion
}
