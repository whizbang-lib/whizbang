# Plan: Change Work Coordinator to Use `IMessageEnvelope<JsonElement>`

**Status**: Draft
**Created**: 2025-12-15
**Target Version**: v0.1.0 (Foundation Release)

---

## Problem Statement

The current `IWorkCoordinator` interface uses `IMessageEnvelope<object>` for envelope properties:
- `OutboxMessage.Envelope: IMessageEnvelope<object>`
- `InboxMessage.Envelope: IMessageEnvelope<object>`
- `OutboxWork.Envelope: IMessageEnvelope<object>`
- `InboxWork.Envelope: IMessageEnvelope<object>`

This causes several issues:

1. **Covariance Problems**: Cannot reliably cast `IMessageEnvelope` (non-generic) or `IMessageEnvelope<TMessage>` to `IMessageEnvelope<object>` after JSON deserialization
2. **AOT Compatibility**: Boxing/unboxing of value types breaks Native AOT compilation
3. **Serialization Issues**: Storing typed payloads (e.g., `MessageEnvelope<CreateProductCommand>`) and deserializing them generically is error-prone
4. **Runtime Type Resolution**: Requires `Type.GetType()` which has AOT warnings (IL2057)

### Current Failure

17 tests failing in `Whizbang.Data.Postgres.Tests` with:
```
InvalidCastException: Unable to cast object of type 'MessageEnvelope`1[System.Text.Json.JsonElement]'
to type 'IMessageEnvelope`1[System.Object]'
```

---

## Proposed Solution

Change all envelope properties to use `IMessageEnvelope<JsonElement>` instead of `IMessageEnvelope<object>`.

### Why `JsonElement`?

1. **Designed for JSON**: `JsonElement` is the native System.Text.Json type for representing JSON data
2. **AOT Compatible**: No reflection or dynamic type resolution needed
3. **Serialization-Friendly**: Directly serializable/deserializable without type information
4. **Type-Safe**: Strong typing while maintaining flexibility
5. **Performance**: No boxing/unboxing, no runtime type resolution
6. **Covariance**: `IMessageEnvelope<JsonElement>` is covariant and works correctly

---

## Implementation Steps

### 1. Update Interface Definitions

**File**: `src/Whizbang.Core/Messaging/IWorkCoordinator.cs`

Change four record types:

#### `OutboxMessage` (lines 153-193)
```csharp
// BEFORE:
public record OutboxMessage {
  public required IMessageEnvelope<object> Envelope { get; init; }
  // ...
}

// AFTER:
public record OutboxMessage {
  public required IMessageEnvelope<JsonElement> Envelope { get; init; }
  // ...
}
```

#### `InboxMessage` (lines 200-240)
```csharp
// BEFORE:
public record InboxMessage {
  public required IMessageEnvelope<object> Envelope { get; init; }
  // ...
}

// AFTER:
public record InboxMessage {
  public required IMessageEnvelope<JsonElement> Envelope { get; init; }
  // ...
}
```

#### `OutboxWork` (lines 322-374)
```csharp
// BEFORE:
public record OutboxWork {
  public required IMessageEnvelope<object> Envelope { get; init; }
  // ...
}

// AFTER:
public record OutboxWork {
  public required IMessageEnvelope<JsonElement> Envelope { get; init; }
  // ...
}
```

#### `InboxWork` (lines 382-424)
```csharp
// BEFORE:
public record InboxWork {
  public required IMessageEnvelope<object> Envelope { get; init; }
  // ...
}

// AFTER:
public record InboxWork {
  public required IMessageEnvelope<JsonElement> Envelope { get; init; }
  // ...
}
```

**Documentation Updates**:
- Update XML comments to reflect `JsonElement` instead of `object`
- Update remarks about heterogeneous collections

---

### 2. Update Dapper Implementation

**File**: `src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.cs`

#### 2a. Update Deserialization (lines 176-219)

```csharp
// CURRENT (lines 176-198):
var outboxWork = resultList
  .Where(r => r.source == "outbox")
  .Select(r => {
    var envelope = DeserializeEnvelope(r.envelope_type, r.envelope_data);
    // Envelope is deserialized as IMessageEnvelope (non-generic)
    // Since we need IMessageEnvelope<object>, we use an unsafe cast via (IMessageEnvelope<object>)(object)envelope
    // This works because the actual runtime type implements IMessageEnvelope<T> which is covariant
    var typedEnvelope = (IMessageEnvelope<object>)(object)envelope;

    return new OutboxWork {
      MessageId = r.msg_id,
      Destination = r.destination!,
      Envelope = typedEnvelope,
      // ...
    };
  })
  .ToList();

// AFTER:
var outboxWork = resultList
  .Where(r => r.source == "outbox")
  .Select(r => {
    var envelope = DeserializeEnvelope(r.envelope_type, r.envelope_data);
    // Safe cast to IMessageEnvelope<JsonElement> - envelope is always deserialized as MessageEnvelope<JsonElement>
    var jsonEnvelope = envelope as IMessageEnvelope<JsonElement>
      ?? throw new InvalidOperationException($"Envelope must be IMessageEnvelope<JsonElement> for message {r.msg_id}");

    return new OutboxWork {
      MessageId = r.msg_id,
      Destination = r.destination!,
      Envelope = jsonEnvelope,
      // ...
    };
  })
  .ToList();
```

Similar change for `inboxWork` (lines 200-219).

#### 2b. Verify Deserialization Method (lines 348-369)

Already updated to deserialize as `MessageEnvelope<JsonElement>` ✓

```csharp
private IMessageEnvelope DeserializeEnvelope(string envelopeTypeName, string envelopeDataJson) {
  // Always deserialize as MessageEnvelope<JsonElement>
  var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
    ?? throw new InvalidOperationException("No JsonTypeInfo found for MessageEnvelope<JsonElement>...");

  var envelope = JsonSerializer.Deserialize(envelopeDataJson, typeInfo) as IMessageEnvelope
    ?? throw new InvalidOperationException($"Failed to deserialize envelope as MessageEnvelope<JsonElement>");

  return envelope;
}
```

---

### 3. Update EFCore Implementation

**File**: `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs`

Apply same changes as Dapper:

#### 3a. Update Deserialization (lines ~199-236)

```csharp
// BEFORE:
var typedEnvelope = envelope as IMessageEnvelope<object>
  ?? throw new InvalidOperationException($"Envelope must implement IMessageEnvelope<object> for message {r.MessageId}");

// AFTER:
var jsonEnvelope = envelope as IMessageEnvelope<JsonElement>
  ?? throw new InvalidOperationException($"Envelope must be IMessageEnvelope<JsonElement> for message {r.MessageId}");
```

#### 3b. Update Deserialization Method (lines ~392-414)

```csharp
// BEFORE:
var envelopeType = Type.GetType(envelopeTypeName)
  ?? throw new InvalidOperationException($"Could not resolve envelope type '{envelopeTypeName}'");

var typeInfo = _jsonOptions.GetTypeInfo(envelopeType)
  ?? throw new InvalidOperationException($"No JsonTypeInfo found for envelope type '{envelopeTypeName}'...");

// AFTER:
// Always deserialize as MessageEnvelope<JsonElement>
var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
  ?? throw new InvalidOperationException("No JsonTypeInfo found for MessageEnvelope<JsonElement>...");
```

---

### 4. Update Test Helpers

#### 4a. Dapper Tests

**File**: `tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs`

##### Update `CreateTestEnvelope` (current line ~1046)

```csharp
// BEFORE:
private static IMessageEnvelope<object> CreateTestEnvelope(Guid messageId) {
  var envelope = new MessageEnvelope<TestEvent> {
    MessageId = MessageId.From(messageId),
    Payload = new TestEvent(),
    Hops = []
  };
  return envelope as IMessageEnvelope<object>
    ?? throw new InvalidOperationException("Envelope must implement IMessageEnvelope<object>");
}

// AFTER:
private static IMessageEnvelope<JsonElement> CreateTestEnvelope(Guid messageId) {
  // Create envelope with JsonElement payload (empty object for testing)
  var payload = JsonDocument.Parse("{}").RootElement;

  var envelope = new MessageEnvelope<JsonElement> {
    MessageId = MessageId.From(messageId),
    Payload = payload,
    Hops = []
  };

  return envelope;
}
```

##### Update Test Assertions

All tests that create `OutboxMessage` or `InboxMessage` need updates:

```csharp
// BEFORE (line ~841):
var newOutboxMessage = new OutboxMessage {
  MessageId = messageId,
  Destination = "test-topic",
  Envelope = CreateTestEnvelope(messageId),  // Returns IMessageEnvelope<object>
  EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
  // ...
};

// AFTER:
var newOutboxMessage = new OutboxMessage {
  MessageId = messageId,
  Destination = "test-topic",
  Envelope = CreateTestEnvelope(messageId),  // Returns IMessageEnvelope<JsonElement>
  EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
  // ...
};
```

Tests that access `envelope.Payload` will need to handle `JsonElement`:

```csharp
// BEFORE:
var payload = work.Envelope.Payload as TestEvent;
Assert.That(payload).IsNotNull();

// AFTER:
var payload = work.Envelope.Payload;  // This is now JsonElement
Assert.That(payload.ValueKind).IsEqualTo(JsonValueKind.Object);
```

#### 4b. EFCore Tests

**File**: `tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs`

Apply same changes as Dapper tests.

---

### 5. Update Sample Projects

#### ECommerce Sample

**Files to update**:
- `samples/ECommerce/InventoryWorker/Program.cs`
- `samples/ECommerce/BFF.API/Program.cs`
- Any code creating `OutboxMessage` or `InboxMessage`

**Pattern**:
```csharp
// BEFORE:
var message = new OutboxMessage {
  Envelope = new MessageEnvelope<CreateProductCommand> {
    Payload = command,
    // ...
  },
  // ...
};

// AFTER:
// Serialize command to JsonElement
var commandJson = JsonSerializer.SerializeToElement(command, jsonOptions);

var message = new OutboxMessage {
  Envelope = new MessageEnvelope<JsonElement> {
    Payload = commandJson,
    // ...
  },
  // ...
};
```

---

## Benefits

### Performance
- ✅ **No boxing/unboxing**: JsonElement is a struct but used correctly
- ✅ **No runtime type resolution**: No `Type.GetType()` calls
- ✅ **Direct deserialization**: Single deserialization pass

### AOT Compatibility
- ✅ **No IL2057 warnings**: No dynamic type loading
- ✅ **No reflection**: All types known at compile time
- ✅ **Trimming-safe**: No runtime type discovery

### Correctness
- ✅ **Type-safe**: Compile-time verification
- ✅ **Covariance works**: `IMessageEnvelope<JsonElement>` is properly covariant
- ✅ **Serialization-friendly**: JsonElement is designed for this

### Maintainability
- ✅ **Clear intent**: JsonElement signals "this is serialized data"
- ✅ **Consistent**: Same pattern across all implementations
- ✅ **Testable**: Easy to create test envelopes

---

## Migration Path

### For v0.1.0 (Foundation Release)
This change should be made **before** the v0.1.0 release since:
1. No public API users yet (pre-release)
2. Fixes critical AOT compatibility issue
3. Establishes correct pattern from the start

### Breaking Changes
This is a **breaking change** for:
- Any code creating `OutboxMessage` or `InboxMessage`
- Any code accessing `OutboxWork.Envelope.Payload` or `InboxWork.Envelope.Payload`

**Mitigation**: Since we're pre-v1.0, document in release notes and update all samples.

---

## Testing Strategy

### Unit Tests
1. **Interface Tests**: Verify all record types accept `IMessageEnvelope<JsonElement>`
2. **Serialization Tests**: Round-trip serialize/deserialize envelopes
3. **Covariance Tests**: Verify casting from `MessageEnvelope<JsonElement>` to `IMessageEnvelope<JsonElement>` works

### Integration Tests
1. **Dapper Tests**: All 487 tests in `Whizbang.Data.Postgres.Tests` must pass
2. **EFCore Tests**: All tests in `Whizbang.Data.EFCore.Postgres.Tests` must pass
3. **Sample Tests**: ECommerce integration tests must pass

### Expected Results
- ✅ All 17 current failures should be resolved
- ✅ No new failures introduced
- ✅ AOT build warnings eliminated

---

## Implementation Checklist

- [ ] 1. Update `IWorkCoordinator.cs` interface definitions
- [ ] 2. Update `DapperWorkCoordinator.cs` implementation
- [ ] 3. Update `EFCoreWorkCoordinator.cs` implementation
- [ ] 4. Update `DapperWorkCoordinatorTests.cs` test helpers
- [ ] 5. Update `EFCoreWorkCoordinatorTests.cs` test helpers
- [ ] 6. Run all Dapper tests - verify 0 failures
- [ ] 7. Run all EFCore tests - verify 0 failures
- [ ] 8. Update ECommerce sample (if applicable)
- [ ] 9. Run ECommerce integration tests
- [ ] 10. Update documentation (XML comments)
- [ ] 11. Run `dotnet format` on all changed files
- [ ] 12. Commit changes with descriptive message

---

## Rollback Plan

If issues arise:
1. Revert interface changes in `IWorkCoordinator.cs`
2. Revert implementation changes in `DapperWorkCoordinator.cs` and `EFCoreWorkCoordinator.cs`
3. Revert test changes
4. Re-run tests to verify rollback

**Git strategy**: Create feature branch `feature/jsonelement-envelopes` before starting.

---

## Related Issues

- **Current Failures**: 17 tests failing with `InvalidCastException`
- **AOT Warning**: IL2057 in `DapperWorkCoordinator.cs:356` for `Type.GetType()`
- **Design Issue**: Storing typed envelopes but deserializing generically

---

## Questions to Resolve

1. **JsonDocument disposal**: Do we need to manage `JsonDocument` lifetime for `JsonElement.Clone()`?
   - **Answer**: Clone JsonElement when creating envelopes to avoid disposal issues

2. **Payload access in consumers**: How do consumers deserialize `JsonElement` back to typed messages?
   - **Answer**: Use `JsonSerializer.Deserialize<T>(envelope.Payload, jsonOptions)` when needed

3. **Message type tracking**: Do we still need `MessageType` property if payload is JsonElement?
   - **Answer**: YES - still needed for routing, logging, and typed deserialization

---

## Timeline

**Estimated Effort**: 4-6 hours
- Interface changes: 30 mins
- Implementation changes: 1 hour
- Test updates: 2-3 hours
- Testing and verification: 1 hour
- Documentation: 30 mins

**Target Completion**: Before v0.1.0 release

---

## References

- `src/Whizbang.Core/Messaging/IWorkCoordinator.cs` - Interface definitions
- `src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.cs` - Dapper implementation
- `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs` - EFCore implementation
- `tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs` - Dapper tests
- System.Text.Json documentation - JsonElement API

---

## Notes

- This change aligns with .NET best practices for AOT-compatible JSON handling
- JsonElement is the recommended type for dynamic JSON data in System.Text.Json
- Follows the pattern used in ASP.NET Core for JsonPatch and similar scenarios
